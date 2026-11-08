using System;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class WorkerDeadlineLifecycleTests(PostgresFixture fixture) : IAsyncLifetime
{
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

    [Fact]
    public async Task A_stage_that_runs_past_the_deadline_is_failed_permanently()
    {
        var options = new WorkerOptions
        {
            Enabled = true,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            PollIntervalSeconds = 1,
            LeaseSeconds = 60,
            AttemptTimeoutSeconds = 2,
            MaxAttempts = 3,
            BackoffSeconds = [0],
            HealthFile = Path.Combine(Path.GetTempPath(), $"worker-{Guid.NewGuid():N}"),
        };
        var queue = new ProcessingQueue(fixture.ConnectionString, options);
        var stage = new BlockingStage();
        var worker = new ProcessingWorker(
            queue,
            stage,
            options,
            NullLogger<ProcessingWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await stage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The attempt has actually started. Expire the worker deadline while the stage is
            // blocked; the attempt timeout then exercises the deadline-failure path deterministically.
            options.DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1);

            var failed = false;
            for (var attempt = 0; attempt < 20 && !failed; attempt++)
            {
                await Task.Delay(500);
                failed = await ScalarAsync(
                    $"select status::text from platform.processing_run where id = '{_runId}'") == "Failed";
            }

            failed.ShouldBeTrue();
            var code = await ScalarAsync($"select error_code from platform.processing_run where id = '{_runId}'");
            code.ShouldBe("deadline_reached");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class BlockingStage : IProcessingStage
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(ProcessingJob job, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
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
