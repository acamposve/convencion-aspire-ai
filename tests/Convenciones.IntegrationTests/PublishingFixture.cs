using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// A database with the publishing schema, one company, and the two capability shapes the news workflow
/// needs: a full editor and a writer who may not publish.
/// </summary>
/// <remarks>
/// Agreements and their clauses are inserted directly as the owner, because the point of these tests is
/// the anonymous read surface, not the agreement workflow (T8 covers that). Inserting a published
/// agreement bypasses the publication guard on purpose: it is an UPDATE guard, and the fixture needs
/// published data without a session.
/// </remarks>
public sealed class PublishingFixture : IAsyncLifetime
{
    public static readonly Guid CompanyA = new("f3000000-0000-0000-0000-000000000001");

    /// <summary>Editor: news.read, news.write and news.publish.</summary>
    public static readonly Guid Editor = new("f4000000-0000-0000-0000-000000000001");

    /// <summary>News writer without the publish capability.</summary>
    public static readonly Guid WriterWithoutPublish = new("f4000000-0000-0000-0000-000000000002");

    private int _dateSequence = 1;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("convenciones_publishing")
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

    /// <summary>A fresh start date per agreement, because (company, valid_from) is unique.</summary>
    public DateOnly NextStartDate() => new DateOnly(2020, 1, 1).AddDays(_dateSequence++);

    public async Task<Guid> HeadingIdAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select id from app.comparison_heading where is_active order by name limit 1", connection);

        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);

        return value is Guid id
            ? id
            : throw new InvalidOperationException("The seed has no comparison heading.");
    }

    public async Task<Guid> InsertAgreementAsync(
        string state,
        DateTimeOffset? publishedAt,
        Guid? publishedBy,
        Guid? id = null,
        DateOnly? validFrom = null)
    {
        var agreementId = id ?? Guid.NewGuid();

        await ExecuteAsync(
            """
            insert into app.agreement (id, company_id, valid_from, scope, state, published_at, published_by)
            values (@id, @company, @from, 'Nacional', @state::app.publication_state, @published_at, @published_by)
            """,
            ("id", agreementId),
            ("company", CompanyA),
            ("from", validFrom ?? NextStartDate()),
            ("state", state),
            ("published_at", publishedAt),
            ("published_by", publishedBy));

        return agreementId;
    }

    public async Task InsertClauseAsync(Guid agreementId, int number, string title, string body)
    {
        await ExecuteAsync(
            """
            insert into app.agreement_clause (agreement_id, number_int, title, body, heading_id)
            values (@agreement, @number, @title, @body, @heading)
            """,
            ("agreement", agreementId),
            ("number", number),
            ("title", title),
            ("body", body),
            ("heading", await HeadingIdAsync().ConfigureAwait(false)));
    }

    public async Task InsertAnnexAsync(Guid agreementId, string name, string? description, string? body)
    {
        await ExecuteAsync(
            """
            insert into app.agreement_annex (agreement_id, name, description, body, sort_order)
            values (@agreement, @name, @description, @body, 1)
            """,
            ("agreement", agreementId),
            ("name", name),
            ("description", description),
            ("body", body));
    }

    public async Task<PublishedAgreementRow> AgreementRowAsync(Guid agreementId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select state::text, published_at, published_by from app.agreement where id = @id", connection);
        command.Parameters.AddWithValue("id", agreementId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException($"No agreement '{agreementId}'.");
        }

        return new PublishedAgreementRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    public async Task<PublishedNewsRow> NewsRowAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select state::text, published_at, published_by, body from app.news_article where id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException($"No news article '{id}'.");
        }

        return new PublishedNewsRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3));
    }

    /// <summary>Proves the anonymous page cannot be produced by a privileged connection.</summary>
    public async Task<long> CountAsAnonAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

        await using (var setClaims = new NpgsqlCommand(
            "select set_config('request.jwt.claims', '', true)", connection, transaction))
        {
            await setClaims.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var setRole = new NpgsqlCommand("set local role anon", connection, transaction))
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

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        (await command.ExecuteNonQueryAsync().ConfigureAwait(false)).ShouldBe(1);
    }

    private async Task SeedAsync()
    {
        const string sql = """
            insert into app.company (id, name, country_id) values
                ('f3000000-0000-0000-0000-000000000001', 'Publishing Company',
                 (select id from app.country where code = 'VE'));

            insert into platform.role (id, key, name)
            values ('f6000000-0000-0000-0000-000000000001', 'news_writer', 'News Writer Without Publish');

            insert into platform.role_capability (role_id, capability_id)
            select 'f6000000-0000-0000-0000-000000000001', id
            from platform.capability where key in ('news.read', 'news.write');

            insert into platform.user_account (id, email, display_name, is_blocked, expires_on) values
                ('f4000000-0000-0000-0000-000000000001', 'pub.editor@example.test', 'Publishing Editor', false, null),
                ('f4000000-0000-0000-0000-000000000002', 'pub.writer@example.test', 'News Writer', false, null);

            insert into platform.user_role (user_id, role_id)
            select v.user_id, r.id
            from (values
                ('f4000000-0000-0000-0000-000000000001'::uuid, 'editor'),
                ('f4000000-0000-0000-0000-000000000002'::uuid, 'news_writer')
            ) as v(user_id, role_key)
            join platform.role r on r.key = v.role_key;

            insert into platform.user_profile (user_id, company_id) values
                ('f4000000-0000-0000-0000-000000000001', 'f3000000-0000-0000-0000-000000000001'),
                ('f4000000-0000-0000-0000-000000000002', 'f3000000-0000-0000-0000-000000000001');
            """;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);

        (await command.ExecuteNonQueryAsync().ConfigureAwait(false)).ShouldBeGreaterThan(0);
    }
}

[CollectionDefinition(Name)]
public sealed class PublishingCollection : ICollectionFixture<PublishingFixture>
{
    public const string Name = "publishing";
}

public sealed record PublishedAgreementRow(string State, DateTimeOffset? PublishedAt, Guid? PublishedBy);

public sealed record PublishedNewsRow(
    string State,
    DateTimeOffset? PublishedAt,
    Guid? PublishedBy,
    string Body);
