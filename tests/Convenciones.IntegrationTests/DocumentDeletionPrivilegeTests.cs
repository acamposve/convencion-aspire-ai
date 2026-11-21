using System;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-004 follow-up: the SECURITY DEFINER deletion helpers must not be callable by `authenticated`.
/// </summary>
/// <remarks>
/// They run with the owner's privileges (outside RLS). With PostgreSQL's default EXECUTE grant to PUBLIC
/// any signed-in user could enumerate other tenants' pending storage paths or mark a deletion completed
/// without removing the object. Execution is restricted to `app_api`; this test pins that boundary.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DocumentDeletionPrivilegeTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Guid _company;
    private Guid _user;
    private Guid _document;

    public async Task InitializeAsync()
    {
        _company = Guid.NewGuid();
        _user = Guid.NewGuid();
        _document = Guid.NewGuid();

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{_company}', 'Deletion privilege');" +
            $"insert into platform.user_account (id, email, display_name) values ('{_user}', 'pinv-{_user:N}@example.test', 'Privilege');" +
            $"insert into platform.user_profile (user_id, company_id) values ('{_user}', '{_company}');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{_document}', 'privilege/{_document:N}.pdf', 'privilege.pdf', 'application/pdf', 1, '{_user}', '{_company}');" +
            $"insert into platform.document_object_deletion (document_id, company_id, storage_path) " +
            $"values ('{_document}', '{_company}', 'privilege/{_document:N}.pdf');");
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync(
            $"delete from platform.document_object_deletion where company_id = '{_company}';" +
            $"delete from platform.document where id = '{_document}';" +
            $"delete from platform.user_profile where user_id = '{_user}';" +
            $"delete from platform.user_account where id = '{_user}';" +
            $"delete from app.company where id = '{_company}';");
    }

    [Fact]
    public async Task Authenticated_cannot_execute_the_deletion_functions()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "set role authenticated;");

        var claim = await Should.ThrowAsync<PostgresException>(() =>
            ExecuteAsync(connection, "select * from platform.claim_document_object_deletions(10, 60);"));
        claim.SqlState.ShouldBe("42501", "authenticated must not be able to lease or read deletion intents");

        var record = await Should.ThrowAsync<PostgresException>(() =>
            ExecuteAsync(connection,
                $"select platform.record_document_object_deletion('{_document}', true, null);"));
        record.SqlState.ShouldBe("42501", "authenticated must not be able to complete a deletion");

        await ExecuteAsync(connection, "reset role;");

        // The intent is untouched by the denied calls.
        (await ScalarAsync($"select state from platform.document_object_deletion where document_id = '{_document}';"))
            .ShouldBe("Pending");
    }

    [Fact]
    public async Task App_api_can_execute_the_deletion_functions()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "set role app_api;");

        var claimed = Convert.ToInt64(await ScalarAsync(connection,
            "select count(*) from platform.claim_document_object_deletions(10, 60);"));
        claimed.ShouldBeGreaterThanOrEqualTo(1, "the API role must be able to claim due deletions");

        await ExecuteAsync(connection,
            $"select platform.record_document_object_deletion('{_document}', true, null);");

        await ExecuteAsync(connection, "reset role;");

        (await ScalarAsync($"select state from platform.document_object_deletion where document_id = '{_document}';"))
            .ShouldBe("Completed");
    }

    // ---------------------------------------------------------------- helpers

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
}
