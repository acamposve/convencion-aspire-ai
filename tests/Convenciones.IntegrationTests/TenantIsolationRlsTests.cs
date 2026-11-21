using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-008: proves tenant isolation at the PostgreSQL policy layer, under the real application roles
/// (<c>app_api</c> and <c>authenticated</c>) with JWT claims set, never as a superuser.
/// </summary>
/// <remarks>
/// Two tenants are seeded with rows in every tenant-scoped table the application reads or writes, then a
/// caller from tenant A attempts to read, update and delete tenant B's rows and to insert rows attributed
/// to B. A denied statement either affects zero rows or fails with 42501; a superuser run would bypass the
/// policies and report false positives, which is why the role is switched explicitly.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class TenantIsolationRlsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Guid _companyA;
    private Guid _companyB;
    private Guid _userA;
    private Guid _documentB;
    private Guid _runB;
    private Guid _attemptB;
    private Guid _clauseB;
    private Guid _budgetB;

    public async Task InitializeAsync()
    {
        _companyA = Guid.NewGuid();
        _companyB = Guid.NewGuid();
        _userA = Guid.NewGuid();
        _documentB = Guid.NewGuid();
        _runB = Guid.NewGuid();
        _attemptB = Guid.NewGuid();
        _clauseB = Guid.NewGuid();
        _budgetB = Guid.NewGuid();

        var documentA = Guid.NewGuid();
        var runA = Guid.NewGuid();
        var attemptA = Guid.NewGuid();

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{_companyA}', 'Isolation A'), ('{_companyB}', 'Isolation B');" +
            $"insert into platform.user_account (id, email, display_name) values ('{_userA}', 'iso-a-{_userA:N}@example.test', 'Isolation A');" +
            $"insert into platform.user_profile (user_id, company_id) values ('{_userA}', '{_companyA}');" +
            $"insert into platform.role (key, name, description, is_system) values ('iso_reader', 'Isolation Reader', 'P1-008 role', true) on conflict do nothing;" +
            $"insert into platform.role_capability (role_id, capability_id) " +
            $"select r.id, c.id from platform.role r join platform.capability c on c.key in " +
            $"('company.read', 'document.read', 'document.write', 'clause.read', 'clause.process', 'clause.review', " +
            $"'agreement.read', 'agreement.write', 'usage.read', 'quota.manage', 'audit.read') " +
            $"where r.key = 'iso_reader' and not exists (select 1 from platform.role_capability rc where rc.role_id = r.id and rc.capability_id = c.id);" +
            $"insert into platform.user_role (user_id, role_id) select '{_userA}', id from platform.role where key = 'iso_reader';" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) values " +
            $"('{documentA}', 'iso/a.pdf', 'a.pdf', 'application/pdf', 10, '{_userA}', '{_companyA}'), " +
            $"('{_documentB}', 'iso/b.pdf', 'b.pdf', 'application/pdf', 10, '{_userA}', '{_companyB}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values " +
            $"('{runA}', '{documentA}', '{_companyA}'), ('{_runB}', '{_documentB}', '{_companyB}');" +
            $"insert into platform.processing_attempt (id, run_id, company_id, attempt_no) values " +
            $"('{attemptA}', '{runA}', '{_companyA}', 1), ('{_attemptB}', '{_runB}', '{_companyB}', 1);" +
            $"insert into platform.extracted_clause (id, run_id, document_id, company_id, ordinal, text, page_from, page_to) values " +
            $"('{Guid.NewGuid()}', '{runA}', '{documentA}', '{_companyA}', 1, 'Clausula A', 1, 1), " +
            $"('{_clauseB}', '{_runB}', '{_documentB}', '{_companyB}', 1, 'Clausula B', 1, 1);" +
            $"insert into platform.clause_classification (clause_id, category_id, company_id) " +
            $"select '{_clauseB}', id, '{_companyB}' from platform.clause_category where key = 'pricing';" +
            $"insert into platform.processing_usage (company_id, period) values ('{_companyA}', date_trunc('month', now())::date), ('{_companyB}', date_trunc('month', now())::date);" +
            $"insert into platform.company_quota (company_id) values ('{_companyA}'), ('{_companyB}');" +
            $"insert into platform.ai_usage_record (invocation_id, company_id, document_id, run_id, attempt_id, stage, " +
            $"invocation_no, provider, input_tokens, output_tokens, usage_source, estimated_cost_usd, currency, " +
            $"pricing_version, pricing_input_per_million, pricing_output_per_million, period) values " +
            $"('{Guid.NewGuid()}', '{_companyB}', '{_documentB}', '{_runB}', '{_attemptB}', 'extraction', 1, " +
            $"'azure-foundry', 10, 5, 'provider', 0.000010, 'USD', 'test-v1', 0.61, 1.82, (date_trunc('month', now()))::date);" +
            $"insert into platform.ai_budget (id, company_id, period_start, period_end, limit_amount, enforcement) values " +
            $"('{_budgetB}', '{_companyB}', (date_trunc('month', now()))::date, (date_trunc('month', now()) + interval '1 month - 1 day')::date, 100, 'shadow');" +
            $"insert into platform.ai_budget_reservation (invocation_id, budget_id, company_id, run_id, attempt_id, stage, " +
            $"estimated_cost, currency, status, expires_at) values " +
            $"('{Guid.NewGuid()}', '{_budgetB}', '{_companyB}', '{_runB}', '{_attemptB}', 'extraction', 1, 'USD', 'reserved', now() + interval '10 minutes');" +
            $"insert into platform.ai_budget_shadow (invocation_id, company_id, run_id, attempt_id, stage, period, estimated_cost, would_admit) values " +
            $"('{Guid.NewGuid()}', '{_companyB}', '{_runB}', '{_attemptB}', 'extraction', (date_trunc('month', now()))::date, 1, true);" +
            $"insert into platform.ai_budget_audit (company_id, period_start, actor, action) values " +
            $"('{_companyB}', (date_trunc('month', now()))::date, 'iso', 'seed');" +
            $"insert into app.negotiation_case (id, company_id, started_on) values " +
            $"('{Guid.NewGuid()}', '{_companyA}', current_date), ('{Guid.NewGuid()}', '{_companyB}', current_date);" +
            $"insert into app.agreement (id, company_id, valid_from, scope) values " +
            $"('{Guid.NewGuid()}', '{_companyA}', current_date, 'company'), " +
            $"('{Guid.NewGuid()}', '{_companyB}', current_date, 'company');");
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync(
            $"delete from platform.ai_usage_record where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.ai_budget_reservation where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.ai_budget_shadow where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.ai_budget_audit where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.ai_budget where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.clause_classification where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.extracted_clause where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.processing_attempt where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.processing_run where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.processing_usage where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.company_quota where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.document where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from app.agreement where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from app.negotiation_case where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.user_role where user_id = '{_userA}';" +
            $"delete from platform.user_profile where user_id = '{_userA}';" +
            $"delete from platform.user_account where id = '{_userA}';" +
            $"delete from app.company where id in ('{_companyA}', '{_companyB}');");
    }

    private static readonly string[] TenantTables =
    [
        "platform.document",
        "platform.processing_run",
        "platform.processing_attempt",
        "platform.extracted_clause",
        "platform.clause_classification",
        "platform.processing_usage",
        "platform.company_quota",
        "platform.ai_usage_record",
        "platform.ai_budget",
        "platform.ai_budget_reservation",
        "platform.ai_budget_shadow",
        "platform.ai_budget_audit",
        "app.agreement",
        "app.negotiation_case",
    ];

    [Fact]
    public async Task Cross_tenant_reads_are_denied_for_every_tenant_table_under_app_api()
    {
        await using var connection = await OpenAsAsync("app_api", _userA);

        foreach (var table in TenantTables)
        {
            var count = await ScalarAsync(connection, $"select count(*) from {table} where company_id = '{_companyB}';");
            count.ShouldBe("0", $"{table} exposed another tenant's rows");
        }
    }

    [Fact]
    public async Task Cross_tenant_updates_and_deletes_affect_no_rows()
    {
        await using var connection = await OpenAsAsync("app_api", _userA);

        foreach (var table in TenantTables)
        {
            var updated = await AffectedAsync(connection, $"update {table} set company_id = company_id where company_id = '{_companyB}';");
            updated.ShouldBe(0, $"{table} allowed an update of another tenant's rows");
        }

        foreach (var table in TenantTables)
        {
            var deleted = await AffectedAsync(connection, $"delete from {table} where company_id = '{_companyB}';");
            deleted.ShouldBe(0, $"{table} allowed a delete of another tenant's rows");
        }
    }

    [Fact]
    public async Task Cross_tenant_inserts_are_rejected_or_hidden()
    {
        await using var connection = await OpenAsAsync("app_api", _userA);

        var documentDenied = await IsDeniedAsync(connection,
            $"insert into platform.document (storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('iso/x.pdf', 'x.pdf', 'application/pdf', 1, '{_userA}', '{_companyB}');");
        documentDenied.ShouldBeTrue("document insert for another company must be rejected");

        var runDenied = await IsDeniedAsync(connection,
            $"insert into platform.processing_run (document_id, company_id) values ('{_documentB}', '{_companyB}');");
        runDenied.ShouldBeTrue("run insert for another company must be rejected");

        var agreementDenied = await IsDeniedAsync(connection,
            $"insert into app.agreement (company_id, valid_from, scope) " +
            $"values ('{_companyB}', current_date, 'company');");
        agreementDenied.ShouldBeTrue("agreement insert for another company must be rejected");

        var budgetDenied = await IsDeniedAsync(connection,
            $"insert into platform.ai_budget (company_id, period_start, period_end, limit_amount) " +
            $"values ('{_companyB}', current_date, current_date, 1);");
        budgetDenied.ShouldBeTrue("budget insert for another company must be rejected");
    }

    [Fact]
    public async Task The_same_tenant_can_still_read_and_write()
    {
        await using var connection = await OpenAsAsync("app_api", _userA);

        (await ScalarAsync(connection, $"select count(*) from platform.document where company_id = '{_companyA}';"))
            .ShouldBe("1");
        (await ScalarAsync(connection, $"select count(*) from app.agreement where company_id = '{_companyA}';"))
            .ShouldBe("1");

        var updated = await AffectedAsync(connection,
            $"update platform.document set file_name = file_name where company_id = '{_companyA}';");
        updated.ShouldBeGreaterThan(0, "the positive control must be able to update its own rows");
    }

    [Fact]
    public async Task Cross_tenant_reads_are_denied_for_the_authenticated_role()
    {
        await using var connection = await OpenAsAsync("authenticated", _userA);

        var documents = await ScalarAsync(connection, $"select count(*) from platform.document where company_id = '{_companyB}';");
        documents.ShouldBe("0");

        var runs = await ScalarAsync(connection, $"select count(*) from platform.processing_run where company_id = '{_companyB}';");
        runs.ShouldBe("0");

        var clauses = await ScalarAsync(connection, $"select count(*) from platform.extracted_clause where company_id = '{_companyB}';");
        clauses.ShouldBe("0");
    }

    // ---------------------------------------------------------------- helpers

    private async Task<NpgsqlConnection> OpenAsAsync(string role, Guid subject)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"set role {role};");
        await ExecuteAsync(connection, $"select set_config('request.jwt.claims', '{{\"sub\":\"{subject}\"}}', false);");
        return connection;
    }

    private static async Task<bool> IsDeniedAsync(NpgsqlConnection connection, string sql)
    {
        try
        {
            await ExecuteAsync(connection, sql);
            return false;
        }
        catch (PostgresException exception) when (exception.SqlState is "42501" or "23514" or "23503")
        {
            return true;
        }
    }

    private static async Task<int> AffectedAsync(NpgsqlConnection connection, string sql)
    {
        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            // A policy that does not grant the command at all denies it outright; that is still a denial.
            return 0;
        }
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, sql);
    }
}
