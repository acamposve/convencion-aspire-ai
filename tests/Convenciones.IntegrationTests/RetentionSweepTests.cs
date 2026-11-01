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
public sealed class RetentionSweepTests(PostgresFixture fixture) : IAsyncLifetime
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
            $"insert into app.company (id, name) values ('{_companyId}', 'Sweep test');" +
            $"insert into platform.user_account (id, email, display_name) values ('{_userId}', 'sweep-{_userId:N}@example.test', 'Sweep');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id, deleted_at) " +
            $"values ('{_documentId}', 'sweep/file.pdf', 'sweep.pdf', 'application/pdf', 10, '{_userId}', '{_companyId}', now());" +
            $"insert into platform.processing_run (document_id, company_id) values ('{_documentId}', '{_companyId}');" +
            $"insert into platform.extracted_clause (run_id, document_id, company_id, ordinal, text, page_from, page_to) " +
            $"select id, '{_documentId}', '{_companyId}', 1, 'Cláusula', 1, 1 from platform.processing_run where document_id = '{_documentId}';" +
            $"insert into platform.clause_classification (clause_id, category_id, company_id) " +
            $"select c.id, (select id from platform.clause_category where key = 'pricing'), '{_companyId}' " +
            $"from platform.extracted_clause c where c.document_id = '{_documentId}';");
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"delete from platform.processing_run where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.document where id = '{_documentId}';");
        await ExecuteAsync($"delete from platform.user_account where id = '{_userId}';");
        await ExecuteAsync($"delete from app.company where id = '{_companyId}';");
    }

    [Fact]
    public async Task Sweep_purges_ai_rows_for_soft_deleted_documents()
    {
        var sweeper = new ProcessingRetentionSweeper(
            fixture.ConnectionString,
            new RetentionOptions { BatchSize = 100 },
            NullLogger<ProcessingRetentionSweeper>.Instance);

        var purged = await sweeper.SweepAsync(CancellationToken.None);

        purged.ShouldBeGreaterThan(0);
        (await ScalarAsync($"select count(*) from platform.processing_run where document_id = '{_documentId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.extracted_clause where document_id = '{_documentId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.clause_classification where company_id = '{_companyId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.document where id = '{_documentId}' and deleted_at is not null")).ShouldBe("1");
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
