using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Convenciones.Api.Endpoints;
using Convenciones.Catalog;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Proves the write path: the authorization matrix for creating a company, tenant scoping on write,
/// optimistic concurrency, and that every change leaves an audit row.
/// </summary>
/// <remarks>
/// The matrix is the point. "Requires both capabilities" is only true if a user holding exactly one of
/// them is refused, so all four combinations are exercised rather than just the happy path.
/// </remarks>
[Collection(IdentityCollection.Name)]
public sealed class CompanyWriteTests(IdentityFixture fixture)
{
    // Holds company.write AND company.read (editor) — but not company.read_all.
    private static readonly Guid WriteOnly = new("a1000000-0000-0000-0000-000000000005");

    // Holds company.read_all without company.write (transient "auditor" role).
    private static readonly Guid ReadAllOnly = new("a1000000-0000-0000-0000-000000000007");

    // Holds neither (negotiator: company.read only).
    private static readonly Guid Neither = new("a1000000-0000-0000-0000-000000000008");

    // Holds both (administrator).
    private static readonly Guid Both = new("a1000000-0000-0000-0000-000000000001");

    private static readonly Guid CompanyA = new("a2000000-0000-0000-0000-000000000001");
    private static readonly Guid CompanyB = new("a2000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Create_with_both_capabilities_succeeds()
    {
        using var client = Client(Both);

        var response = await client.PostAsJsonAsync(
            new Uri("/companies", UriKind.Relative),
            new CreateCompanyRequest($"Created {Guid.NewGuid():N}", null, null, null, "Caracas"));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            $"expected 201, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        response.Headers.Location.ShouldNotBeNull();
        var created = await response.Content.ReadFromJsonAsync<CompanyDetail>();
        created.ShouldNotBeNull();
        created!.RowVersion.ShouldBeGreaterThan(0u);
        created.Name.ShouldStartWith("Created ");
    }

    [Fact]
    public async Task Create_with_only_company_write_is_forbidden()
    {
        using var client = Client(WriteOnly);

        var response = await client.PostAsJsonAsync(
            new Uri("/companies", UriKind.Relative),
            new CreateCompanyRequest("Escalated Tenant", null, null, null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe("forbidden");
    }

    [Fact]
    public async Task Create_with_only_company_read_all_is_forbidden()
    {
        using var client = Client(ReadAllOnly);

        var response = await client.PostAsJsonAsync(
            new Uri("/companies", UriKind.Relative),
            new CreateCompanyRequest("Read Only Tenant", null, null, null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_with_neither_capability_is_forbidden()
    {
        using var client = Client(Neither);

        var response = await client.PostAsJsonAsync(
            new Uri("/companies", UriKind.Relative),
            new CreateCompanyRequest("Nobody Tenant", null, null, null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Creating_a_company_does_not_grant_access_to_it()
    {
        using var admin = Client(Both);
        using var editor = Client(WriteOnly);

        var created = await CreateAsync(admin, $"Isolation Check {Guid.NewGuid():N}");
        created.ShouldNotBeNull();

        // The creator's own company membership is untouched by creating a company.
        var me = await admin.GetFromJsonAsync<MeResponse>(new Uri("/me", UriKind.Relative));
        me.ShouldNotBeNull();
        me!.CompanyId.ShouldBe(CompanyA, "creating a company must not attach it to the creator");

        // And another company's user still cannot see it.
        var get = await editor.GetAsync(new Uri($"/companies/{created!.Id}", UriKind.Relative));
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var list = await editor.GetFromJsonAsync<CompanyListResponse>(new Uri("/companies", UriKind.Relative));
        list.ShouldNotBeNull();
        list!.Items.Count.ShouldBe(1);
        list.Items[0].Id.ShouldBe(CompanyA);
    }

    [Fact]
    public async Task Patch_with_current_row_version_succeeds_and_changes_the_row_version()
    {
        using var client = Client(Both);
        var company = await CreateAsync(client, $"Patch Target {Guid.NewGuid():N}");
        company.ShouldNotBeNull();

        var response = await PatchAsync(client, company!.Id, company.RowVersion, "Renamed Company");

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"expected 200, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var updated = await response.Content.ReadFromJsonAsync<CompanyDetail>();
        updated.ShouldNotBeNull();
        updated!.Name.ShouldBe("Renamed Company");
        updated.RowVersion.ShouldNotBe(company.RowVersion);
    }

    [Fact]
    public async Task Patch_with_a_stale_row_version_is_a_concurrency_conflict()
    {
        using var client = Client(Both);
        var company = await CreateAsync(client, $"Stale Target {Guid.NewGuid():N}");
        company.ShouldNotBeNull();

        var first = await PatchAsync(client, company!.Id, company.RowVersion, "First Update");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Re-using the pre-update version is exactly the lost-update scenario.
        var second = await PatchAsync(client, company.Id, company.RowVersion, "Second Update");

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadCodeAsync(second)).ShouldBe("concurrent_modification");
    }

    [Fact]
    public async Task Patch_without_if_match_is_refused()
    {
        using var client = Client(Both);
        var company = await CreateAsync(client, $"No Header {Guid.NewGuid():N}");

        var response = await client.PatchAsJsonAsync(
            new Uri($"/companies/{company!.Id}", UriKind.Relative),
            new UpdateCompanyRequest("Unconditional", null, null, null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadCodeAsync(response)).ShouldBe("validation_failed");
    }

    [Fact]
    public async Task Patch_of_another_company_is_not_found()
    {
        using var client = Client(WriteOnly);

        var response = await PatchAsync(client, CompanyB, 1u, "Hijacked");

        // The caller holds company.write, so a 403 would confirm the company exists.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Every_change_writes_an_audit_row_with_the_actor()
    {
        using var client = Client(Both);
        var name = $"Audited {Guid.NewGuid():N}";
        var company = await CreateAsync(client, name);
        company.ShouldNotBeNull();

        var createRows = await CountAuditRowsAsync(company!.Id, "created");
        createRows.ShouldBe(1, "creating a company must leave exactly one audit row");

        var actor = await ScalarAsync(
            "select actor_id::text from platform.audit_entry where entity_id = @id and action = 'created'",
            company.Id);
        actor.ShouldBe(Both.ToString());

        var patch = await PatchAsync(client, company.Id, company.RowVersion, "Audited Rename");
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updateRows = await CountAuditRowsAsync(company.Id, "updated");
        updateRows.ShouldBe(1, "a partial update must leave exactly one audit row");

        var changes = await ScalarAsync(
            "select changes::text from platform.audit_entry where entity_id = @id and action = 'updated'",
            company.Id);
        changes.ShouldNotBeNull();
        changes!.ShouldContain("Name");
    }

    private HttpClient Client(Guid subject)
    {
        var factory = new ApiFactory(fixture.ConnectionString);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(subject));
        return client;
    }

    private static async Task<CompanyDetail?> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/companies", UriKind.Relative),
            new CreateCompanyRequest(name, null, null, null, "Caracas"));

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            $"create failed: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<CompanyDetail>();
    }

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client,
        Guid id,
        uint rowVersion,
        string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/companies/{id}", UriKind.Relative))
        {
            Content = JsonContent.Create(new UpdateCompanyRequest(name, null, null, null, null)),
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{rowVersion}\""));

        return client.SendAsync(request);
    }

    private async Task<int> CountAuditRowsAsync(Guid companyId, string action)
    {
        var value = await ScalarAsync(
            "select count(*)::text from platform.audit_entry where entity_id = @id and action = @action",
            companyId,
            action);

        return int.Parse(value ?? "0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string?> ScalarAsync(string sql, Guid id, string? action = null)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id.ToString());

        if (action is not null)
        {
            command.Parameters.AddWithValue("action", action);
        }

        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result?.ToString();
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem is not null && problem.TryGetValue("code", out var code)
            ? code?.ToString()
            : null;
    }
}
