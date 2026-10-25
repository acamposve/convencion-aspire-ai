using System;
using System.Threading.Tasks;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// A database of its own for the authorization tests.
/// </summary>
/// <remarks>
/// Separate from <see cref="PostgresFixture"/> on purpose: these tests insert user accounts to
/// exercise profile resolution, while <c>SchemaTests</c> asserts that the seed creates **no** user
/// (constitution Art. VI.6). Sharing one database would make those two requirements race.
/// </remarks>
public sealed class IdentityFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("convenciones_identity")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        await TestDatabase.ApplyAsync(ConnectionString).ConfigureAwait(false);
        await TestDatabase.ApplySeedAsync(ConnectionString).ConfigureAwait(false);
        await SeedAccountsAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Two companies and the accounts the tests need: an administrator who may cross companies, two
    /// editors who may not, a blocked account, an expired one, and a subject deliberately left without
    /// an account so "valid token, no profile" can be exercised.
    /// </summary>
    private async Task SeedAccountsAsync()
    {
        const string sql = """
            insert into app.company (id, name, country_id) values
                ('a2000000-0000-0000-0000-000000000001', 'Identity Test Company',
                 (select id from app.country where code = 'VE')),
                ('a2000000-0000-0000-0000-000000000002', 'Other Tenant Company',
                 (select id from app.country where code = 'VE'));

            insert into platform.user_account (id, email, display_name, is_blocked, expires_on) values
                ('a1000000-0000-0000-0000-000000000001', 'active@example.test',   'Active User',  false, null),
                ('a1000000-0000-0000-0000-000000000002', 'blocked@example.test',  'Blocked User', true,  null),
                ('a1000000-0000-0000-0000-000000000003', 'expired@example.test',  'Expired User', false, current_date - 1),
                ('a1000000-0000-0000-0000-000000000005', 'editor.a@example.test', 'Editor A',     false, null),
                ('a1000000-0000-0000-0000-000000000006', 'editor.b@example.test', 'Editor B',     false, null);

            insert into platform.user_role (user_id, role_id)
            select u.id, r.id
            from platform.user_account u
            join platform.role r on r.key = 'administrator'
            where u.id in ('a1000000-0000-0000-0000-000000000001',
                           'a1000000-0000-0000-0000-000000000002',
                           'a1000000-0000-0000-0000-000000000003');

            -- Editors hold company.read but NOT company.read_all: the role that must stay scoped.
            insert into platform.user_role (user_id, role_id)
            select u.id, r.id
            from platform.user_account u
            join platform.role r on r.key = 'editor'
            where u.id in ('a1000000-0000-0000-0000-000000000005',
                           'a1000000-0000-0000-0000-000000000006');

            insert into platform.user_profile (user_id, company_id) values
                ('a1000000-0000-0000-0000-000000000001', 'a2000000-0000-0000-0000-000000000001'),
                ('a1000000-0000-0000-0000-000000000002', 'a2000000-0000-0000-0000-000000000001'),
                ('a1000000-0000-0000-0000-000000000003', 'a2000000-0000-0000-0000-000000000001'),
                ('a1000000-0000-0000-0000-000000000005', 'a2000000-0000-0000-0000-000000000001'),
                ('a1000000-0000-0000-0000-000000000006', 'a2000000-0000-0000-0000-000000000002');
            """;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
    }
}
