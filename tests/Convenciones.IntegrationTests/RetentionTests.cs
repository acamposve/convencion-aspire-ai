using System;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Api.Infrastructure;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RetentionTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Guid _companyId;
    private Guid _documentId;
    private Guid _runId;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        _companyId = Guid.NewGuid();
        _documentId = Guid.NewGuid();
        _runId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        var user = _userId;
        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{_companyId}', 'Retention test');" +
            $"insert into platform.user_account (id, email, display_name) values ('{user}', 'retention-{user:N}@example.test', 'Retention');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{_documentId}', 'retention/{user:N}.pdf', 'retention.pdf', 'application/pdf', 10, '{user}', '{_companyId}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values ('{_runId}', '{_documentId}', '{_companyId}');" +
            $"insert into platform.document_text (document_id, content, char_count, extracted_at) values ('{_documentId}', 'texto extraído', 14, now());" +
            $"insert into platform.extracted_clause (run_id, document_id, company_id, ordinal, text, page_from, page_to) " +
            $"values ('{_runId}', '{_documentId}', '{_companyId}', 1, 'Cláusula', 1, 1);" +
            $"insert into platform.clause_classification (clause_id, category_id, company_id) " +
            $"select id, (select id from platform.clause_category where key = 'pricing'), '{_companyId}' " +
            $"from platform.extracted_clause where run_id = '{_runId}';");
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"delete from platform.processing_run where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.document where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.user_profile where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.user_account where id = '{_userId}';");
        await ExecuteAsync($"delete from app.company where id = '{_companyId}';");
    }

    [Fact]
    public async Task Purge_removes_clauses_classifications_attempts_and_runs()
    {
        var service = new ProcessingRetentionService(fixture.ConnectionString);

        var deleted = await service.PurgeDocumentAsync(_documentId, CancellationToken.None);

        deleted.ShouldBeGreaterThan(0);
        (await ScalarAsync($"select count(*) from platform.processing_run where document_id = '{_documentId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.extracted_clause where document_id = '{_documentId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.clause_classification where company_id = '{_companyId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.document_text where document_id = '{_documentId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.document where id = '{_documentId}'")).ShouldBe("1");
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
