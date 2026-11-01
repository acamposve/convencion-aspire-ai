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

    public async Task InitializeAsync()
    {
        _companyId = Guid.NewGuid();
        await ExecuteAsync($"insert into app.company (id, name) values ('{_companyId}', 'Quota test');");
    }

    public async Task DisposeAsync()
    {
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
