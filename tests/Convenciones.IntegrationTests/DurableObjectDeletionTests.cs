using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Api.Infrastructure;
using Convenciones.Documents;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-004: the deletion intent commits with the soft delete, and object removal is retried durably.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DurableObjectDeletionTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<Guid> _companies = [];
    private readonly List<Guid> _users = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // One connection for the whole teardown: the replication-role switch is session-scoped.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await ExecuteAsync(connection, "set session_replication_role = replica;");

        foreach (var company in _companies)
        {
            await ExecuteAsync(connection,
                $"delete from platform.document_object_deletion where company_id = '{company}';" +
                $"delete from platform.processing_run where company_id = '{company}';" +
                $"delete from platform.document where company_id = '{company}';");
        }

        foreach (var user in _users)
        {
            await ExecuteAsync(connection,
                $"delete from platform.audit_entry where actor_id = '{user}';" +
                $"delete from platform.user_role where user_id = '{user}';" +
                $"delete from platform.user_profile where user_id = '{user}';" +
                $"delete from platform.user_account where id = '{user}';");
        }

        foreach (var company in _companies)
        {
            await ExecuteAsync(connection, $"delete from app.company where id = '{company}';");
        }

        await ExecuteAsync(connection, "set session_replication_role = origin;");
        await ExecuteAsync(connection, "delete from platform.role where key = 'p1004_editor';");
    }
    [Fact]
    public async Task A_storage_failure_leaves_a_pending_intent_and_returns_204()
    {
        var seeded = await SeedAsync();
        var storage = new FakeDocumentStorage { Failure = new HttpRequestException("storage unavailable") };
        using var client = Client(seeded.User, storage);

        var response = await DeleteAsync(client, seeded.Document);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var intent = await ReadIntentAsync(seeded.Document);
        intent.State.ShouldBe("Pending");
        intent.Attempts.ShouldBe(1);
        intent.LastError.ShouldNotBeNullOrWhiteSpace();
        intent.CompletedAt.ShouldBeNull();

        var deletedAt = await ScalarAsync($"select coalesce(deleted_at::text, 'none') from platform.document where id = '{seeded.Document}';");
        deletedAt.ShouldNotBe("none");
    }

    [Fact]
    public async Task The_sweeper_completes_a_pending_intent_once_storage_recovers()
    {
        var seeded = await SeedAsync();
        var storage = new FakeDocumentStorage { Failure = new HttpRequestException("storage unavailable") };
        using var client = Client(seeded.User, storage);
        (await DeleteAsync(client, seeded.Document)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        storage.Failure = null;
        await DueNowAsync(seeded.Document);

        var processed = await Store().SweepOnceAsync(10, TimeSpan.FromMinutes(1), storage, CancellationToken.None);

        processed.ShouldBeGreaterThanOrEqualTo(1);
        var intent = await ReadIntentAsync(seeded.Document);
        intent.State.ShouldBe("Completed");
        intent.CompletedAt.ShouldNotBeNull();
        storage.Deleted.ShouldContain(seeded.Path);
    }

    [Fact]
    public async Task A_missing_object_is_recorded_as_completed()
    {
        var seeded = await SeedAsync();
        var storage = new FakeDocumentStorage();
        using var client = Client(seeded.User, storage);

        (await DeleteAsync(client, seeded.Document)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var intent = await ReadIntentAsync(seeded.Document);
        intent.State.ShouldBe("Completed");
        intent.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_crash_after_the_commit_is_retried_by_the_sweeper()
    {
        var seeded = await SeedAsync();
        var storage = new FakeDocumentStorage();

        // Simulate a process that committed the soft delete and intent but never ran the storage call.
        await ExecuteAsync(
            $"update platform.document set deleted_at = now() where id = '{seeded.Document}';" +
            $"insert into platform.document_object_deletion (document_id, company_id, storage_path) " +
            $"values ('{seeded.Document}', '{seeded.Company}', '{seeded.Path}');");

        var processed = await Store().SweepOnceAsync(10, TimeSpan.FromMinutes(1), storage, CancellationToken.None);

        processed.ShouldBeGreaterThanOrEqualTo(1);
        (await ReadIntentAsync(seeded.Document)).State.ShouldBe("Completed");
        storage.Deleted.ShouldContain(seeded.Path);
    }

    [Fact]
    public async Task A_failing_sweep_keeps_the_intent_pending_with_backoff()
    {
        var seeded = await SeedAsync();
        var storage = new FakeDocumentStorage { Failure = new HttpRequestException("still down") };

        await ExecuteAsync(
            $"update platform.document set deleted_at = now() where id = '{seeded.Document}';" +
            $"insert into platform.document_object_deletion (document_id, company_id, storage_path, next_attempt_at) " +
            $"values ('{seeded.Document}', '{seeded.Company}', '{seeded.Path}', now() - interval '1 minute');");

        var processed = await Store().SweepOnceAsync(10, TimeSpan.FromMinutes(1), storage, CancellationToken.None);

        processed.ShouldBeGreaterThanOrEqualTo(1);
        var intent = await ReadIntentAsync(seeded.Document);
        intent.State.ShouldBe("Pending");
        intent.Attempts.ShouldBe(1);
        intent.LastError.ShouldNotBeNullOrWhiteSpace();

        var nextAttempt = await ScalarAsync(
            $"select (next_attempt_at > now())::text from platform.document_object_deletion where document_id = '{seeded.Document}';");
        nextAttempt.ShouldBe("true", "a failed attempt schedules the next retry in the future");
    }

    [Fact]
    public async Task Deleting_twice_is_not_found_and_does_not_duplicate_the_intent()
    {
        var seeded = await SeedAsync();
        var storage = new FakeDocumentStorage();
        using var client = Client(seeded.User, storage);

        var first = await DeleteAsync(client, seeded.Document);
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var second = await SendDeleteAsync(client, seeded.Document, seeded.RowVersion);
        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ScalarAsync(
            $"select count(*) from platform.document_object_deletion where document_id = '{seeded.Document}';"))
            .ShouldBe("1");
    }

    [Fact]
    public async Task Another_tenant_cannot_read_the_deletion_intent()
    {
        var seeded = await SeedAsync();
        var otherUser = Guid.NewGuid();
        var otherCompany = Guid.NewGuid();
        _companies.Add(otherCompany);
        _users.Add(otherUser);

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{otherCompany}', 'Deletion Other');" +
            $"insert into platform.user_account (id, email, display_name) values ('{otherUser}', 'p1004-other-{otherUser:N}@example.test', 'Other');" +
            $"insert into platform.user_profile (user_id, company_id) values ('{otherUser}', '{otherCompany}');");

        var storage = new FakeDocumentStorage();
        using var client = Client(seeded.User, storage);
        (await DeleteAsync(client, seeded.Document)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "set role app_api;");
        await ExecuteAsync(connection, $"select set_config('request.jwt.claims', '{{\"sub\":\"{otherUser}\"}}', false);");

        (await ScalarAsync(connection, "select count(*) from platform.document_object_deletion;")).ShouldBe("0");
        (await ScalarAsync(connection,
            $"select count(*) from platform.document_object_deletion where company_id = '{seeded.Company}';")).ShouldBe("0");
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Seeded(Guid Company, Guid User, Guid Document, string Path, uint RowVersion);

    private sealed record Intent(string State, int Attempts, string? LastError, string? CompletedAt);

    private DocumentDeletionStore Store() => new(fixture.ConnectionString);

    private HttpClient Client(Guid user, FakeDocumentStorage storage)
    {
        var client = new ApiFactory(fixture.ConnectionString, storage: storage).CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuth.Token(user));
        return client;
    }

    private async Task<Seeded> SeedAsync()
    {
        var company = Guid.NewGuid();
        var user = Guid.NewGuid();
        var document = Guid.NewGuid();
        var path = $"p1004/{document:N}.pdf";
        _companies.Add(company);
        _users.Add(user);
        var suffix = document.ToString("N")[..8];

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{company}', 'Deletion {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{user}', 'p1004-{suffix}@example.test', 'Deletion');" +
            $"insert into platform.user_profile (user_id, company_id) values ('{user}', '{company}');" +
            $"insert into platform.role (key, name, description, is_system) values ('p1004_editor', 'P1004 Editor', 'deletion tests', true) on conflict do nothing;" +
            $"insert into platform.role_capability (role_id, capability_id) " +
            $"select r.id, c.id from platform.role r join platform.capability c on c.key in ('company.read', 'document.read', 'document.write') " +
            $"where r.key = 'p1004_editor' and not exists (select 1 from platform.role_capability rc where rc.role_id = r.id and rc.capability_id = c.id);" +
            $"insert into platform.user_role (user_id, role_id) select '{user}', id from platform.role where key = 'p1004_editor';" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{document}', '{path}', 'deletion.pdf', 'application/pdf', 10, '{user}', '{company}');");

        using var client = Client(user, new FakeDocumentStorage());
        var detail = await client.GetAsync(new Uri($"/documents/{document}", UriKind.Relative));
        detail.StatusCode.ShouldBe(HttpStatusCode.OK, await detail.Content.ReadAsStringAsync());

        using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var rowVersion = json.RootElement.GetProperty("rowVersion").GetUInt32();

        return new Seeded(company, user, document, path, rowVersion);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid document)
    {
        // The caller reads the current rowVersion first, exactly as the API requires.
        var detail = await client.GetAsync(new Uri($"/documents/{document}", UriKind.Relative));
        using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var rowVersion = json.RootElement.GetProperty("rowVersion").GetUInt32();
        return await SendDeleteAsync(client, document, rowVersion);
    }

    private static async Task<HttpResponseMessage> SendDeleteAsync(HttpClient client, Guid document, uint rowVersion)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/documents/{document}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("If-Match", rowVersion.ToString(CultureInfo.InvariantCulture));
        return await client.SendAsync(request);
    }

    private async Task<Intent> ReadIntentAsync(Guid document)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"select state, attempts, last_error, completed_at::text from platform.document_object_deletion where document_id = '{document}';",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue("a deletion intent must exist");
        return new Intent(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private async Task DueNowAsync(Guid document) =>
        await ExecuteAsync(
            $"update platform.document_object_deletion set next_attempt_at = now() - interval '1 minute' where document_id = '{document}';");

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return await ScalarAsync(connection, sql);
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    internal sealed class FakeDocumentStorage : IDocumentStorage
    {
        public Exception? Failure { get; set; }

        public List<string> Deleted { get; } = [];

        public Task SaveAsync(string key, Stream content, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<Stream?>(null);

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Deleted.Add(key);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }
}
