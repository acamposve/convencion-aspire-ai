using System;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// The retention sweeper is part of the worker lifecycle: it must not sweep while the worker is
/// disabled, and must keep its startup and periodic behaviour while enabled.
/// </summary>
/// <remarks>
/// These tests drive the real <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> lifecycle
/// through StartAsync/ExecuteTask/StopAsync. The periodic pass is advanced by an explicit signal
/// (<c>DelayAsync</c>) instead of waiting for the configured interval, so nothing depends on
/// wall-clock timing.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class RetentionSweeperLifecycleTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private Guid _companyId;
    private Guid _userId;
    private Guid _documentId;

    public async Task InitializeAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        _companyId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        _documentId = Guid.NewGuid();

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{_companyId}', 'Sweeper lifecycle {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{_userId}', 'sweeper-{suffix}@example.test', 'Sweeper');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id, deleted_at) " +
            $"values ('{_documentId}', 'sweeper/{suffix}.pdf', 'sweeper.pdf', 'application/pdf', 10, '{_userId}', '{_companyId}', now());" +
            $"insert into platform.processing_run (document_id, company_id) values ('{_documentId}', '{_companyId}');" +
            $"insert into platform.document_text (document_id, content, char_count, extracted_at) values ('{_documentId}', 'texto', 5, now());");
    }

    public async Task DisposeAsync()
    {
        // The sweeper may remove runs; the test may also add a second document for the same user.
        // Delete by company so cleanup does not depend on what each test created.
        await ExecuteAsync($"delete from platform.processing_run where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.document where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.user_account where id = '{_userId}';");
        await ExecuteAsync($"delete from app.company where id = '{_companyId}';");
    }

    [Fact]
    public async Task Disabled_sweeper_completes_without_sweeping_and_schedules_nothing()
    {
        // Interval of one minute: if the flag were ignored the loop would sweep now and then wait.
        var sweeper = Create(new WorkerOptions { Enabled = false }, intervalMinutes: 1);

        await sweeper.StartAsync(CancellationToken.None);

        var execute = sweeper.ExecuteTask.ShouldNotBeNull();
        await execute.WaitAsync(WaitTimeout);
        execute.IsCompletedSuccessfully.ShouldBeTrue();

        (await CountRunsAsync()).ShouldBe("1");
        (await CountTextAsync()).ShouldBe("1");

        await sweeper.StopAsync(CancellationToken.None);
        (await CountRunsAsync()).ShouldBe("1");
    }

    [Fact]
    public async Task Disabled_sweeper_does_not_sweep_during_shutdown()
    {
        var sweeper = Create(new WorkerOptions { Enabled = false }, intervalMinutes: 1);

        await sweeper.StartAsync(CancellationToken.None);
        await sweeper.StopAsync(CancellationToken.None);

        (await CountRunsAsync()).ShouldBe("1");
        (await CountTextAsync()).ShouldBe("1");
    }

    [Fact]
    public async Task Enabled_sweeper_sweeps_at_startup()
    {
        var sweeper = Create(new WorkerOptions { Enabled = true }, intervalMinutes: 360);

        await sweeper.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () => await CountRunsAsync() == "0");
            (await CountTextAsync()).ShouldBe("0");
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Enabled_sweeper_sweeps_again_after_the_interval()
    {
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var sweeper = Create(new WorkerOptions { Enabled = true }, intervalMinutes: 1);
        sweeper.DelayAsync = async (_, cancellationToken) =>
        {
            if (releaseDelay.Task.IsCompleted)
            {
                // The interval was advanced once; park the loop until shutdown.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            delayEntered.TrySetResult();
            await releaseDelay.Task.WaitAsync(cancellationToken);
        };

        await sweeper.StartAsync(CancellationToken.None);
        try
        {
            // Startup pass.
            await WaitUntilAsync(async () => await CountRunsAsync() == "0");

            var second = await InsertDeletedDocumentAsync();

            // The loop has finished the first pass and is waiting for the interval.
            await delayEntered.Task.WaitAsync(WaitTimeout);

            // Advance the interval explicitly; the second pass must now purge the new document.
            releaseDelay.TrySetResult();
            await WaitUntilAsync(async () => await CountRunsForDocumentAsync(second) == "0");
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Default_configuration_keeps_retention_disabled()
    {
        // The deployment writes no Worker__Enabled override for production defaults; the factory must
        // keep Enabled false, and that must be enough to stop the sweep.
        var options = WorkerOptionsFactory.From(new ConfigurationBuilder().Build());
        options.Enabled.ShouldBeFalse();

        var sweeper = Create(options, intervalMinutes: 1);
        await sweeper.StartAsync(CancellationToken.None);
        await sweeper.ExecuteTask.ShouldNotBeNull().WaitAsync(WaitTimeout);

        (await CountRunsAsync()).ShouldBe("1");
        (await CountTextAsync()).ShouldBe("1");

        await sweeper.StopAsync(CancellationToken.None);
    }

    private ProcessingRetentionSweeper Create(WorkerOptions workerOptions, int intervalMinutes) =>
        new(
            fixture.ConnectionString,
            new RetentionOptions { IntervalMinutes = intervalMinutes, BatchSize = 100 },
            workerOptions,
            NullLogger<ProcessingRetentionSweeper>.Instance);

    private Task<string> CountRunsAsync() =>
        ScalarAsync($"select count(*) from platform.processing_run where document_id = '{_documentId}'");

    private Task<string> CountTextAsync() =>
        ScalarAsync($"select count(*) from platform.document_text where document_id = '{_documentId}'");

    private Task<string> CountRunsForDocumentAsync(Guid documentId) =>
        ScalarAsync($"select count(*) from platform.processing_run where document_id = '{documentId}'");

    private async Task<Guid> InsertDeletedDocumentAsync()
    {
        var documentId = Guid.NewGuid();
        var suffix = documentId.ToString("N");
        await ExecuteAsync(
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id, deleted_at) " +
            $"values ('{documentId}', 'sweeper/{suffix}.pdf', 'second.pdf', 'application/pdf', 10, '{_userId}', '{_companyId}', now());" +
            $"insert into platform.processing_run (document_id, company_id) values ('{documentId}', '{_companyId}');");
        return documentId;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The expected condition was not observed within the wait timeout.");
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
