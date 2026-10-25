using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Convenciones.Api.Endpoints;
using Convenciones.Catalog;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Proves tenant scoping over HTTP, which is the only place it can actually be proven.
/// </summary>
/// <remarks>
/// The database enforces the same rule with RLS, but an endpoint that trusts the policy alone is one
/// policy edit away from serving another company's rows. These tests exercise the application rule with
/// a caller who holds <c>company.read</c> but not <c>company.read_all</c> — a role that must stay
/// scoped.
/// </remarks>
[Collection(IdentityCollection.Name)]
public sealed class TenantScopingTests(IdentityFixture fixture)
{
    private static readonly Guid EditorA = new("a1000000-0000-0000-0000-000000000005");
    private static readonly Guid Administrator = new("a1000000-0000-0000-0000-000000000001");

    private static readonly Guid CompanyA = new("a2000000-0000-0000-0000-000000000001");
    private static readonly Guid CompanyB = new("a2000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task Own_company_is_readable()
    {
        using var client = AuthenticatedClient(fixture, EditorA);

        var response = await client.GetAsync(new Uri($"/companies/{CompanyA}", UriKind.Relative));

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"expected 200, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var company = await response.Content.ReadFromJsonAsync<CompanyDetail>();
        company.ShouldNotBeNull();
        company!.Id.ShouldBe(CompanyA);
    }

    [Fact]
    public async Task Another_company_is_not_found_rather_than_forbidden()
    {
        using var client = AuthenticatedClient(fixture, EditorA);

        var response = await client.GetAsync(new Uri($"/companies/{CompanyB}", UriKind.Relative));

        // 403 would confirm the company exists and let a caller enumerate the platform's tenants.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadCodeAsync(response)).ShouldBe("not_found");
    }

    [Fact]
    public async Task List_returns_only_the_callers_company()
    {
        using var client = AuthenticatedClient(fixture, EditorA);

        var response = await client.GetAsync(new Uri("/companies", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<CompanyListResponse>();
        page.ShouldNotBeNull();
        page!.Items.Count.ShouldBe(1, "an editor holds company.read but not company.read_all");
        page.Items[0].Id.ShouldBe(CompanyA);
        page.Total.ShouldBe(1);
    }

    [Fact]
    public async Task Cross_company_capability_unlocks_every_company()
    {
        using var client = AuthenticatedClient(fixture, Administrator);

        var response = await client.GetAsync(new Uri("/companies", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<CompanyListResponse>();
        page.ShouldNotBeNull();

        // Not an exact count: other tests create companies, and the assertion must not depend on
        // execution order.
        page!.Items.Count.ShouldBeGreaterThanOrEqualTo(2, "an administrator holds company.read_all");
        page.Items.Select(item => item.Id).ShouldContain(CompanyA);
        page.Items.Select(item => item.Id).ShouldContain(CompanyB);
    }

    [Fact]
    public async Task Cross_company_capability_reads_any_company()
    {
        using var client = AuthenticatedClient(fixture, Administrator);

        var response = await client.GetAsync(new Uri($"/companies/{CompanyB}", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Company_endpoints_require_a_token()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        (await client.GetAsync(new Uri("/companies", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync(new Uri($"/companies/{CompanyA}", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static HttpClient AuthenticatedClient(IdentityFixture fixture, Guid subject)
    {
        var factory = new ApiFactory(fixture.ConnectionString);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(subject));
        return client;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem is not null && problem.TryGetValue("code", out var code)
            ? code?.ToString()
            : null;
    }
}
