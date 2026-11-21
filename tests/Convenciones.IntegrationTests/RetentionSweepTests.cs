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
            $"insert into platform.document_text (document_id, content, char_count, extracted_at) values ('{_documentId}', 'texto', 5, now());" +
            $"insert into platform.extracted_clause (run_id, document_id, company_id, ordinal, text, page_from, page_to) " +
            $"select id, '{_documentId}', '{_companyId}', 1, 'Cláusula', 1, 1 from platform.processing_run where document_id = '{_documentId}';" +
            $"insert into platform.clause_classification (clause_id, category_id, company_id) " +
            $"select c.id, (select id from platform.clause_category where key = 'pricing'), '{_companyId}' " +
            $"from platform.extracted_clause c where c.document_id = '{_documentId}';");
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"delete from platform.ai_usage_record where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.ai_budget_reservation where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.ai_budget_shadow where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.processing_run where company_id = '{_companyId}';");
        await ExecuteAsync($"delete from platform.document where id = '{_documentId}';");
        await ExecuteAsync($"delete from platform.user_account where id = '{_userId}';");
        await ExecuteAsync($"delete from app.company where id = '{_companyId}';");
    }

    [Fact]
    public async Task Sweep_skips_documents_that_have_financial_history()
    {
        await ExecuteAsync(
            $"insert into platform.processing_attempt (id, run_id, company_id, attempt_no) " +
            $"select gen_random_uuid(), id, '{_companyId}', 1 from platform.processing_run where document_id = '{_documentId}';" +
            $"insert into platform.ai_usage_record (invocation_id, company_id, document_id, run_id, attempt_id, stage, " +
            $"invocation_no, provider, input_tokens, output_tokens, usage_source, estimated_cost_usd, currency, " +
            $"pricing_version, pricing_input_per_million, pricing_output_per_million, period) " +
            $"select gen_random_uuid(), '{_companyId}', '{_documentId}', r.id, a.id, 'extraction', 1, 'azure-foundry', " +
            $"10, 5, 'provider', 0.01, 'USD', 'test-v1', 0.61, 1.82, (date_trunc('month', now()))::date " +
            $"from platform.processing_run r join platform.processing_attempt a on a.run_id = r.id " +
            $"where r.document_id = '{_documentId}';");

        var sweeper = new ProcessingRetentionSweeper(
            fixture.ConnectionString,
            new RetentionOptions { BatchSize = 100 },
            new WorkerOptions(),
            NullLogger<ProcessingRetentionSweeper>.Instance);

        await sweeper.SweepAsync(CancellationToken.None);

        (await ScalarAsync($"select count(*) from platform.processing_run where document_id = '{_documentId}'"))
            .ShouldBe("1", "a run with financial history is not purged");
        (await ScalarAsync($"select count(*) from platform.ai_usage_record where document_id = '{_documentId}'"))
            .ShouldBe("1", "the ledger survives retention");
        (await ScalarAsync($"select count(*) from platform.extracted_clause where document_id = '{_documentId}'"))
            .ShouldBe("1", "the document is skipped as a whole");
    }

    [Fact]
    public async Task Sweep_purges_ai_rows_for_soft_deleted_documents()
    {
        var sweeper = new ProcessingRetentionSweeper(
            fixture.ConnectionString,
            new RetentionOptions { BatchSize = 100 },
            new WorkerOptions(),
            NullLogger<ProcessingRetentionSweeper>.Instance);

        var purged = await sweeper.SweepAsync(CancellationToken.None);

        purged.ShouldBeGreaterThan(0);
        (await ScalarAsync($"select count(*) from platform.processing_run where document_id = '{_documentId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.extracted_clause where document_id = '{_documentId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.clause_classification where company_id = '{_companyId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.document_text where document_id = '{_documentId}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.document where id = '{_documentId}' and deleted_at is not null")).ShouldBe("1");
    }

    [Fact]
    public async Task Scoped_sweep_does_not_touch_another_company()
    {
        var otherCompany = Guid.NewGuid();
        var otherDocument = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{otherCompany}', 'Other sweep');" +
            $"insert into platform.user_account (id, email, display_name) values ('{otherUser}', 'other-{otherUser:N}@example.test', 'Other');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id, deleted_at) " +
            $"values ('{otherDocument}', 'other/file.pdf', 'other.pdf', 'application/pdf', 10, '{otherUser}', '{otherCompany}', now());" +
            $"insert into platform.processing_run (document_id, company_id) values ('{otherDocument}', '{otherCompany}');" +
            $"insert into platform.extracted_clause (run_id, document_id, company_id, ordinal, text, page_from, page_to) " +
            $"select id, '{otherDocument}', '{otherCompany}', 1, 'Otra cláusula', 1, 1 from platform.processing_run where document_id = '{otherDocument}';");

        var sweeper = new ProcessingRetentionSweeper(
            fixture.ConnectionString,
            new RetentionOptions { BatchSize = 100 },
            new WorkerOptions
            {
                RequireScope = true,
                AllowedCompanyId = _companyId,
                AuthorizedCompanyId = _companyId,
            },
            NullLogger<ProcessingRetentionSweeper>.Instance);

        await sweeper.SweepAsync(CancellationToken.None);

        (await ScalarAsync($"select count(*) from platform.processing_run where document_id = '{otherDocument}'"))
            .ShouldBe("1");
        (await ScalarAsync($"select count(*) from platform.extracted_clause where document_id = '{otherDocument}'"))
            .ShouldBe("1");

        await ExecuteAsync($"delete from platform.processing_run where document_id = '{otherDocument}';");
        await ExecuteAsync($"delete from platform.document where id = '{otherDocument}';");
        await ExecuteAsync($"delete from platform.user_account where id = '{otherUser}';");
        await ExecuteAsync($"delete from app.company where id = '{otherCompany}';");
    }

    [Fact]
    public async Task Run_scoped_sweep_preserves_other_run_and_its_correction()
    {
        var allowedRun = Guid.Parse(await ScalarAsync($"select id from platform.processing_run where document_id = '{_documentId}'"));
        var otherRun = Guid.NewGuid();
        var otherClause = Guid.NewGuid();
        await ExecuteAsync(
            $"update platform.processing_run set status = 'Completed' where id = '{allowedRun}';" +
            $"insert into platform.processing_run (id, document_id, company_id, status) values ('{otherRun}', '{_documentId}', '{_companyId}', 'Completed');" +
            $"insert into platform.extracted_clause (id, run_id, document_id, company_id, ordinal, text, page_from, page_to) " +
            $"values ('{otherClause}', '{otherRun}', '{_documentId}', '{_companyId}', 1, 'Other run', 1, 1);" +
            $"insert into platform.clause_classification (clause_id, category_id, company_id) " +
            $"values ('{otherClause}', (select id from platform.clause_category where key = 'pricing'), '{_companyId}');" +
            $"insert into platform.audit_entry (actor_id, module, entity_type, entity_id, action, changes) " +
            $"values (null, 'documents', 'extracted_clause', '{otherClause}', 'clause.classification.corrected', '{{\"next\":[{{\"key\":\"pricing\",\"confidence\":1,\"isPrimary\":true}}],\"primary\":\"pricing\"}}'::jsonb);");

        var sweeper = new ProcessingRetentionSweeper(
            fixture.ConnectionString,
            new RetentionOptions { BatchSize = 100 },
            new WorkerOptions
            {
                RequireScope = true,
                AllowedCompanyId = _companyId,
                AuthorizedCompanyId = _companyId,
                AllowedDocumentId = _documentId,
                AllowedRunId = allowedRun,
            },
            NullLogger<ProcessingRetentionSweeper>.Instance);

        (await sweeper.SweepAsync(CancellationToken.None)).ShouldBeGreaterThan(0);
        (await ScalarAsync($"select count(*) from platform.processing_run where id = '{allowedRun}'")).ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.processing_run where id = '{otherRun}'")).ShouldBe("1");
        (await ScalarAsync($"select count(*) from platform.extracted_clause where id = '{otherClause}' and run_id = '{otherRun}'")).ShouldBe("1");
        (await ScalarAsync($"select count(*) from platform.clause_classification where clause_id = '{otherClause}'")).ShouldBe("1");
        (await ScalarAsync($"select count(*) from platform.audit_entry where entity_id = '{otherClause}' and action = 'clause.classification.corrected'")).ShouldBe("1");
        (await ScalarAsync($"select count(*) from platform.document_text where document_id = '{_documentId}'")).ShouldBe("1");
        (await ScalarAsync($"select count(*) from platform.document where id = '{_documentId}'")).ShouldBe("1");
    }

    [Fact]
    public async Task Full_sweep_removes_text_even_when_no_run_remains()
    {
        var documentId = Guid.NewGuid();
        var user = Guid.NewGuid();
        await ExecuteAsync(
            $"insert into platform.user_account (id, email, display_name) values ('{user}', 'text-only-{user:N}@example.test', 'Text only');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id, deleted_at) " +
            $"values ('{documentId}', 'text-only/file.pdf', 'text.pdf', 'application/pdf', 10, '{user}', '{_companyId}', now());" +
            $"insert into platform.document_text (document_id, content, char_count, extracted_at) values ('{documentId}', 'solo texto', 10, now());");

        var sweeper = new ProcessingRetentionSweeper(
            fixture.ConnectionString,
            new RetentionOptions { BatchSize = 100 },
            new WorkerOptions(),
            NullLogger<ProcessingRetentionSweeper>.Instance);
        await sweeper.SweepAsync(CancellationToken.None);

        (await ScalarAsync($"select count(*) from platform.document_text where document_id = '{documentId}'"))
            .ShouldBe("0");
        (await ScalarAsync($"select count(*) from platform.document where id = '{documentId}'"))
            .ShouldBe("1");

        await ExecuteAsync($"delete from platform.document where id = '{documentId}';");
        await ExecuteAsync($"delete from platform.user_account where id = '{user}';");
    }

    [Fact]
    public async Task Sweep_refuses_to_orphan_a_human_correction()
    {
        await ExecuteAsync(
            $"insert into platform.audit_entry (actor_id, module, entity_type, entity_id, action, changes) " +
            $"select null, 'documents', 'extracted_clause', c.id::text, " +
            $"'clause.classification.corrected', '{{\"next\":[{{\"key\":\"scope\",\"confidence\":1,\"isPrimary\":true}}],\"primary\":\"scope\"}}'::jsonb " +
            $"from platform.extracted_clause c where c.document_id = '{_documentId}';");

        var sweeper = new ProcessingRetentionSweeper(
            fixture.ConnectionString,
            new RetentionOptions { BatchSize = 100 },
            new WorkerOptions(),
            NullLogger<ProcessingRetentionSweeper>.Instance);

        await sweeper.SweepAsync(CancellationToken.None);

        (await ScalarAsync($"select count(*) from platform.extracted_clause where document_id = '{_documentId}'"))
            .ShouldBe("1");
        (await ScalarAsync($"select count(*) from platform.document_text where document_id = '{_documentId}'"))
            .ShouldBe("1");
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
