using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// A database with the agreement schema, two companies, the roles needed for the capability matrix,
/// and a small heading taxonomy. Each test class owns its container, so counts cannot leak across
/// fixtures.
/// </summary>
public sealed class AgreementFixture : IAsyncLifetime
{
    public static readonly Guid CompanyA = new("c3000000-0000-0000-0000-000000000001");
    public static readonly Guid CompanyB = new("c3000000-0000-0000-0000-000000000002");

    public static readonly Guid EditorA = new("c4000000-0000-0000-0000-000000000001");
    public static readonly Guid EditorB = new("c4000000-0000-0000-0000-000000000002");
    public static readonly Guid Transcriptor = new("c4000000-0000-0000-0000-000000000003");
    public static readonly Guid Negotiator = new("c4000000-0000-0000-0000-000000000004");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("convenciones_agreements")
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

    public async Task<string> CompanyNameAsync(Guid companyId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select name from app.company where id = @id", connection);
        command.Parameters.AddWithValue("id", companyId);

        return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    public async Task SetCompanyNameAsync(Guid companyId, string name)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "update app.company set name = @name where id = @id", connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("id", companyId);

        (await command.ExecuteNonQueryAsync().ConfigureAwait(false)).ShouldBe(1);
    }

    public async Task<StoredAgreement> AgreementRowAsync(Guid agreementId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select state::text, published_at, published_by, scope, valid_from, valid_to
            from app.agreement where id = @id
            """, connection);
        command.Parameters.AddWithValue("id", agreementId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        (await reader.ReadAsync().ConfigureAwait(false)).ShouldBeTrue($"agreement {agreementId} must exist");

        return new StoredAgreement(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.GetFieldValue<DateOnly>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5));
    }

    public async Task<int> AuditCountAsync(Guid agreementId, string entityType = "agreement")
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select count(*) from platform.audit_entry
            where entity_type = @type and entity_id = @id
            """, connection);
        command.Parameters.AddWithValue("type", entityType);
        command.Parameters.AddWithValue("id", agreementId.ToString());

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<string>> AuditChangesAsync(Guid agreementId, string entityType = "agreement")
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select coalesce(changes::text, '') from platform.audit_entry
            where entity_type = @type and entity_id = @id order by id
            """, connection);
        command.Parameters.AddWithValue("type", entityType);
        command.Parameters.AddWithValue("id", agreementId.ToString());

        var changes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            changes.Add(reader.GetString(0));
        }

        return changes;
    }

    public async Task<int> ClauseCountAsync(Guid agreementId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select count(*) from app.agreement_clause where agreement_id = @id", connection);
        command.Parameters.AddWithValue("id", agreementId);

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<int> AgreementCountAsync(Guid companyId, string? state = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select count(*) from app.agreement
            where company_id = @company
              and (@state::text is null or state::text = @state::text)
            """, connection);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("state", (object?)state ?? DBNull.Value);

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<int> AgreementCountForDateAsync(Guid companyId, DateOnly validFrom)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select count(*) from app.agreement where company_id = @company and valid_from = @from",
            connection);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("from", validFrom);

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<int> PublishedCandidateCountAsync(Guid companyId, Guid headingId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select count(distinct a.id)
            from app.agreement a
            join app.agreement_clause c on c.agreement_id = a.id
            where a.company_id = @company
              and a.state = 'Published'
              and c.heading_id = @heading
            """, connection);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("heading", headingId);

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<int> AgreementColumnCountAsync(params string[] names)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select count(*) from information_schema.columns
            where table_schema = 'app' and table_name = 'agreement' and column_name = any(@names)
            """, connection);
        command.Parameters.AddWithValue("names", names);

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    private async Task SeedAsync()
    {
        const string sql = """
            insert into app.company (id, name, country_id) values
                ('c3000000-0000-0000-0000-000000000001', 'Agreements Company A',
                 (select id from app.country where code = 'VE')),
                ('c3000000-0000-0000-0000-000000000002', 'Agreements Company B',
                 (select id from app.country where code = 'VE'));

            insert into platform.user_account (id, email, display_name, is_blocked, expires_on) values
                ('c4000000-0000-0000-0000-000000000001', 'agreements.a@example.test', 'Agreements Editor A', false, null),
                ('c4000000-0000-0000-0000-000000000002', 'agreements.b@example.test', 'Agreements Editor B', false, null),
                ('c4000000-0000-0000-0000-000000000003', 'agreements.t@example.test', 'Agreements Transcriptor', false, null),
                ('c4000000-0000-0000-0000-000000000004', 'agreements.n@example.test', 'Agreements Reader', false, null);

            insert into platform.user_role (user_id, role_id)
            select u.id, r.id from platform.user_account u
            join platform.role r on r.key = 'editor'
            where u.id in ('c4000000-0000-0000-0000-000000000001',
                           'c4000000-0000-0000-0000-000000000002');

            insert into platform.user_role (user_id, role_id)
            select u.id, r.id from platform.user_account u
            join platform.role r on r.key = 'transcriptor'
            where u.id = 'c4000000-0000-0000-0000-000000000003';

            insert into platform.user_role (user_id, role_id)
            select u.id, r.id from platform.user_account u
            join platform.role r on r.key = 'negotiator'
            where u.id = 'c4000000-0000-0000-0000-000000000004';

            insert into platform.user_profile (user_id, company_id) values
                ('c4000000-0000-0000-0000-000000000001', 'c3000000-0000-0000-0000-000000000001'),
                ('c4000000-0000-0000-0000-000000000002', 'c3000000-0000-0000-0000-000000000002'),
                ('c4000000-0000-0000-0000-000000000003', 'c3000000-0000-0000-0000-000000000001'),
                ('c4000000-0000-0000-0000-000000000004', 'c3000000-0000-0000-0000-000000000001');

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

            insert into app.comparison_heading (group_id, name, description, is_excluded, sort_order)
            select g.id, 'Bono de alimentación', 'Economic benefit clauses', false, 10
            from app.comparison_heading_group g
            where g.name = 'ECONOMICO'
              and not exists (
                  select 1 from app.comparison_heading h
                  where h.group_id = g.id and lower(h.name) = lower('Bono de alimentación')
              );
            """;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}

[CollectionDefinition(Name)]
public sealed class AgreementsCollection : ICollectionFixture<AgreementFixture>
{
    public const string Name = "agreements";
}

public sealed record StoredAgreement(
    string State,
    DateTimeOffset? PublishedAt,
    Guid? PublishedBy,
    string Scope,
    DateOnly ValidFrom,
    DateOnly? ValidTo);
