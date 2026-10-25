using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Convenciones.Administration;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// The one-time bootstrap over HTTP: it provisions exactly one SuperAdmin, the second call is refused, and
/// the refusal comes from the database rather than from the API's convenience check.
/// </summary>
[Collection(BootstrapCollection.Name)]
public sealed class BootstrapApiTests(BootstrapFixture fixture) : IDisposable
{
    private readonly List<ApiFactory> _hosts = [];

    [Fact]
    public async Task The_bootstrap_creates_the_first_superadmin_once()
    {
        using var client = ClientFor();

        var before = await client.GetFromJsonAsync<BootstrapStatusResponse>(
            new Uri("/auth/bootstrap/status", UriKind.Relative));

        before.ShouldNotBeNull();
        before!.Configured.ShouldBeFalse();

        var created = await client.PostAsJsonAsync(
            new Uri("/auth/bootstrap", UriKind.Relative),
            new BootstrapRequest(BootstrapFixture.FirstAdmin, "first@example.test", "First SuperAdmin"));

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var user = await created.Content.ReadFromJsonAsync<UserDetail>();

        user.ShouldNotBeNull();
        user!.Id.ShouldBe(BootstrapFixture.FirstAdmin);
        user.Roles.ShouldBe(["superadmin"]);

        var after = await client.GetFromJsonAsync<BootstrapStatusResponse>(
            new Uri("/auth/bootstrap/status", UriKind.Relative));
        after!.Configured.ShouldBeTrue();

        var second = await client.PostAsJsonAsync(
            new Uri("/auth/bootstrap", UriKind.Relative),
            new BootstrapRequest(Guid.NewGuid(), "second@example.test", "Intruder"));

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("code").GetString().ShouldBe("invalid_state");

        (await fixture.UserCountAsync()).ShouldBe(1);
        (await fixture.AuditCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_bootstrap_validates_its_payload_before_touching_the_database()
    {
        using var client = ClientFor();

        var invalid = await client.PostAsJsonAsync(
            new Uri("/auth/bootstrap", UriKind.Relative),
            new BootstrapRequest(BootstrapFixture.FirstAdmin, "not-an-email", "First"));

        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fixture.UserCountAsync()).ShouldBe(0);
    }

    private HttpClient ClientFor()
    {
        var factory = new ApiFactory(fixture.ConnectionString);
        _hosts.Add(factory);
        return factory.CreateClient();
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }
}

/// <summary>
/// Identity administration: provisioning, blocking, self-lockout, the role matrix and the audit trail.
/// </summary>
[Collection(AdministrationCollection.Name)]
public sealed class AdministrationTests(AdministrationFixture fixture) : IDisposable
{
    private readonly List<ApiFactory> _hosts = [];

    [Fact]
    public async Task An_administrator_provisions_a_user_who_can_then_read_their_own_session()
    {
        using var admin = ClientFor(AdministrationFixture.SuperAdmin);
        var subject = Guid.NewGuid();

        var created = await CreateUserAsync(
            admin, subject, "provisioned@example.test", "Provisioned Editor", ["editor"]);

        created.Roles.ShouldBe(["editor"]);

        using var provisioned = ClientFor(subject);
        var me = await provisioned.GetAsync(new Uri("/me", UriKind.Relative));

        me.StatusCode.ShouldBe(HttpStatusCode.OK, await me.Content.ReadAsStringAsync());

        var list = await admin.GetFromJsonAsync<UserListResponse>(
            new Uri("/users?q=provisioned", UriKind.Relative));

        list.ShouldNotBeNull();
        list!.Items.ShouldContain(item => item.Id == subject && item.Roles.Contains("editor"));
    }

    [Fact]
    public async Task A_capability_is_read_from_the_database_on_every_request()
    {
        using var admin = ClientFor(AdministrationFixture.SuperAdmin);
        var subject = Guid.NewGuid();
        await CreateUserAsync(admin, subject, "capability@example.test", "Capability", ["editor"]);

        using var user = ClientFor(subject);

        (await user.GetAsync(new Uri("/companies", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        // Removing the role takes effect on the next request, not when a token expires.
        var replaced = await admin.PutAsJsonAsync(
            new Uri($"/users/{subject}/roles", UriKind.Relative),
            new ReplaceUserRolesRequest([]));

        replaced.StatusCode.ShouldBe(HttpStatusCode.OK, await replaced.Content.ReadAsStringAsync());

        (await user.GetAsync(new Uri("/companies", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Blocking_takes_effect_on_the_users_next_request()
    {
        using var admin = ClientFor(AdministrationFixture.SuperAdmin);
        var subject = Guid.NewGuid();
        await CreateUserAsync(admin, subject, "blocked@example.test", "Soon Blocked", ["editor"]);

        using var user = ClientFor(subject);
        (await user.GetAsync(new Uri("/me", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var blocked = await admin.PatchAsJsonAsync(
            new Uri($"/users/{subject}", UriKind.Relative),
            new { isBlocked = true });

        blocked.StatusCode.ShouldBe(HttpStatusCode.OK, await blocked.Content.ReadAsStringAsync());

        var refused = await user.GetAsync(new Uri("/me", UriKind.Relative));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("code").GetString().ShouldBe("account_inactive");

        // Unblocking restores the same token: the check is live in both directions.
        await admin.PatchAsJsonAsync(new Uri($"/users/{subject}", UriKind.Relative), new { isBlocked = false });
        (await user.GetAsync(new Uri("/me", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_administrator_cannot_remove_their_own_last_administrative_role()
    {
        using var admin = ClientFor(AdministrationFixture.SuperAdmin);

        var emptied = await admin.PutAsJsonAsync(
            new Uri($"/users/{AdministrationFixture.SuperAdmin}/roles", UriKind.Relative),
            new ReplaceUserRolesRequest([]));

        emptied.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = JsonDocument.Parse(await emptied.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("code").GetString().ShouldBe("conflict");

        // A role that does not grant user.manage does not keep administration possible.
        var editorial = await admin.PutAsJsonAsync(
            new Uri($"/users/{AdministrationFixture.SuperAdmin}/roles", UriKind.Relative),
            new ReplaceUserRolesRequest(["editor"]));

        editorial.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Keeping an administrative role is accepted.
        var kept = await admin.PutAsJsonAsync(
            new Uri($"/users/{AdministrationFixture.SuperAdmin}/roles", UriKind.Relative),
            new ReplaceUserRolesRequest(["superadmin"]));

        kept.StatusCode.ShouldBe(HttpStatusCode.OK, await kept.Content.ReadAsStringAsync());

        // Blocking or deleting yourself is refused for the same reason.
        (await admin.PatchAsJsonAsync(
                new Uri($"/users/{AdministrationFixture.SuperAdmin}", UriKind.Relative),
                new { isBlocked = true }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await admin.DeleteAsync(new Uri($"/users/{AdministrationFixture.SuperAdmin}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Every_user_change_is_audited_with_the_actor_and_the_time()
    {
        using var admin = ClientFor(AdministrationFixture.SuperAdmin);
        var subject = Guid.NewGuid();
        await CreateUserAsync(admin, subject, "audited@example.test", "Audited", ["editor"]);

        await admin.PatchAsJsonAsync(new Uri($"/users/{subject}", UriKind.Relative), new { displayName = "Renamed" });
        await admin.PutAsJsonAsync(
            new Uri($"/users/{subject}/roles", UriKind.Relative),
            new ReplaceUserRolesRequest(["editor", "negotiator"]));
        (await admin.DeleteAsync(new Uri($"/users/{subject}", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var changes = await fixture.UserChangesAsync(subject);

        changes.Select(change => change.Action).ShouldBe(["created", "updated", "updated", "deleted"]);
        changes.ShouldAllBe(change => change.Module == "identity");
        changes.ShouldAllBe(change => change.ActorId == AdministrationFixture.SuperAdmin);
        changes.ShouldAllBe(change => change.OccurredAt > DateTimeOffset.MinValue);
        changes.Last().Changes.ShouldContain("roles");
    }

    [Fact]
    public async Task The_audit_trail_is_invisible_without_the_capability()
    {
        using var admin = ClientFor(AdministrationFixture.SuperAdmin);
        var subject = Guid.NewGuid();
        await CreateUserAsync(admin, subject, "audit-reader@example.test", "Audit Reader", ["editor"]);

        using var editor = ClientFor(subject);

        var forbidden = await editor.GetAsync(new Uri("/audit", UriKind.Relative));
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var audit = await admin.GetFromJsonAsync<AuditListResponse>(
            new Uri($"/audit?entityType=user_account&entityId={subject}", UriKind.Relative));

        audit.ShouldNotBeNull();
        audit!.Items.ShouldNotBeEmpty();
        audit.Items.ShouldAllBe(entry => entry.ActorId == AdministrationFixture.SuperAdmin);
        audit.Items[0].ActorDisplayName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task No_response_carries_a_password()
    {
        using var admin = ClientFor(AdministrationFixture.SuperAdmin);
        var subject = Guid.NewGuid();

        // A client that sends a password field is ignored: the request type has no such member.
        var response = await admin.PostAsJsonAsync(
            new Uri("/users", UriKind.Relative),
            new
            {
                subject,
                email = "no-password@example.test",
                displayName = "No Password",
                roleKeys = new[] { "editor" },
                password = "hunter2",
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var responses = new List<string>
        {
            await response.Content.ReadAsStringAsync(),
            await admin.GetStringAsync(new Uri($"/users/{subject}", UriKind.Relative)),
            await admin.GetStringAsync(new Uri($"/users?q=no-password", UriKind.Relative)),
            await admin.GetStringAsync(new Uri("/audit?entityType=user_account", UriKind.Relative)),
            await admin.GetStringAsync(new Uri("/me", UriKind.Relative)),
        };

        foreach (var json in responses)
        {
            using var document = JsonDocument.Parse(json);
            PropertyNames(document.RootElement).ShouldNotContain(
                name => name.Contains("password", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task The_role_matrix_and_the_capability_catalogue_require_role_manage()
    {
        using var admin = ClientFor(AdministrationFixture.SuperAdmin);
        var subject = Guid.NewGuid();
        await CreateUserAsync(admin, subject, "matrix@example.test", "Matrix", ["editor"]);

        using var editor = ClientFor(subject);
        (await editor.GetAsync(new Uri("/roles", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        var roles = await admin.GetFromJsonAsync<List<RoleResponse>>(new Uri("/roles", UriKind.Relative));

        roles.ShouldNotBeNull();
        roles!.ShouldContain(role => role.Key == "superadmin");
        roles.Single(role => role.Key == "superadmin").Capabilities.ShouldContain("user.manage");
        roles.ShouldContain(role => role.IsSystem);

        var capabilities = await admin.GetFromJsonAsync<List<CapabilityResponse>>(
            new Uri("/capabilities", UriKind.Relative));

        capabilities.ShouldNotBeNull();
        capabilities!.ShouldContain(capability => capability.Key == "user.manage");
    }

    [Fact]
    public async Task An_unknown_role_is_a_validation_error_and_a_duplicate_subject_is_a_conflict()
    {
        using var admin = ClientFor(AdministrationFixture.SuperAdmin);

        var unknownRole = await admin.PostAsJsonAsync(
            new Uri("/users", UriKind.Relative),
            new CreateUserRequest(Guid.NewGuid(), "unknown-role@example.test", "Unknown", null, null, ["ghost"], null, null));

        unknownRole.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(unknownRole)).Code.ShouldBe("validation_failed");

        var duplicate = await admin.PostAsJsonAsync(
            new Uri("/users", UriKind.Relative),
            new CreateUserRequest(
                AdministrationFixture.SuperAdmin, "duplicate@example.test", "Duplicate", null, null, ["editor"], null, null));

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(duplicate)).Code.ShouldBe("conflict");
    }

    [Fact]
    public async Task Users_require_user_manage()
    {
        using var admin = ClientFor(AdministrationFixture.SuperAdmin);
        var subject = Guid.NewGuid();
        await CreateUserAsync(admin, subject, "no-manage@example.test", "No Manage", ["editor"]);

        using var editor = ClientFor(subject);

        (await editor.GetAsync(new Uri("/users", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await editor.GetAsync(new Uri($"/users/{subject}", UriKind.Relative))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private HttpClient ClientFor(Guid subject)
    {
        var factory = new ApiFactory(fixture.ConnectionString);
        _hosts.Add(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(subject));
        return client;
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }
    }

    private static async Task<UserDetail> CreateUserAsync(
        HttpClient client,
        Guid subject,
        string email,
        string displayName,
        string[] roles)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/users", UriKind.Relative),
            new CreateUserRequest(subject, email, displayName, null, null, roles, null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var user = await response.Content.ReadFromJsonAsync<UserDetail>();
        user.ShouldNotBeNull();
        return user!;
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;

                    foreach (var nested in PropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in PropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static async Task<(string? Code, string? Detail)> ReadProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        return (
            root.TryGetProperty("code", out var code) ? code.GetString() : null,
            root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
    }
}
