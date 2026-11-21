using System;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Api.Infrastructure;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class QuotaTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Guid _companyId;
    private Guid _userId;
    private Guid _documentId;

    public async Task InitializeAsync()
    {
        _companyId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        _documentId = Guid.NewGuid();
        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{_companyId}', 'Quota test');" +
            $"insert into platform.user_account (id, email, display_name) values ('{_userId}', 'quota-{_userId:N}@example.test', 'Quota');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{_documentId}', 'quota/{_userId:N}.pdf', 'quota.pdf', 'application/pdf', 10, '{_userId}', '{_companyId}');");
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"delete from platform.processing_run where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.document where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.user_account where id = '{_userId}';");
        await ExecuteAsync($"delete from platform.processing_usage where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.company_quota where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from app.company where id = '{_companyId}';");
    }

    [Fact]
    public async Task Concurrent_reservations_do_not_exceed_the_document_limit()
    {
        var service = new ProcessingQuotaService(fixture.ConnectionString);
        await service.SetAsync(_companyId, new QuotaLimits(1, null, null), CancellationToken.None);

        var barrier = new Barrier(2);
        var results = await Task.WhenAll(ReserveAsync(service, 0, barrier: barrier), ReserveAsync(service, 0, barrier: barrier));

        results.Count(result => result.Allowed).ShouldBe(1);
        var status = await service.GetAsync(_companyId, CancellationToken.None);
        status.Usage.Documents.ShouldBe(1);
    }

    [Fact]
    public async Task Reservation_rolls_back_with_the_callers_transaction()
    {
        // P1-001: a failure after reserving must not leave an orphan charge behind. The old
        // implementation committed the reservation on its own connection, so this rollback was a no-op.
        var service = new ProcessingQuotaService(fixture.ConnectionString);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var decision = await service.TryReserveAsync(
            connection, transaction, _companyId, 0, CancellationToken.None);

        decision.Allowed.ShouldBeTrue();

        await transaction.RollbackAsync();

        var status = await service.GetAsync(_companyId, CancellationToken.None);
        status.Usage.Documents.ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_starts_cannot_exceed_the_active_run_cap()
    {
        // Two documents of the same tenant start at once with a cap of one. The advisory lock is
        // transaction-scoped, so the second start must observe the first run, not just the first charge.
        var service = new ProcessingQuotaService(fixture.ConnectionString);
        var secondDocument = Guid.NewGuid();
        await ExecuteAsync(
            "insert into platform.document " +
            "(id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) values " +
            $"('{secondDocument}', 'quota/{secondDocument:N}.pdf', 'quota2.pdf', 'application/pdf', 10, '{_userId}', '{_companyId}');");

        var barrier = new Barrier(2);
        var results = await Task.WhenAll(
            ReserveAsync(service, 1, _documentId, barrier),
            ReserveAsync(service, 1, secondDocument, barrier));

        results.Count(result => result.Allowed).ShouldBe(1);
        results.Single(result => !result.Allowed).Code.ShouldBe("concurrency_limit");
        (await CountActiveRunsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Active_run_cap_rejects_a_second_run_for_the_company()
    {
        var service = new ProcessingQuotaService(fixture.ConnectionString);
        await ExecuteAsync(
            $"insert into platform.processing_run (document_id, company_id) values ('{_documentId}', '{_companyId}');");

        var decision = await ReserveAsync(service, 1);

        decision.Allowed.ShouldBeFalse();
        decision.Code.ShouldBe("concurrency_limit");
    }
    [Fact]
    public async Task Usage_accumulates_pages_tokens_and_cost()
    {
        var service = new ProcessingQuotaService(fixture.ConnectionString);
        await service.RecordUsageAsync(_companyId, 16, 1_000, 500, 0.12m, CancellationToken.None);
        await service.RecordUsageAsync(_companyId, 4, 500, 250, 0.03m, CancellationToken.None);

        var status = await service.GetAsync(_companyId, CancellationToken.None);

        status.Usage.Pages.ShouldBe(20);
        status.Usage.InputTokens.ShouldBe(1_500);
        status.Usage.OutputTokens.ShouldBe(750);
        status.Usage.CostEstimateUsd.ShouldBe(0.15m);
    }

    /// <summary>
    /// Runs one start attempt the way the endpoint does: reserve inside a transaction, insert the run
    /// when allowed, then commit; roll back when the reservation is refused.
    /// </summary>
    private async Task<QuotaDecision> ReserveAsync(
        ProcessingQuotaService service,
        int maxActiveRuns,
        Guid? documentId = null,
        Barrier? barrier = null)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // Both attempts must be inside their transaction before either reserves, otherwise the test
        // could pass by running sequentially and prove nothing about concurrency.
        barrier?.SignalAndWait(TimeSpan.FromSeconds(10));

        var decision = await service.TryReserveAsync(
            connection, transaction, _companyId, maxActiveRuns, CancellationToken.None);

        if (decision.Allowed)
        {
            if (documentId is { } id)
            {
                await using var insert = new NpgsqlCommand(
                    $"insert into platform.processing_run (document_id, company_id) values ('{id}', '{_companyId}');",
                    connection,
                    transaction);
                await insert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return decision;
    }

    private async Task<int> CountActiveRunsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from platform.processing_run where company_id = @company " +
            "and status in ('Queued'::platform.processing_status, 'Running'::platform.processing_status);",
            connection);
        command.Parameters.AddWithValue("company", _companyId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
