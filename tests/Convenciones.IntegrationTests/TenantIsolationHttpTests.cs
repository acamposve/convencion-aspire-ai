using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-008: proves tenant isolation over HTTP, where the application rule (not just RLS) decides.
/// </summary>
/// <remarks>
/// Tenant A holds every capability a normal editor needs. Tenant B owns documents, runs and quotas that A
/// must never reach. The expected status is 404 for a foreign identifier (403 would confirm that the row
/// exists and let a caller enumerate tenants) and a capability denial is 403 when the route itself is not
/// part of the caller's role.
///
/// <c>company.read_all</c> is exercised explicitly: the Product Owner's decision is that it is for
/// administrative/aggregate views, not for another tenant's raw content, so the observed behaviour on the
/// document routes is recorded here and in the isolation matrix.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class TenantIsolationHttpTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Guid _companyA;
    private Guid _companyB;
    private Guid _editor;
    private Guid _admin;
    private Guid _blocked;
    private Guid _documentA;
    private Guid _documentB;
    private Guid _runB;

    public async Task InitializeAsync()
    {
        _companyA = Guid.NewGuid();
        _companyB = Guid.NewGuid();
        _editor = Guid.NewGuid();
        _admin = Guid.NewGuid();
        _blocked = Guid.NewGuid();
        _documentA = Guid.NewGuid();
        _documentB = Guid.NewGuid();
        _runB = Guid.NewGuid();
        var runA = Guid.NewGuid();
        var editorEmail = $"iso-editor-{_editor:N}@example.test";
        var adminEmail = $"iso-admin-{_admin:N}@example.test";
        var blockedEmail = $"iso-blocked-{_blocked:N}@example.test";

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{_companyA}', 'HTTP A'), ('{_companyB}', 'HTTP B');" +
            $"insert into platform.user_account (id, email, display_name, is_blocked) values " +
            $"('{_editor}', '{editorEmail}', 'Editor A', false), ('{_admin}', '{adminEmail}', 'Admin', false), " +
            $"('{_blocked}', '{blockedEmail}', 'Blocked', true);" +
            $"insert into platform.user_profile (user_id, company_id) values ('{_editor}', '{_companyA}'), ('{_admin}', '{_companyA}'), ('{_blocked}', '{_companyA}');" +
            $"insert into platform.role (key, name, description, is_system) values " +
            $"('iso_editor', 'Isolation Editor', 'P1-008', true), ('iso_admin', 'Isolation Admin', 'P1-008', true) on conflict do nothing;" +
            $"insert into platform.role_capability (role_id, capability_id) " +
            $"select r.id, c.id from platform.role r join platform.capability c on c.key in " +
            $"('company.read', 'document.read', 'document.write', 'clause.read', 'clause.process', 'agreement.read', " +
            $"'negotiation.read', 'statute.read', 'news.read', 'comparison.read') " +
            $"where r.key = 'iso_editor' and not exists (select 1 from platform.role_capability rc where rc.role_id = r.id and rc.capability_id = c.id);" +
            $"insert into platform.role_capability (role_id, capability_id) " +
            $"select r.id, c.id from platform.role r join platform.capability c on c.key in " +
            $"('company.read', 'company.read_all', 'document.read', 'document.write', 'clause.read', 'clause.process', " +
            $"'agreement.read', 'negotiation.read', 'statute.read', 'news.read', 'comparison.read', 'quota.manage', 'user.manage') " +
            $"where r.key = 'iso_admin' and not exists (select 1 from platform.role_capability rc where rc.role_id = r.id and rc.capability_id = c.id);" +
            $"insert into platform.user_role (user_id, role_id) " +
            $"select v.uid, r.id from (values ('{_editor}'::uuid, 'iso_editor'), ('{_admin}'::uuid, 'iso_admin'), ('{_blocked}'::uuid, 'iso_editor')) as v(uid, role_key) " +
            $"join platform.role r on r.key = v.role_key;" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) values " +
            $"('{_documentA}', 'http/a.pdf', 'a.pdf', 'application/pdf', 10, '{_editor}', '{_companyA}'), " +
            $"('{_documentB}', 'http/b.pdf', 'b.pdf', 'application/pdf', 10, '{_editor}', '{_companyB}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values " +
            $"('{runA}', '{_documentA}', '{_companyA}'), ('{_runB}', '{_documentB}', '{_companyB}');" +
            $"insert into platform.processing_usage (company_id, period) values " +
            $"('{_companyA}', date_trunc('month', now())::date), ('{_companyB}', date_trunc('month', now())::date);" +
            $"insert into platform.company_quota (company_id, documents_limit) values ('{_companyA}', 10), ('{_companyB}', 99);");
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync(
            $"delete from platform.processing_usage where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.company_quota where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.processing_run where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.document where company_id in ('{_companyA}', '{_companyB}');" +
            $"delete from platform.user_role where user_id in ('{_editor}', '{_admin}', '{_blocked}');" +
            $"delete from platform.user_profile where user_id in ('{_editor}', '{_admin}', '{_blocked}');" +
            $"delete from platform.user_account where id in ('{_editor}', '{_admin}', '{_blocked}');" +
            $"delete from app.company where id in ('{_companyA}', '{_companyB}');");
    }

    [Fact]
    public async Task Another_tenants_company_is_not_found()
    {
        using var client = Client(_editor);

        var response = await client.GetAsync(new Uri($"/companies/{_companyB}", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_tenants_document_detail_text_and_download_are_not_found()
    {
        using var client = Client(_editor);

        foreach (var path in new[]
                 {
                     $"/documents/{_documentB}",
                     $"/documents/{_documentB}/text",
                     $"/documents/{_documentB}/download",
                 })
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{path} must not reveal another tenant's document");
            (await response.Content.ReadAsStringAsync()).ShouldNotContain("b.pdf");
        }
    }

    [Fact]
    public async Task Another_tenants_processing_state_and_clauses_are_not_found()
    {
        using var client = Client(_editor);

        foreach (var path in new[]
                 {
                     $"/documents/{_documentB}/processing",
                     $"/documents/{_documentB}/clauses",
                     $"/processing/{_runB}",
                 })
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{path} must not expose another tenant's run");
        }
    }

    [Fact]
    public async Task Processing_another_tenants_document_is_rejected_and_creates_no_run()
    {
        using var client = Client(_editor);

        var response = await client.PostAsync(new Uri($"/documents/{_documentB}/process", UriKind.Relative), null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var runs = await ScalarAsync($"select count(*) from platform.processing_run where document_id = '{_documentB}';");
        runs.ShouldBe("1", "no run may be created for another tenant's document");
    }

    [Fact]
    public async Task Quota_of_another_tenant_is_neither_readable_nor_writable()
    {
        using var client = Client(_editor);

        var read = await client.GetAsync(new Uri($"/companies/{_companyB}/usage", UriKind.Relative));
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var write = await client.PutAsJsonAsync(
            new Uri($"/companies/{_companyB}/quota", UriKind.Relative),
            new { documentsLimit = 1, pagesLimit = 1, tokensLimit = 1 });

        write.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an editor does not hold quota.manage");

        var limit = await ScalarAsync($"select documents_limit from platform.company_quota where company_id = '{_companyB}';");
        limit.ShouldBe("99", "a denied write must not change the other tenant's quota");
    }

    [Fact]
    public async Task Administration_requires_user_manage()
    {
        using var editorClient = Client(_editor);
        using var adminClient = Client(_admin);

        (await editorClient.GetAsync(new Uri("/users", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        (await adminClient.GetAsync(new Uri("/users", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Blocked_users_are_rejected()
    {
        using var client = Client(_blocked);

        // A blocked account is authenticated but not permitted: the session resolution reports 403 rather
        // than pretending the credential is invalid.
        (await client.GetAsync(new Uri($"/documents/{_documentA}", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        using var client = UnauthenticatedClient();

        (await client.GetAsync(new Uri($"/documents/{_documentA}", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_returns_the_caller()
    {
        using var client = Client(_editor);

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Company_read_all_currently_reaches_another_tenants_company_and_document()
    {
        // Recorded behaviour, not an endorsement: the Product Owner's decision is that company.read_all is
        // for administrative/aggregate oversight, not for another tenant's raw content. The assertions keep
        // the current behaviour visible and the matrix records it as a policy finding.
        using var client = Client(_admin);

        var company = await client.GetAsync(new Uri($"/companies/{_companyB}", UriKind.Relative));
        company.StatusCode.ShouldBe(HttpStatusCode.OK, "company.read_all currently unlocks company detail");

        var document = await client.GetAsync(new Uri($"/documents/{_documentB}", UriKind.Relative));
        document.StatusCode.ShouldBe(HttpStatusCode.OK, "current behaviour: company.read_all reaches raw document metadata; recorded as a policy finding");
    }

    // ---------------------------------------------------------------- helpers

    private HttpClient Client(Guid subject)
    {
        var client = new ApiFactory(fixture.ConnectionString).CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(subject));
        return client;
    }

    private HttpClient UnauthenticatedClient() => new ApiFactory(fixture.ConnectionString).CreateClient();

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
