using System;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P2-003: the deadline, fixed before the attempt starts, is what cancels a blocked stage.
/// </summary>
/// <remarks>
/// The previous version of this test let a two-second attempt timeout fire and only then moved
/// <see cref="WorkerOptions.DeadlineUtc"/> into the past. The cancellation therefore came from the
/// attempt timeout; the deadline clamp could be deleted and the test still observed
/// <c>deadline_reached</c>. Here the deadline is fixed before <c>StartAsync</c> and the attempt
/// timeout is two orders of magnitude longer, so the only thing that can cancel the stage inside the
/// barrier window is the deadline. If the clamp (or the deadline branch that records the code) is
/// removed, the stage stays blocked until the barrier timeout and the test fails.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class WorkerDeadlineLifecycleTests(PostgresFixture fixture) : IAsyncLifetime
{
    /// <summary>Head start for the worker to start and claim the run.</summary>
    private static readonly TimeSpan DeadlineLead = TimeSpan.FromSeconds(5);

    /// <summary>How long the test waits for each externally observable step before declaring a failure.</summary>
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Longer than <see cref="DeadlineLead"/> by two orders of magnitude: the attempt timeout cannot be the cause.</summary>
    private const int AttemptTimeoutSeconds = 600;

    private Guid _companyId;
    private Guid _userId;
    private Guid _documentId;
    private Guid _runId;

    public async Task InitializeAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        _companyId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        _documentId = Guid.NewGuid();
        _runId = Guid.NewGuid();

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{_companyId}', 'Deadline lifecycle {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{_userId}', 'deadline-{suffix}@example.test', 'Deadline');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{_documentId}', 'deadline/{suffix}.pdf', 'deadline.pdf', 'application/pdf', 1024, '{_userId}', '{_companyId}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values ('{_runId}', '{_documentId}', '{_companyId}');");
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"delete from platform.processing_run where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.document where id = '{_documentId}';");
        await ExecuteAsync($"delete from platform.user_account where id = '{_userId}';");
        await ExecuteAsync($"delete from app.company where id = '{_companyId}';");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_deadline_fixed_before_the_stage_cancels_it_and_fails_the_attempt_permanently(int repetition)
    {
        // Fixed before the worker starts and never mutated: the timeout the worker schedules is
        // derived from this value, so the cancellation cannot be the attempt timeout in disguise.
        var deadline = DateTimeOffset.UtcNow.Add(DeadlineLead);

        var options = new WorkerOptions
        {
            Enabled = true,
            DeadlineUtc = deadline,
            AllowedRunId = _runId,
            PollIntervalSeconds = 1,
            LeaseSeconds = 60,
            AttemptTimeoutSeconds = AttemptTimeoutSeconds,
            MaxAttempts = 3,
            BackoffSeconds = [0],
            HealthFile = Path.Combine(Path.GetTempPath(), $"worker-{Guid.NewGuid():N}"),
        };
        var queue = new ProcessingQueue(fixture.ConnectionString, options);
        var stage = new BlockingStage();
        var worker = new ProcessingWorker(
            queue,
            stage,
            new WorkerHeartbeat(fixture.ConnectionString),
            options,
            NullLogger<ProcessingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var job = await stage.Started.Task.WaitAsync(BarrierTimeout);
            job.RunId.ShouldBe(_runId, $"repetition {repetition}: the worker claimed exactly this run");
            job.Attempt.ShouldBe(1, $"repetition {repetition}: the first attempt holds the stage");
            job.AttemptId.ShouldNotBe(Guid.Empty);

            // The stage is blocked. Wait for the cancellation itself instead of sleeping through a
            // guessed window: the token firing is the evidence that the deadline cancelled the stage.
            await stage.Cancelled.Task.WaitAsync(BarrierTimeout);
            stage.CancelledAt.ShouldNotBeNull();
            stage.CancelledAt!.Value.ShouldBeGreaterThanOrEqualTo(
                deadline.AddMilliseconds(-500),
                $"repetition {repetition}: the stage was cancelled when the fixed deadline expired");

            var run = await WaitForTerminalAsync(repetition);
            run.Status.ShouldBe("Failed", $"repetition {repetition}: a deadline failure is terminal");
            run.ErrorCode.ShouldBe("deadline_reached");
            run.Attempts.ShouldBe(1, "a deadline failure is not retried");
            run.FinishedAt.ShouldNotBeNull($"repetition {repetition}: finished_at is recorded for a terminal run");
            // Two seconds of tolerance cover PostgreSQL and process clock skew; the attempt timeout
            // is 600 s, so this still pins the finish to the deadline rather than to the timeout.
            run.FinishedAt!.Value.ShouldBeGreaterThanOrEqualTo(
                deadline.AddSeconds(-2),
                "the run finished no earlier than the deadline that cancelled it");
            run.FinishedAt.Value.ShouldBeLessThan(
                deadline.AddSeconds(30),
                "the run finished when the deadline expired, not after a long wait");

            var attempt = await ReadAttemptAsync();
            attempt.Count.ShouldBe(1, $"repetition {repetition}: exactly one attempt exists");
            attempt.Id.ShouldBe(job.AttemptId, "the closed attempt is the one that held the stage");
            attempt.Status.ShouldBe("Failed");
            attempt.ErrorCode.ShouldBe("deadline_reached");
            attempt.FinishedAt.ShouldNotBeNull("the attempt is closed with a deadline code, not left Running");

            (await CountAuditAsync("processing.failed"))
                .ShouldBe(1L, $"repetition {repetition}: the failure is audited once");
            (await CountAuditAsync("processing.retried"))
                .ShouldBe(0L, "a deadline failure never schedules a retry");
        }
        finally
        {
            await worker.StopAsync(new CancellationTokenSource(BarrierTimeout).Token);
        }
    }

    private sealed class BlockingStage : IProcessingStage
    {
        public TaskCompletionSource<ProcessingJob> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset? CancelledAt { get; private set; }

        public async Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
        {
            Started.TrySetResult(job);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancelledAt = DateTimeOffset.UtcNow;
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed record RunState(string Status, string ErrorCode, int Attempts, DateTimeOffset? FinishedAt);

    private sealed record AttemptState(int Count, Guid Id, string Status, string ErrorCode, DateTimeOffset? FinishedAt);

    private async Task<RunState> WaitForTerminalAsync(int repetition)
    {
        var giveUpAt = DateTimeOffset.UtcNow + BarrierTimeout;
        while (DateTimeOffset.UtcNow < giveUpAt)
        {
            var run = await ReadRunAsync();
            if (run.Status is "Failed" or "Completed" or "Cancelled")
            {
                return run;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException(
            $"repetition {repetition}: run '{_runId}' did not reach a terminal state within {BarrierTimeout}.");
    }

    private async Task<RunState> ReadRunAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select status::text, coalesce(error_code, ''), attempts, finished_at " +
            "from platform.processing_run where id = @id;",
            connection);
        command.Parameters.AddWithValue("id", _runId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"Run '{_runId}' was not found.");
        }

        return new RunState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }

    private async Task<AttemptState> ReadAttemptAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) over (), id, status::text, coalesce(error_code, ''), finished_at " +
            "from platform.processing_attempt where run_id = @id order by attempt_no;",
            connection);
        command.Parameters.AddWithValue("id", _runId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return new AttemptState(0, Guid.Empty, string.Empty, string.Empty, null);
        }

        return new AttemptState(
            (int)reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }

    private async Task<long> CountAuditAsync(string action)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from platform.audit_entry " +
            "where entity_type = 'processing_run' and entity_id = @id and action = @action;",
            connection);
        command.Parameters.AddWithValue("id", _runId.ToString());
        command.Parameters.AddWithValue("action", action);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
