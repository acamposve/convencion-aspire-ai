using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// A database with the negotiation schema, two companies, the capability shapes the matrix needs, and a
/// heading to classify petitions with. Each test class owns its container.
/// </summary>
public sealed class NegotiationFixture : IAsyncLifetime
{
    public static readonly Guid CompanyA = new("e3000000-0000-0000-0000-000000000001");
    public static readonly Guid CompanyB = new("e3000000-0000-0000-0000-000000000002");

    /// <summary>Negotiator of company A: read, write and promote.</summary>
    public static readonly Guid NegotiatorA = new("e4000000-0000-0000-0000-000000000001");

    /// <summary>Negotiator of company A without promotion: read and write only.</summary>
    public static readonly Guid WriterWithoutPromote = new("e4000000-0000-0000-0000-000000000002");

    /// <summary>Editor of company A: no negotiation capability at all.</summary>
    public static readonly Guid EditorA = new("e4000000-0000-0000-0000-000000000003");

    /// <summary>Negotiator of company B: the caller who must see nothing of company A.</summary>
    public static readonly Guid NegotiatorB = new("e4000000-0000-0000-0000-000000000004");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("convenciones_negotiation")
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

    public async Task<CaseRow> CaseRowAsync(Guid caseId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select state::text, closed_at, promoted_agreement_id
            from app.negotiation_case where id = @id
            """, connection);
        command.Parameters.AddWithValue("id", caseId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException($"No negotiation case '{caseId}'.");
        }

        return new CaseRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    public async Task<int> AgreementCountAsync(Guid companyId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select count(*) from app.agreement where company_id = @company", connection);
        command.Parameters.AddWithValue("company", companyId);

        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    public async Task<AgreementRow> AgreementRowAsync(Guid agreementId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select company_id, valid_from, valid_to, scope, state::text
            from app.agreement where id = @id
            """, connection);
        command.Parameters.AddWithValue("id", agreementId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException($"No agreement '{agreementId}'.");
        }

        return new AgreementRow(
            reader.GetGuid(0),
            reader.GetFieldValue<DateOnly>(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateOnly>(2),
            reader.GetString(3),
            reader.GetString(4));
    }

    public async Task<IReadOnlyList<AuditRow>> AuditRowsAsync(string entityType, Guid entityId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            select module, action, from_state, to_state, actor_id
            from platform.audit_entry
            where entity_type = @type and entity_id = @id
            order by id
            """, connection);
        command.Parameters.AddWithValue("type", entityType);
        command.Parameters.AddWithValue("id", entityId.ToString());

        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new AuditRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4)));
        }

        return rows;
    }

    /// <summary>Inserts a case row directly, for the database-level tests that bypass the API.</summary>
    public async Task InsertCaseAsync(Guid caseId, Guid companyId, string state, bool closed)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            insert into app.negotiation_case (id, company_id, started_on, state, closed_at)
            values (@id, @company, '2026-01-01', @state::app.negotiation_case_state, case when @closed then now() else null end)
            """, connection);
        command.Parameters.AddWithValue("id", caseId);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("closed", closed);

        (await command.ExecuteNonQueryAsync().ConfigureAwait(false)).ShouldBe(1);
    }

    /// <summary>Inserts an agreement header directly, as the promotion target for the SQL-level tests.</summary>
    public async Task InsertAgreementAsync(Guid agreementId, Guid companyId, DateOnly validFrom, string scope = "Nacional")
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            insert into app.agreement (id, company_id, valid_from, scope, state)
            values (@id, @company, @from, @scope, 'Draft')
            """, connection);
        command.Parameters.AddWithValue("id", agreementId);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("from", validFrom);
        command.Parameters.AddWithValue("scope", scope);

        (await command.ExecuteNonQueryAsync().ConfigureAwait(false)).ShouldBe(1);
    }

    /// <summary>Runs one statement as the given user and commits it, to arrange a state for a test.</summary>
    public async Task ExecuteAsUserAsync(Guid userId, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        var claims = $$"""{"sub":"{{userId}}","role":"authenticated"}""";

        await using (var setClaims = new NpgsqlCommand(
            "select set_config('request.jwt.claims', @claims, true)", connection, transaction))
        {
            setClaims.Parameters.AddWithValue("claims", claims);
            await setClaims.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var setRole = new NpgsqlCommand("set local role app_api", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            (await command.ExecuteNonQueryAsync().ConfigureAwait(false)).ShouldBeGreaterThan(0);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>Runs one scalar query as the given user through the API's database role.</summary>
    public async Task<long> ScalarAsUserAsync(Guid userId, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        var claims = $$"""{"sub":"{{userId}}","role":"authenticated"}""";

        await using (var setClaims = new NpgsqlCommand(
            "select set_config('request.jwt.claims', @claims, true)", connection, transaction))
        {
            setClaims.Parameters.AddWithValue("claims", claims);
            await setClaims.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var setRole = new NpgsqlCommand("set local role app_api", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        long value;

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            value = Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false));
        }

        await transaction.RollbackAsync().ConfigureAwait(false);
        return value;
    }

    /// <summary>Runs one statement as the given user through the API's database role.</summary>
    /// <returns>The provider exception, or null when the statement succeeded.</returns>
    public async Task<PostgresException?> TryAsUserAsync(Guid userId, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        try
        {
            var claims = $$"""{"sub":"{{userId}}","role":"authenticated"}""";

            await using (var setClaims = new NpgsqlCommand(
                "select set_config('request.jwt.claims', @claims, true)", connection, transaction))
            {
                setClaims.Parameters.AddWithValue("claims", claims);
                await setClaims.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await using (var setRole = new NpgsqlCommand("set local role app_api", connection, transaction))
            {
                await setRole.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            return null;
        }
        catch (PostgresException exception)
        {
            return exception;
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes the case UPDATE fail after the agreement has been inserted, so the rollback can be proved.
    /// </summary>
    public async Task InstallForcedFailureTriggerAsync()
    {
        await ExecuteAsync(
            """
            create or replace function app.reject_case_update() returns trigger
            language plpgsql as $$
            begin
                raise exception 'forced failure for the rollback test'
                    using errcode = 'invalid_parameter_value';
            end;
            $$;
            drop trigger if exists negotiation_case_forced_failure on app.negotiation_case;
            create trigger negotiation_case_forced_failure
                before update on app.negotiation_case
                for each row execute function app.reject_case_update();
            """).ConfigureAwait(false);
    }

    public async Task RemoveForcedFailureTriggerAsync()
    {
        await ExecuteAsync(
            """
            drop trigger if exists negotiation_case_forced_failure on app.negotiation_case;
            drop function if exists app.reject_case_update();
            """).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task SeedAsync()
    {
        const string sql = """
            insert into app.company (id, name, country_id) values
                ('e3000000-0000-0000-0000-000000000001', 'Negotiation Company A',
                 (select id from app.country where code = 'VE')),
                ('e3000000-0000-0000-0000-000000000002', 'Negotiation Company B',
                 (select id from app.country where code = 'VE'));

            -- A negotiator without negotiation.promote: no seeded role has that shape, and the
            -- promotion matrix needs it.
            insert into platform.role (id, key, name)
            values ('e6000000-0000-0000-0000-000000000001', 'negotiator_no_promote',
                    'Negotiator Without Promotion');

            insert into platform.role_capability (role_id, capability_id)
            select 'e6000000-0000-0000-0000-000000000001', id
            from platform.capability
            where key in ('negotiation.read', 'negotiation.write');

            insert into platform.user_account (id, email, display_name, is_blocked, expires_on) values
                ('e4000000-0000-0000-0000-000000000001', 'neg.a@example.test', 'Negotiator A', false, null),
                ('e4000000-0000-0000-0000-000000000002', 'neg.w@example.test', 'Writer Without Promote', false, null),
                ('e4000000-0000-0000-0000-000000000003', 'neg.e@example.test', 'Editor A', false, null),
                ('e4000000-0000-0000-0000-000000000004', 'neg.b@example.test', 'Negotiator B', false, null);

            insert into platform.user_role (user_id, role_id)
            select v.user_id, r.id
            from (values
                ('e4000000-0000-0000-0000-000000000001'::uuid, 'negotiator'),
                ('e4000000-0000-0000-0000-000000000002'::uuid, 'negotiator_no_promote'),
                ('e4000000-0000-0000-0000-000000000003'::uuid, 'editor'),
                ('e4000000-0000-0000-0000-000000000004'::uuid, 'negotiator')
            ) as v(user_id, role_key)
            join platform.role r on r.key = v.role_key;

            insert into platform.user_profile (user_id, company_id) values
                ('e4000000-0000-0000-0000-000000000001', 'e3000000-0000-0000-0000-000000000001'),
                ('e4000000-0000-0000-0000-000000000002', 'e3000000-0000-0000-0000-000000000001'),
                ('e4000000-0000-0000-0000-000000000003', 'e3000000-0000-0000-0000-000000000001'),
                ('e4000000-0000-0000-0000-000000000004', 'e3000000-0000-0000-0000-000000000002');

            insert into app.comparison_heading (group_id, name, description, is_excluded, sort_order)
            select g.id, 'Salarios', 'Wage clauses', false, 10
            from app.comparison_heading_group g
            where g.name = 'GENERALES'
              and not exists (
                  select 1 from app.comparison_heading h
                  where h.group_id = g.id and lower(h.name) = lower('Salarios')
              );
            """;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        (await command.ExecuteNonQueryAsync().ConfigureAwait(false)).ShouldBeGreaterThan(0);
    }
}

[CollectionDefinition(Name)]
public sealed class NegotiationCollection : ICollectionFixture<NegotiationFixture>
{
    public const string Name = "negotiation";
}

public sealed record CaseRow(string State, DateTimeOffset? ClosedAt, Guid? PromotedAgreementId);

public sealed record AgreementRow(
    Guid CompanyId,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string Scope,
    string State);

public sealed record AuditRow(
    string Module,
    string Action,
    string? FromState,
    string? ToState,
    Guid? ActorId);
