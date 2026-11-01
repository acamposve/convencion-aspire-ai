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

        var results = await Task.WhenAll(
            service.TryReserveAsync(_companyId, CancellationToken.None),
            service.TryReserveAsync(_companyId, CancellationToken.None));

        results.Count(result => result.Allowed).ShouldBe(1);
        var status = await service.GetAsync(_companyId, CancellationToken.None);
        status.Usage.Documents.ShouldBe(1);
    }

    [Fact]
    public async Task Active_run_cap_rejects_a_second_run_for_the_company()
    {
        var service = new ProcessingQuotaService(fixture.ConnectionString);
        await ExecuteAsync(
            $"insert into platform.processing_run (document_id, company_id) values ('{_documentId}', '{_companyId}');");

        var decision = await service.TryReserveAsync(_companyId, 1, CancellationToken.None);

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

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
