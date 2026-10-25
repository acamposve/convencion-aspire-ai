using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// A database with the identity schema and exactly one user: the bootstrap SuperAdmin.
/// </summary>
/// <remarks>
/// Created through <c>platform.bootstrap_first_admin</c>, the same function the API calls, so the fixture
/// cannot drift from the provisioning path it exercises.
/// </remarks>
public sealed class AdministrationFixture : IAsyncLifetime
{
    public static readonly Guid SuperAdmin = new("a4000000-0000-0000-0000-000000000001");

    public static readonly Guid CompanyA = new("a3000000-0000-0000-0000-000000000001");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("convenciones_administration")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        await TestDatabase.ApplyAsync(ConnectionString).ConfigureAwait(false);
        await TestDatabase.ApplySeedAsync(ConnectionString).ConfigureAwait(false);

        await ExecuteAsync(
            """
            select platform.bootstrap_first_admin(
                'a4000000-0000-0000-0000-000000000001', 'superadmin@example.test', 'Bootstrap SuperAdmin');

            insert into app.company (id, name, country_id) values
                ('a3000000-0000-0000-0000-000000000001', 'Administration Company',
                 (select id from app.country where code = 'VE'));
            """).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<int> UserCountAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select count(*) from platform.user_account", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<UserRow> UserRowAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select a.email, a.display_name, a.is_blocked, a.expires_on,
                   coalesce(array_agg(r.key order by r.key) filter (where r.key is not null), '{}')
            from platform.user_account a
            left join platform.user_role ur on ur.user_id = a.id
            left join platform.role r on r.id = ur.role_id
            where a.id = @id
            group by a.id, a.email, a.display_name, a.is_blocked, a.expires_on
            """, connection);
        command.Parameters.AddWithValue("id", userId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException($"No user '{userId}'.");
        }

        return new UserRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3),
            reader.GetFieldValue<string[]>(4));
    }

    public async Task<IReadOnlyList<UserChangeRow>> UserChangesAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select module, action, actor_id, occurred_at, coalesce(changes::text, '')
            from platform.audit_entry
            where entity_type = 'user_account' and entity_id = @id
            order by id
            """, connection);
        command.Parameters.AddWithValue("id", userId.ToString());

        var rows = new List<UserChangeRow>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new UserChangeRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// A database with the identity schema and **no users**: the bootstrap window is open.
/// </summary>
public sealed class BootstrapFixture : IAsyncLifetime
{
    public static readonly Guid FirstAdmin = new("b4000000-0000-0000-0000-000000000001");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("convenciones_bootstrap")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        await TestDatabase.ApplyAsync(ConnectionString).ConfigureAwait(false);
        await TestDatabase.ApplySeedAsync(ConnectionString).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<int> UserCountAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select count(*) from platform.user_account", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<int> AuditCountAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "select count(*) from platform.audit_entry where entity_type = 'user_account'", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }
}

[CollectionDefinition(Name)]
public sealed class AdministrationCollection : ICollectionFixture<AdministrationFixture>
{
    public const string Name = "administration";
}

[CollectionDefinition(Name)]
public sealed class BootstrapCollection : ICollectionFixture<BootstrapFixture>
{
    public const string Name = "bootstrap";
}

public sealed record UserRow(
    string Email,
    string DisplayName,
    bool IsBlocked,
    DateOnly? ExpiresOn,
    string[] Roles);

public sealed record UserChangeRow(
    string Module,
    string Action,
    Guid? ActorId,
    DateTimeOffset OccurredAt,
    string Changes);
