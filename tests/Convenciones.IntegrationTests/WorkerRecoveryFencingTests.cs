using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Worker;
using Convenciones.Worker.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-002 (recovery during the process lifetime) and P1-003 (progress writes fenced by attempt).
/// </summary>
/// <remarks>
/// These tests assert persisted state, not that a method was called. The recovery tests start a real
/// <see cref="ProcessingWorker"/>, let its lease expire while it runs, and prove the run is reclaimed
/// without a restart; the fencing tests use two real attempts and assert the stale one changes nothing.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class WorkerRecoveryFencingTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<(Guid Company, Guid User, Guid Document)> _created = [];

    private WorkerOptions Options(int recoveryIntervalSeconds = 60) => new()
    {
        Enabled = true,
        LeaseSeconds = 60,
        MaxAttempts = 3,
        BackoffSeconds = [0],
        PollIntervalSeconds = 1,
        RecoveryIntervalSeconds = recoveryIntervalSeconds,
        HealthFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"worker-ready-{Guid.NewGuid():N}"),
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (company, user, document) in _created)
        {
            await ExecuteAsync($"delete from platform.processing_run where company_id = '{company}';");
            await ExecuteAsync($"delete from platform.document where id = '{document}';");
            await ExecuteAsync($"delete from platform.user_account where id = '{user}';");
            await ExecuteAsync($"delete from app.company where id = '{company}';");
        }
    }

    // ---------------------------------------------------------------- P1-002

    [Fact]
    public async Task Recovery_reclaims_an_expired_lease_while_the_worker_is_running()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options());
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();

        var options = Options(recoveryIntervalSeconds: 1);
        using var worker = new ProcessingWorker(queue, new BlockingStage(), options, NullLogger<ProcessingWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);

        try
        {
            // Let the startup recovery run first, so what follows is unambiguously the periodic sweep.
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
                .ShouldBe("Running");
            (await ScalarAsync(
                $"select count(*) from platform.audit_entry where entity_id = '{runId}' and action = 'processing.recovered'"))
                .ShouldBe("0");

            await ExecuteAsync(
                $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' where id = '{runId}';");

            var recovered = await WaitForAsync(async () =>
                await ScalarAsync(
                    $"select count(*) from platform.audit_entry where entity_id = '{runId}' and action = 'processing.recovered'") == "1");

            recovered.ShouldBeTrue("the expired lease must be reclaimed without restarting the worker");
        }
        finally
        {
            await worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        }
    }

    [Fact]
    public async Task Recovery_does_not_touch_a_live_lease()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options());
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();

        using var worker = new ProcessingWorker(
            queue, new BlockingStage(), Options(recoveryIntervalSeconds: 1), NullLogger<ProcessingWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));

            (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
                .ShouldBe("Running");
            (await ScalarAsync(
                $"select count(*) from platform.audit_entry where entity_id = '{runId}' and action = 'processing.recovered'"))
                .ShouldBe("0");
        }
        finally
        {
            await worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        }
    }

    [Fact]
    public async Task Concurrent_recovery_sweeps_reclaim_a_run_once()
    {
        // Two application instances sweeping at the same time must not double-transition the run.
        var (runId, _) = await CreateRunAsync();
        var first = new ProcessingQueue(fixture.ConnectionString, Options());
        var second = new ProcessingQueue(fixture.ConnectionString, Options());
        var job = await first.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();

        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' where id = '{runId}';");

        var sweeps = await Task.WhenAll(
            first.RecoverAsync(CancellationToken.None),
            second.RecoverAsync(CancellationToken.None));

        sweeps.Sum().ShouldBe(1);
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Queued");
        (await ScalarAsync($"select attempts from platform.processing_run where id = '{runId}'"))
            .ShouldBe("1", "recovery requeues without consuming another attempt");
    }

    [Fact]
    public async Task Recovery_ignores_completed_runs()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options());
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await queue.CompleteAsync(job!, CancellationToken.None);

        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' where id = '{runId}';");

        (await queue.RecoverAsync(CancellationToken.None)).ShouldBe(0);
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Completed");
    }

    [Fact]
    public async Task A_cancelled_recovery_sweep_changes_nothing()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options());
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' where id = '{runId}';");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => queue.RecoverAsync(cancelled.Token));
        (await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'"))
            .ShouldBe("Running");
    }

    // ---------------------------------------------------------------- P1-003

    [Fact]
    public async Task The_current_attempt_updates_progress()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options());
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();

        await store.UpdateProgressAsync(job!, "ExtractingClauses", 3, CancellationToken.None);

        var (stage, count) = await ProgressAsync(runId);
        stage.ShouldBe("ExtractingClauses");
        count.ShouldBe(3);
    }

    [Fact]
    public async Task A_previous_attempt_cannot_update_progress_after_the_next_attempt_takes_over()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options());
        var store = new NpgsqlClauseStore(fixture.ConnectionString);

        var first = await queue.ClaimAsync(CancellationToken.None);
        first.ShouldNotBeNull();
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' where id = '{runId}';");
        await queue.RecoverAsync(CancellationToken.None);

        var second = await queue.ClaimAsync(CancellationToken.None);
        second.ShouldNotBeNull();
        second!.Attempt.ShouldBe(2);

        await store.UpdateProgressAsync(second, "ClassifyingClauses", 7, CancellationToken.None);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.UpdateProgressAsync(first!, "ExtractingClauses", 99, CancellationToken.None));

        var (stage, count) = await ProgressAsync(runId);
        stage.ShouldBe("ClassifyingClauses");
        count.ShouldBe(7, "a rejected stale update must not modify persisted progress");
    }

    [Fact]
    public async Task Progress_is_rejected_once_the_run_is_completed()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options());
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await queue.CompleteAsync(job!, CancellationToken.None);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.UpdateProgressAsync(job!, "ExtractingClauses", 1, CancellationToken.None));

        (await ProgressAsync(runId)).Stage.ShouldBe("Completed");
    }

    [Fact]
    public async Task Progress_is_rejected_once_the_run_failed()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options());
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await queue.FailAsync(job!, "permanent", "Permanent failure.", retryable: false, CancellationToken.None);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.UpdateProgressAsync(job!, "ExtractingClauses", 1, CancellationToken.None));

        (await ProgressAsync(runId)).Stage.ShouldBe("Failed");
    }

    [Fact]
    public async Task Concurrent_stale_and_current_updates_keep_the_current_progress()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options());
        var store = new NpgsqlClauseStore(fixture.ConnectionString);

        var first = await queue.ClaimAsync(CancellationToken.None);
        first.ShouldNotBeNull();
        await ExecuteAsync(
            $"update platform.processing_run set lease_expires_at = now() - interval '1 minute' where id = '{runId}';");
        await queue.RecoverAsync(CancellationToken.None);
        var second = await queue.ClaimAsync(CancellationToken.None);
        second.ShouldNotBeNull();

        var current = store.UpdateProgressAsync(second!, "ClassifyingClauses", 5, CancellationToken.None);
        var stale = Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.UpdateProgressAsync(first!, "ExtractingClauses", 99, CancellationToken.None));

        await Task.WhenAll(current, stale);

        var (stage, count) = await ProgressAsync(runId);
        stage.ShouldBe("ClassifyingClauses");
        count.ShouldBe(5);
    }

    [Fact]
    public async Task An_empty_attempt_id_is_rejected_before_touching_the_database()
    {
        var (runId, _) = await CreateRunAsync();
        var store = new NpgsqlClauseStore(fixture.ConnectionString);
        var untracked = new ProcessingJob(runId, Guid.NewGuid(), Guid.NewGuid(), 1);

        await Should.ThrowAsync<StaleProcessingAttemptException>(() =>
            store.UpdateProgressAsync(untracked, "ExtractingClauses", 1, CancellationToken.None));

        (await ProgressAsync(runId)).Count.ShouldBe(0);
    }

    // ---------------------------------------------------------------- helpers

    private sealed class BlockingStage : IProcessingStage
    {
        public async Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    private async Task<(string Stage, int Count)> ProgressAsync(Guid runId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select stage, clause_count from platform.processing_run where id = @run;", connection);
        command.Parameters.AddWithValue("run", runId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private async Task<(Guid RunId, Guid DocumentId)> CreateRunAsync()
    {
        await ExecuteAsync("delete from platform.processing_run;");

        var suffix = Guid.NewGuid().ToString("N");
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{companyId}', 'Recovery test {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{userId}', 'recovery-{suffix}@example.test', 'Recovery Test');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{documentId}', 'recovery/{suffix}.pdf', 'recovery.pdf', 'application/pdf', 1024, '{userId}', '{companyId}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values ('{runId}', '{documentId}', '{companyId}');");

        _created.Add((companyId, userId, documentId));
        return (runId, documentId);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }
}
