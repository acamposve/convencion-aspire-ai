using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Convenciones.Api.Endpoints;
using Convenciones.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Proves the authorization contract end to end: no token is refused, a token without an account is
/// refused, an inactive account is refused, and every route declares what it needs.
/// </summary>
[Collection(IdentityCollection.Name)]
public sealed class AuthorizationTests(IdentityFixture fixture)
{
    private static readonly Guid ActiveUser = new("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid BlockedUser = new("a1000000-0000-0000-0000-000000000002");
    private static readonly Guid ExpiredUser = new("a1000000-0000-0000-0000-000000000003");
    private static readonly Guid NoAccountUser = new("a1000000-0000-0000-0000-000000000004");
    private static readonly Guid CompanyId = new("a2000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Every_route_declares_its_security_requirement()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var _ = factory.CreateClient(); // forces the host to start

        var endpoints = Program.ValidatedRoutes;

        // If the enumeration were empty the check below would pass for the wrong reason.
        endpoints.ShouldNotBeEmpty("the startup gate must inspect the real route table");

        var undeclared = endpoints
            .Where(endpoint => !EndpointSecurityValidator.IsDeclared(endpoint))
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        undeclared.ShouldBeEmpty();

        // The probes and /me must be in that set; otherwise the enumeration is partial.
        endpoints.Select(endpoint => endpoint.RoutePattern.RawText)
            .ShouldContain(pattern => pattern!.Contains("me", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_without_a_token_is_refused_with_401()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_signed_with_a_foreign_key_is_refused_with_401()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var token = TestAuth.Token(ActiveUser, signingKey: "a-foreign-signing-key-that-is-long-enough-1234");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_for_a_subject_without_an_account_is_refused_with_403()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(NoAccountUser));

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative));

        // Refused, but not as "unauthorised": the token is valid, the identity is simply not
        // provisioned. Access is never granted by default.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe("no_profile");
    }

    [Fact]
    public async Task Active_user_receives_company_and_capabilities()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(ActiveUser));

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative));

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"expected 200, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var me = await response.Content.ReadFromJsonAsync<MeResponse>();
        me.ShouldNotBeNull();
        me!.UserId.ShouldBe(ActiveUser);
        me.CompanyId.ShouldBe(CompanyId);
        me.Capabilities.ShouldContain("agreement.read");
        me.Capabilities.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Blocked_account_is_refused_with_403()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(BlockedUser));

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe("account_inactive");
    }

    [Fact]
    public async Task Expired_account_is_refused_with_403()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(ExpiredUser));

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadCodeAsync(response)).ShouldBe("account_inactive");
    }

    [Fact]
    public async Task Expired_token_is_refused_with_401()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.ExpiredToken(ActiveUser));

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_for_another_issuer_or_audience_is_refused_with_401()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestAuth.Token(ActiveUser, issuer: "https://somebody-else.example"));
        (await client.GetAsync(new Uri("/me", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestAuth.Token(ActiveUser, audience: "some-other-api"));
        (await client.GetAsync(new Uri("/me", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Probes_stay_anonymous()
    {
        await using var factory = new ApiFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        (await client.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem is not null && problem.TryGetValue("code", out var code)
            ? code?.ToString()
            : null;
    }
}

[CollectionDefinition(Name)]
public sealed class IdentityCollection : ICollectionFixture<IdentityFixture>
{
    public const string Name = "identity";
}
