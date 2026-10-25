using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// A database with the legal-library schema, two companies, the roles needed for the capability
/// matrix, and a small heading taxonomy. Each test class owns its container, so a statute created by
/// one class cannot leak into another — the comparison tests assert an empty statute list, and that
/// assertion must stay meaningful.
/// </summary>
public sealed class StatuteFixture : IAsyncLifetime
{
    public static readonly Guid CompanyA = new("c5000000-0000-0000-0000-000000000001");
    public static readonly Guid CompanyB = new("c5000000-0000-0000-0000-000000000002");

    /// <summary>Editor of company A: <c>statute.read</c> and <c>statute.write</c>.</summary>
    public static readonly Guid EditorA = new("c6000000-0000-0000-0000-000000000001");

    /// <summary>Editor of company B — the caller who must still see company A's statutes.</summary>
    public static readonly Guid EditorB = new("c6000000-0000-0000-0000-000000000002");

    /// <summary>Negotiator: <c>statute.read</c> only. The write matrix needs this shape.</summary>
    public static readonly Guid Negotiator = new("c6000000-0000-0000-0000-000000000003");

    /// <summary>Editor with no company and no profile — reference data must still be readable.</summary>
    public static readonly Guid EditorWithoutCompany = new("c6000000-0000-0000-0000-000000000004");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("convenciones_legal")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        await TestDatabase.ApplyAsync(ConnectionString).ConfigureAwait(false);
        await TestDatabase.ApplySeedAsync(ConnectionString).ConfigureAwait(false);
        await SeedAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<Guid> HeadingIdAsync(string name)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select id from app.comparison_heading where name = @name and is_active", connection);
        command.Parameters.AddWithValue("name", name);

        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);

        return value is Guid id
            ? id
            : throw new InvalidOperationException($"No active heading named '{name}'.");
    }

    public async Task<int> LabourLawCountAsync() => await StatuteCountAsync("LabourLaw");

    public async Task<int> StatuteCountAsync(string? kind = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select count(*) from app.statute
            where (@kind::text is null or kind::text = @kind::text)
            """, connection);
        command.Parameters.AddWithValue("kind", (object?)kind ?? DBNull.Value);

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<int> ArticleCountAsync(Guid statuteId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select count(*) from app.statute_article where statute_id = @id", connection);
        command.Parameters.AddWithValue("id", statuteId);

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<string> StatuteTextAsync(Guid statuteId, string column)
    {
        var allowed = column is "name" or "source" ? column : throw new ArgumentOutOfRangeException(nameof(column));

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            $"select {allowed} from app.statute where id = @id", connection);
        command.Parameters.AddWithValue("id", statuteId);

        return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    /// <summary>
    /// How many company columns the legal tables carry. Zero is the point: statutes are
    /// company-independent reference data, so a tenant predicate applied to them in code would make
    /// the library invisible.
    /// </summary>
    public async Task<int> CompanyColumnCountAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select count(*) from information_schema.columns
            where table_schema = 'app'
              and table_name in ('statute', 'statute_article')
              and column_name = 'company_id'
            """, connection);

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<string>> AuditActionsAsync(string entityType, string entityId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select action from platform.audit_entry
            where entity_type = @type and entity_id = @id order by id
            """, connection);
        command.Parameters.AddWithValue("type", entityType);
        command.Parameters.AddWithValue("id", entityId);

        var actions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            actions.Add(reader.GetString(0));
        }

        return actions;
    }

    public async Task<IReadOnlyList<string>> AuditModulesAsync(string entityType, string entityId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select module from platform.audit_entry
            where entity_type = @type and entity_id = @id order by id
            """, connection);
        command.Parameters.AddWithValue("type", entityType);
        command.Parameters.AddWithValue("id", entityId);

        var modules = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            modules.Add(reader.GetString(0));
        }

        return modules;
    }

    private async Task SeedAsync()
    {
        const string sql = """
            insert into app.company (id, name, country_id) values
                ('c5000000-0000-0000-0000-000000000001', 'Statute Company A',
                 (select id from app.country where code = 'VE')),
                ('c5000000-0000-0000-0000-000000000002', 'Statute Company B',
                 (select id from app.country where code = 'VE'));

            insert into platform.user_account (id, email, display_name, is_blocked, expires_on) values
                ('c6000000-0000-0000-0000-000000000001', 'legal.a@example.test', 'Statute Editor A', false, null),
                ('c6000000-0000-0000-0000-000000000002', 'legal.b@example.test', 'Statute Editor B', false, null),
                ('c6000000-0000-0000-0000-000000000003', 'legal.n@example.test', 'Statute Negotiator', false, null),
                ('c6000000-0000-0000-0000-000000000004', 'legal.nc@example.test', 'Statute Reader No Company', false, null);

            insert into platform.user_role (user_id, role_id)
            select u.id, r.id from platform.user_account u
            join platform.role r on r.key = 'editor'
            where u.id in ('c6000000-0000-0000-0000-000000000001',
                           'c6000000-0000-0000-0000-000000000002',
                           'c6000000-0000-0000-0000-000000000004');

            insert into platform.user_role (user_id, role_id)
            select u.id, r.id from platform.user_account u
            join platform.role r on r.key = 'negotiator'
            where u.id = 'c6000000-0000-0000-0000-000000000003';

            -- Company A for editors A and the negotiator; company B for editor B. The reader has no
            -- profile row at all: reference data must not depend on a company membership.
            insert into platform.user_profile (user_id, company_id) values
                ('c6000000-0000-0000-0000-000000000001', 'c5000000-0000-0000-0000-000000000001'),
                ('c6000000-0000-0000-0000-000000000002', 'c5000000-0000-0000-0000-000000000002'),
                ('c6000000-0000-0000-0000-000000000003', 'c5000000-0000-0000-0000-000000000001');

            insert into app.comparison_heading (group_id, name, description, is_excluded, sort_order)
            select g.id, v.name, v.description, false, v.sort_order
            from app.comparison_heading_group g
            cross join (values
                ('Salarios', 'Wage and salary clauses', 10),
                ('Vacaciones', 'Holiday clauses', 20)
            ) as v(name, description, sort_order)
            where g.name = 'GENERALES'
              and not exists (
                  select 1 from app.comparison_heading h
                  where h.group_id = g.id and lower(h.name) = lower(v.name)
              );
            """;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        (await command.ExecuteNonQueryAsync().ConfigureAwait(false)).ShouldBeGreaterThan(0);
    }
}

[CollectionDefinition(Name)]
public sealed class StatutesCollection : ICollectionFixture<StatuteFixture>
{
    public const string Name = "statutes";
}
