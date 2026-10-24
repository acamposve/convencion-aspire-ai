using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Confirms the migration files produced the schema the design specifies, against a real database.
/// </summary>
/// <remarks>
/// These assertions duplicate part of the Phase 4 SQL verification on purpose: they prove the
/// migrations still apply from scratch through the same code path the test host uses, which is what
/// a future schema change could silently break.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class SchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Both_schemas_exist()
    {
        var schemas = await QueryAsync<string>(
            "select schema_name from information_schema.schemata where schema_name in ('app', 'platform') order by schema_name");

        schemas.ShouldBe(["app", "platform"]);
    }

    [Fact]
    public async Task Expected_table_count_is_present()
    {
        var count = await QueryAsync<long>(
            "select count(*) from information_schema.tables where table_schema in ('app', 'platform') and table_type = 'BASE TABLE'");

        count.Single().ShouldBe(31L);
    }

    [Fact]
    public async Task Every_table_has_row_level_security_enabled()
    {
        var tablesWithoutRls = await QueryAsync<string>(
            """
            select c.relnamespace::regnamespace::text || '.' || c.relname
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where c.relkind = 'r'
              and n.nspname in ('app', 'platform')
              and c.relrowsecurity = false
            """);

        tablesWithoutRls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Seeded_reference_data_is_present_and_no_user_exists()
    {
        var capabilities = await QueryAsync<long>("select count(*) from platform.capability");
        var roles = await QueryAsync<long>("select count(*) from platform.role");
        var users = await QueryAsync<long>("select count(*) from platform.user_account");

        capabilities.Single().ShouldBe(22L);
        roles.Single().ShouldBe(4L);
        users.Single().ShouldBe(0L, "constitution Art. VI.6 forbids seeding a user account");
    }

    private async Task<List<T>> QueryAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        var results = new List<T>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(reader.GetFieldValue<T>(0));
        }

        return results;
    }
}
