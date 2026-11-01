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
    WorkerOptions options,
    ILogger<ProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WriteHealthFile();

        try
        {
            var recovered = await queue.RecoverAsync(stoppingToken).ConfigureAwait(false);
            if (recovered > 0)
            {
                logger.LogWarning("Recovered {Runs} interrupted processing run(s).", recovered);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not recover interrupted runs at startup.");
        }

        if (!options.Enabled)
        {
            logger.LogInformation("Worker is disabled (Worker:Enabled=false); the container stays healthy.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!options.Enabled)
            {
                await DelayAsync(options.PollIntervalSeconds, stoppingToken).ConfigureAwait(false);
                continue;
            }

            ProcessingJob? job = null;
            try
            {
                job = await queue.ClaimAsync(stoppingToken).ConfigureAwait(false);
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

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds));

                try
                {
                    await stage.ExecuteAsync(job, timeout.Token).ConfigureAwait(false);
                    await queue.CompleteAsync(job, stoppingToken).ConfigureAwait(false);
                    logger.LogInformation("Run {RunId} completed.", job.RunId);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    await queue.FailAsync(job, "timeout", "The attempt exceeded its timeout.", retryable: true, stoppingToken)
                        .ConfigureAwait(false);
                    logger.LogWarning("Run {RunId} timed out.", job.RunId);
                }
                catch (PermanentProcessingException exception)
                {
                    await queue.FailAsync(job, exception.Code, exception.Message, retryable: false, stoppingToken)
                        .ConfigureAwait(false);
                    logger.LogWarning("Run {RunId} failed permanently: {Code}.", job.RunId, exception.Code);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await queue.FailAsync(job, "unexpected_error", "The stage failed unexpectedly.", retryable: true, stoppingToken)
                        .ConfigureAwait(false);
                    logger.LogError(exception, "Run {RunId} failed and was scheduled for retry.", job.RunId);
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

    private static Task DelayAsync(int seconds, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), cancellationToken);
}
