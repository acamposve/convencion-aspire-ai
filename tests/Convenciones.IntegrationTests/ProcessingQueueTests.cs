using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Convenciones.Worker;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ProcessingQueueTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<(Guid Company, Guid User, Guid Document)> _created = [];

    private static readonly WorkerOptions Options = new()
    {
        LeaseSeconds = 60,
        MaxAttempts = 3,
        BackoffSeconds = [0],
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

    [Fact]
    public async Task Two_workers_never_claim_the_same_run()
    {
        var (runId, _) = await CreateRunAsync();
        var first = new ProcessingQueue(fixture.ConnectionString, Options);
        var second = new ProcessingQueue(fixture.ConnectionString, Options);

        var claims = await Task.WhenAll(
            first.ClaimAsync(CancellationToken.None),
            second.ClaimAsync(CancellationToken.None));

        claims.Count(claim => claim is not null).ShouldBe(1);
        claims.Single(claim => claim is not null)!.RunId.ShouldBe(runId);
    }

    [Fact]
    public async Task Transient_failure_requeues_and_exhausted_attempts_fail()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var job = await queue.ClaimAsync(CancellationToken.None);
            job.ShouldNotBeNull();
            job!.RunId.ShouldBe(runId);
            job.Attempt.ShouldBe(attempt);

            await queue.FailAsync(job, "provider_unavailable", "Temporary failure.", retryable: true, CancellationToken.None);
        }

        var status = await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'");
        status.ShouldBe("Failed");
        var attempts = await ScalarAsync($"select count(*) from platform.processing_attempt where run_id = '{runId}'");
        attempts.ShouldBe("3");
    }

    [Fact]
    public async Task Transient_failure_can_be_retried_and_completed()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);

        var first = await queue.ClaimAsync(CancellationToken.None);
        first.ShouldNotBeNull();
        await queue.FailAsync(first!, "provider_unavailable", "Temporary failure.", retryable: true, CancellationToken.None);

        var second = await queue.ClaimAsync(CancellationToken.None);
        second.ShouldNotBeNull();
        second!.Attempt.ShouldBe(2);
        await queue.CompleteAsync(second, CancellationToken.None);

        var status = await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'");
        status.ShouldBe("Completed");
    }

    [Fact]
    public async Task Lifecycle_writes_audit_entries()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);

        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        await queue.FailAsync(job!, "provider_unavailable", "Temporary failure.", retryable: true, CancellationToken.None);
        var second = await queue.ClaimAsync(CancellationToken.None);
        second.ShouldNotBeNull();
        await queue.CompleteAsync(second!, CancellationToken.None);

        var actions = await ScalarAsync(
            $"select string_agg(action, ',' order by id) from platform.audit_entry where entity_id = '{runId}'");
        actions.ShouldContain("processing.started");
        actions.ShouldContain("processing.retried");
        actions.ShouldContain("processing.completed");
    }
    [Fact]
    public async Task Expired_lease_is_recovered()
    {
        var (runId, _) = await CreateRunAsync();
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);
        var job = await queue.ClaimAsync(CancellationToken.None);
        job.ShouldNotBeNull();

        await ExecuteAsync($"update platform.processing_run set lease_expires_at = now() - interval '1 minute' where id = '{runId}'");
        var recovered = await queue.RecoverAsync(CancellationToken.None);

        recovered.ShouldBe(1);
        var status = await ScalarAsync($"select status::text from platform.processing_run where id = '{runId}'");
        status.ShouldBe("Queued");
        var actions = await ScalarAsync(
            $"select string_agg(action, ',' order by id) from platform.audit_entry where entity_id = '{runId}'");
        actions.ShouldContain("processing.recovered");
    }

    [Fact]
    public async Task Cancelled_run_is_not_claimed()
    {
        var (runId, _) = await CreateRunAsync();
        await ExecuteAsync($"update platform.processing_run set status = 'Cancelled' where id = '{runId}'");
        var queue = new ProcessingQueue(fixture.ConnectionString, Options);

        (await queue.ClaimAsync(CancellationToken.None)).ShouldBeNull();
        (await queue.IsCancelledAsync(runId, CancellationToken.None)).ShouldBeTrue();
    }

    private async Task<(Guid RunId, Guid DocumentId)> CreateRunAsync()
    {
        // This class owns the queue tables in the shared fixture; clear leftovers from prior tests.
        await ExecuteAsync("delete from platform.processing_run;");

        var suffix = Guid.NewGuid().ToString("N");
        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{companyId}', 'Queue test {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{userId}', 'queue-{suffix}@example.test', 'Queue Test');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{documentId}', 'queue/{suffix}.pdf', 'queue.pdf', 'application/pdf', 1024, '{userId}', '{companyId}');" +
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
