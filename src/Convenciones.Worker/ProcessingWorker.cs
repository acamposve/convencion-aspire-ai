using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Convenciones.Worker;

/// <summary>
/// Claims one run at a time, executes the injected stage, and persists the terminal state.
/// </summary>
/// <remarks>
/// Claiming is a single UPDATE with <c>FOR UPDATE SKIP LOCKED</c>, so several workers can run without
/// a broker. A run whose worker dies is reclaimed by <see cref="ProcessingQueue.RecoverAsync"/> when
/// its lease expires. Logs carry ids only; document text never reaches them.
/// </remarks>
public sealed class ProcessingWorker(
    ProcessingQueue queue,
    IProcessingStage stage,
    WorkerHeartbeat heartbeat,
    WorkerOptions options,
    ILogger<ProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WriteHealthFile();
        await TouchHeartbeatAsync(stoppingToken).ConfigureAwait(false);

        await TryRecoverAsync("at startup", stoppingToken).ConfigureAwait(false);
        var lastRecovery = DateTimeOffset.UtcNow;

        if (!options.Enabled)
        {
            logger.LogInformation("Worker is disabled (Worker:Enabled=false); the container stays healthy.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await TouchHeartbeatAsync(stoppingToken).ConfigureAwait(false);

            if (!options.Enabled)
            {
                await DelayAsync(options.PollIntervalSeconds, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (options.DeadlineUtc is { } deadline && DateTimeOffset.UtcNow >= deadline)
            {
                logger.LogWarning(
                    "Worker deadline {Deadline:O} reached; no new jobs will be claimed.",
                    deadline);
                break;
            }

            // Periodic recovery: an expired lease must be reclaimed during the process lifetime, not
            // only when the worker restarts. Recovery is a bounded, set-based statement, so running it
            // from several instances is idempotent; the interval keeps that load negligible.
            var now = DateTimeOffset.UtcNow;
            if (now - lastRecovery >= RecoveryInterval)
            {
                lastRecovery = now;
                await TryRecoverAsync("periodically", stoppingToken).ConfigureAwait(false);
            }

            ProcessingJob? job = null;
            try
            {
                job = await queue.ClaimAsync(options.DeadlineUtc, stoppingToken).ConfigureAwait(false);
                if (job is null)
                {
                    await DelayAsync(options.PollIntervalSeconds, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (await queue.IsCancelledAsync(job.RunId, stoppingToken).ConfigureAwait(false))
                {
                    await queue.CancelAsync(job, stoppingToken).ConfigureAwait(false);
                    logger.LogInformation(
                        "Run {RunId} for document {DocumentId} was cancelled before it started.",
                        job.RunId,
                        job.DocumentId);
                    continue;
                }

                logger.LogInformation(
                    "Processing run {RunId} for document {DocumentId}, company {CompanyId}, attempt {Attempt}.",
                    job.RunId,
                    job.DocumentId,
                    job.CompanyId,
                    job.Attempt);

                var attemptTimeout = TimeSpan.FromSeconds(options.AttemptTimeoutSeconds);
                // True when the timeout was shortened by the worker deadline. The timer runs on the
                // monotonic clock while DeadlineUtc is wall-clock time, and the two can differ by a
                // few milliseconds while the wall clock is slewed. Without this flag, a timer that
                // fires just before the deadline would be classified as a retryable attempt timeout
                // and the run would be requeued after the worker had already exited on the deadline.
                var deadlineBoundTimeout = false;
                if (options.DeadlineUtc is { } absoluteDeadline)
                {
                    var remaining = absoluteDeadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        await queue.FailAsync(
                            job,
                            "deadline_reached",
                            "The worker deadline expired before the attempt started.",
                            retryable: false,
                            stoppingToken).ConfigureAwait(false);
                        logger.LogWarning("Run {RunId} was not started: the worker deadline expired.", job.RunId);
                        continue;
                    }

                    if (remaining < attemptTimeout)
                    {
                        attemptTimeout = remaining;
                        deadlineBoundTimeout = true;
                    }
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(attemptTimeout);

                try
                {
                    await stage.ExecuteAsync(job, timeout.Token).ConfigureAwait(false);

                    if (options.DeadlineUtc is { } reached && DateTimeOffset.UtcNow >= reached)
                    {
                        await queue.FailAsync(
                            job,
                            "deadline_reached",
                            "The worker deadline expired while the attempt was running.",
                            retryable: false,
                            stoppingToken).ConfigureAwait(false);
                        logger.LogWarning("Run {RunId} finished after the worker deadline; it is marked failed.", job.RunId);
                        continue;
                    }

                    await queue.CompleteAsync(job, stoppingToken).ConfigureAwait(false);
                    logger.LogInformation("Run {RunId} completed.", job.RunId);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The timeout was derived from the deadline, so this cancellation is the
                    // deadline's even when wall-clock time has not quite reached it yet.
                    if (WorkerDeadlinePolicy.IsDeadlineFailure(
                            deadlineBoundTimeout, options.DeadlineUtc, DateTimeOffset.UtcNow))
                    {
                        await queue.FailAsync(
                            job,
                            "deadline_reached",
                            "The worker deadline cancelled the in-flight attempt.",
                            retryable: false,
                            stoppingToken).ConfigureAwait(false);
                        logger.LogWarning("Run {RunId} was cancelled by the worker deadline.", job.RunId);
                    }
                    else
                    {
                        await queue.FailAsync(job, "timeout", "The attempt exceeded its timeout.", retryable: true, stoppingToken)
                            .ConfigureAwait(false);
                        logger.LogWarning("Run {RunId} timed out.", job.RunId);
                    }
                }
                catch (StaleProcessingAttemptException exception)
                {
                    logger.LogWarning(
                        "Run {RunId} attempt {AttemptId} is stale; persistence was rejected without changing the run.",
                        exception.RunId,
                        exception.AttemptId);
                }
                catch (DocumentDeletedException exception)
                {
                    await queue.CancelAsync(
                            job,
                            exception.Code,
                            exception.Message,
                            stoppingToken)
                        .ConfigureAwait(false);
                    logger.LogWarning(
                        "Run {RunId} stopped because document {DocumentId} was deleted.",
                        job.RunId,
                        job.DocumentId);
                }
                catch (PermanentProcessingException exception)
                {
                    if (options.DeadlineUtc is { } expiredAt && DateTimeOffset.UtcNow >= expiredAt)
                    {
                        await queue.FailAsync(
                            job,
                            "deadline_reached",
                            "The worker deadline expired while the attempt was running.",
                            retryable: false,
                            stoppingToken).ConfigureAwait(false);
                        logger.LogWarning("Run {RunId} failed after the worker deadline.", job.RunId);
                    }
                    else
                    {
                        await queue.FailAsync(job, exception.Code, exception.Message, retryable: false, stoppingToken)
                            .ConfigureAwait(false);
                        logger.LogWarning("Run {RunId} failed permanently: {Code}.", job.RunId, exception.Code);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (options.DeadlineUtc is { } expiredAt && DateTimeOffset.UtcNow >= expiredAt)
                    {
                        await queue.FailAsync(
                            job,
                            "deadline_reached",
                            "The worker deadline expired while the attempt was running.",
                            retryable: false,
                            stoppingToken).ConfigureAwait(false);
                        logger.LogWarning("Run {RunId} failed after the worker deadline.", job.RunId);
                    }
                    else
                    {
                        await queue.FailAsync(job, "unexpected_error", "The stage failed unexpectedly.", retryable: true, stoppingToken)
                            .ConfigureAwait(false);
                        logger.LogError(exception, "Run {RunId} failed and was scheduled for retry.", job.RunId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The worker loop failed while claiming or executing a run.");
                await DelayAsync(options.PollIntervalSeconds, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs one recovery sweep. Failures are logged and swallowed: a transient database problem must
    /// not stop the worker loop, and a shutdown cancellation must not be reported as an error.
    /// </summary>
    private async Task TryRecoverAsync(string when, CancellationToken stoppingToken)
    {
        try
        {
            var recovered = await queue.RecoverAsync(stoppingToken).ConfigureAwait(false);
            if (recovered > 0)
            {
                logger.LogWarning("Recovered {Runs} interrupted processing run(s) {When}.", recovered, when);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: the recovery transaction was rolled back by its disposal.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not recover interrupted runs {When}.", when);
        }
    }

    private async Task TouchHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            await heartbeat.TouchAsync(options.Enabled, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A heartbeat failure must not stop processing; the next poll writes it again.
            logger.LogWarning(exception, "The worker heartbeat could not be written.");
        }
    }

    private void WriteHealthFile()
    {
        try
        {
            File.WriteAllText(options.HealthFile, "ready");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not write the worker health file at {Path}.", options.HealthFile);
        }
    }

    private TimeSpan RecoveryInterval => TimeSpan.FromSeconds(Math.Max(1, options.RecoveryIntervalSeconds));

    private static Task DelayAsync(int seconds, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), cancellationToken);
}
