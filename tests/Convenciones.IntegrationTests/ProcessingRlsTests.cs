using System;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ProcessingRlsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Guid _companyA;
    private Guid _companyB;
    private Guid _userA;
    private Guid _userB;
    private Guid _documentA;
    private Guid _documentB;
    private Guid _runA;
    private Guid _runB;
    private Guid _clauseA;
    private Guid _clauseB;

    public async Task InitializeAsync()
    {
        _companyA = Guid.NewGuid();
        _companyB = Guid.NewGuid();
        _userA = Guid.NewGuid();
        _userB = Guid.NewGuid();
        _documentA = Guid.NewGuid();
        _documentB = Guid.NewGuid();
        _runA = Guid.NewGuid();
        _runB = Guid.NewGuid();
        _clauseA = Guid.NewGuid();
        _clauseB = Guid.NewGuid();

        await ExecuteAsync(
            $"insert into app.company (id, name) values ('{_companyA}', 'RLS A'), ('{_companyB}', 'RLS B');" +
            $"insert into platform.user_account (id, email, display_name) values " +
            $"('{_userA}', 'rls-a@example.test', 'RLS A'), ('{_userB}', 'rls-b@example.test', 'RLS B');" +
            $"insert into platform.user_profile (user_id, company_id) values ('{_userA}'::uuid, '{_companyA}'::uuid), ('{_userB}'::uuid, '{_companyB}'::uuid);" +
            $"insert into platform.role_capability (role_id, capability_id) " +
            $"select r.id, c.id from platform.role r join platform.capability c on c.key in ('clause.read', 'clause.process', 'clause.review') " +
            $"where r.key = 'editor' and not exists (select 1 from platform.role_capability rc where rc.role_id = r.id and rc.capability_id = c.id);" +
            $"insert into platform.user_role (user_id, role_id) " +
            $"select '{_userA}'::uuid, id from platform.role where key = 'editor' union all " +
            $"select '{_userB}'::uuid, id from platform.role where key = 'editor';" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) values " +
            $"('{_documentA}', 'rls/a.pdf', 'a.pdf', 'application/pdf', 1024, '{_userA}', '{_companyA}'), " +
            $"('{_documentB}', 'rls/b.pdf', 'b.pdf', 'application/pdf', 1024, '{_userB}', '{_companyB}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values " +
            $"('{_runA}', '{_documentA}', '{_companyA}'), ('{_runB}', '{_documentB}', '{_companyB}');" +
            $"insert into platform.extracted_clause (id, run_id, document_id, company_id, ordinal, text, page_from, page_to) values " +
            $"('{_clauseA}', '{_runA}', '{_documentA}', '{_companyA}', 1, 'Cláusula A', 1, 1), " +
            $"('{_clauseB}', '{_runB}', '{_documentB}', '{_companyB}', 1, 'Cláusula B', 1, 1);" +
            $"insert into platform.clause_classification (clause_id, category_id, company_id) " +
            $"select '{_clauseA}', id, '{_companyA}' from platform.clause_category where key = 'pricing';" +
            $"insert into platform.clause_classification (clause_id, category_id, company_id) " +
            $"select '{_clauseB}', id, '{_companyB}' from platform.clause_category where key = 'pricing';");
    }

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"delete from platform.processing_run where company_id in ('{_companyA}', '{_companyB}');");
        await ExecuteAsync($"delete from platform.document where company_id in ('{_companyA}', '{_companyB}');");
        await ExecuteAsync($"delete from platform.user_account where id in ('{_userA}', '{_userB}');");
        await ExecuteAsync($"delete from app.company where id in ('{_companyA}', '{_companyB}');");
    }

    [Fact]
    public async Task Company_A_cannot_read_or_write_company_B_processing_rows()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteAsync(connection, transaction, "set local role app_api;");
        await ExecuteAsync(connection, transaction,
            $"select set_config('request.jwt.claims', '{{\"sub\":\"{_userA}\"}}', true);");

        (await ScalarAsync(connection, transaction, "select count(*) from platform.processing_run;"))
            .ShouldBe("1");
        (await ScalarAsync(connection, transaction, $"select count(*) from platform.extracted_clause where id = '{_clauseB}';"))
            .ShouldBe("0");
        (await ScalarAsync(connection, transaction, $"select count(*) from platform.clause_classification where clause_id = '{_clauseB}';"))
            .ShouldBe("0");
        (await ScalarAsync(connection, transaction, "select count(*) from platform.clause_category;"))
            .ShouldBe("14");

        var updated = await ExecuteCountAsync(
            connection,
            transaction,
            $"update platform.extracted_clause set review_status = 'Accepted' where id = '{_clauseB}';");
        updated.ShouldBe(0);

        var denied = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(
            connection,
            transaction,
            $"insert into platform.processing_run (document_id, company_id) values ('{_documentB}', '{_companyB}');"));
        denied.SqlState.ShouldBe("42501");

        await transaction.RollbackAsync();
    }

    private static async Task<int> ExecuteCountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
