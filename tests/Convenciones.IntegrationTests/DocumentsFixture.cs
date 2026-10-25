using System;
using System.IO;
using System.Threading.Tasks;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// A database and a private storage root of its own, so the upload tests can count exactly what was
/// written to each.
/// </summary>
public sealed class DocumentsFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("convenciones_documents")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>A throwaway directory standing in for the private storage bucket.</summary>
    public string StorageRoot { get; } = Path.Combine(
        Path.GetTempPath(), $"convenciones-docs-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(StorageRoot);

        await _container.StartAsync().ConfigureAwait(false);
        await TestDatabase.ApplyAsync(ConnectionString).ConfigureAwait(false);
        await TestDatabase.ApplySeedAsync(ConnectionString).ConfigureAwait(false);
        await SeedAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);

        if (Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }

    /// <summary>Files currently held by the storage adapter.</summary>
    public int StoredObjectCount() =>
        Directory.Exists(StorageRoot)
            ? Directory.EnumerateFiles(StorageRoot, "*", SearchOption.AllDirectories).Count()
            : 0;

    public async Task<int> DocumentRowCountAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select count(*) from platform.document", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    /// <summary>The company a stored document actually belongs to, read from the database.</summary>
    public async Task<Guid> QueryCompanyAsync(Guid documentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select company_id from platform.document where id = @id", connection);
        command.Parameters.AddWithValue("id", documentId);

        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is Guid companyId
            ? companyId
            : throw new InvalidOperationException($"No document row for {documentId}.");
    }

    private async Task SeedAsync()
    {
        // Editors hold document.write; negotiators do not, which gives the 403 case.
        const string sql = """
            insert into app.company (id, name, country_id) values
                ('b2000000-0000-0000-0000-000000000001', 'Documents Company A',
                 (select id from app.country where code = 'VE')),
                ('b2000000-0000-0000-0000-000000000002', 'Documents Company B',
                 (select id from app.country where code = 'VE'));

            insert into platform.user_account (id, email, display_name, is_blocked, expires_on) values
                ('b1000000-0000-0000-0000-000000000001', 'docs.a@example.test', 'Docs Editor A', false, null),
                ('b1000000-0000-0000-0000-000000000002', 'docs.b@example.test', 'Docs Editor B', false, null),
                ('b1000000-0000-0000-0000-000000000003', 'docs.n@example.test', 'Docs Reader',   false, null);

            insert into platform.user_role (user_id, role_id)
            select u.id, r.id
            from platform.user_account u
            join platform.role r on r.key = 'editor'
            where u.id in ('b1000000-0000-0000-0000-000000000001',
                           'b1000000-0000-0000-0000-000000000002');

            insert into platform.user_role (user_id, role_id)
            select u.id, r.id
            from platform.user_account u
            join platform.role r on r.key = 'negotiator'
            where u.id = 'b1000000-0000-0000-0000-000000000003';

            insert into platform.user_profile (user_id, company_id) values
                ('b1000000-0000-0000-0000-000000000001', 'b2000000-0000-0000-0000-000000000001'),
                ('b1000000-0000-0000-0000-000000000002', 'b2000000-0000-0000-0000-000000000002'),
                ('b1000000-0000-0000-0000-000000000003', 'b2000000-0000-0000-0000-000000000001');
            """;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}

[CollectionDefinition(Name)]
public sealed class DocumentsCollection : ICollectionFixture<DocumentsFixture>
{
    public const string Name = "documents";
}
