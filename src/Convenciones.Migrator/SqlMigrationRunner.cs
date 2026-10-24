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
/// reported as success. The runner is idempotent-by-absence — it does not track history, and the
/// migrations themselves are written to be applied exactly once to an empty schema.
/// </remarks>
public static class SqlMigrationRunner
{
    public const string ConnectionStringName = "convenciones";

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

        foreach (var file in files)
        {
            var sql = await File.ReadAllTextAsync(file).ConfigureAwait(false);

            await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection, transaction);

            try
            {
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
            }
            catch (PostgresException ex)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Migration failed and was rolled back: {Path.GetFileName(file)} — {ex.MessageText}", ex);
            }
        }

        return files.Count;
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
}
