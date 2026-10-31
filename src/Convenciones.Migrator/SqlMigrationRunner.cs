using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;

namespace Convenciones.Migrator;

/// <summary>
/// Applies the repository's ordered SQL migrations to a database.
/// </summary>
/// <remarks>
/// The files under <c>supabase/migrations</c> are the single source of truth for the schema
/// (ADR-023); there is no parallel EF Core migration history that could drift from them. Both the
/// Aspire migrator resource and the integration tests use this one code path, so a schema that ships
/// is the schema that was tested.
///
/// Every migration runs inside its own transaction and the whole run is all-or-nothing per file: a
/// failure stops the run and returns a non-zero exit code, so a half-applied schema is never
/// reported as success. Applied files are recorded in <c>public.schema_migration</c>, so a deploy
/// applies only what is new instead of replaying a schema that already exists.
/// </remarks>
public static class SqlMigrationRunner
{
    public const string ConnectionStringName = "convenciones";

    private const string HistoryTable = "public.schema_migration";

    /// <summary>Returns the migration files, ordered by filename, which encodes the version order.</summary>
    public static IReadOnlyList<string> OrderedMigrationFiles(string migrationsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsDirectory);

        if (!Directory.Exists(migrationsDirectory))
        {
            throw new DirectoryNotFoundException($"Migrations directory not found: {migrationsDirectory}");
        }

        return Directory
            .GetFiles(migrationsDirectory, "*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Applies every migration in order. Throws on the first failure.</summary>
    public static async Task<int> ApplyAsync(string connectionString, string migrationsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var files = OrderedMigrationFiles(migrationsDirectory);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await EnsureHistoryAsync(connection).ConfigureAwait(false);
        var applied = 0;

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);

            if (await IsAppliedAsync(connection, fileName).ConfigureAwait(false))
            {
                continue;
            }

            var sql = await File.ReadAllTextAsync(file).ConfigureAwait(false);

            await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection, transaction);

            try
            {
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                await using var record = new NpgsqlCommand(
                    $"insert into {HistoryTable}(filename) values (@filename)", connection, transaction);
                record.Parameters.AddWithValue("filename", fileName);
                await record.ExecuteNonQueryAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                applied++;
            }
            catch (PostgresException ex)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Migration failed and was rolled back: {Path.GetFileName(file)} — {ex.MessageText}", ex);
            }
        }

        return applied;
    }

    /// <summary>Applies the reference-data seed, if present.</summary>
    public static async Task ApplySeedAsync(string connectionString, string seedFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (!File.Exists(seedFile))
        {
            return;
        }

        var sql = await File.ReadAllTextAsync(seedFile).ConfigureAwait(false);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        // The seed is written to be idempotent (every insert is guarded by NOT EXISTS), so it is
        // safe to run on every startup.
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task EnsureHistoryAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            $"create table if not exists {HistoryTable} (" +
            "filename text primary key, " +
            "applied_at timestamptz not null default now())",
            connection);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<bool> IsAppliedAsync(NpgsqlConnection connection, string fileName)
    {
        await using var command = new NpgsqlCommand(
            $"select exists (select 1 from {HistoryTable} where filename = @filename)",
            connection);
        command.Parameters.AddWithValue("filename", fileName);

        return (bool)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }
}
