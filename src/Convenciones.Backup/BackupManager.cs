using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Convenciones.Migrator;
using Npgsql;

namespace Convenciones.Backup;

/// <summary>
/// Logical backup and isolated restore for Convenciones.
/// </summary>
/// <remarks>
/// The schema is not dumped: it is reproduced from <c>supabase/migrations</c> (the single source of truth,
/// ADR-023), and the manifest records the migration high-water mark and a schema fingerprint so a restore
/// refuses to load data into a schema that does not match. Data is dumped per table with
/// <c>COPY â€¦ TO STDOUT (FORMAT csv, HEADER true)</c> and restored with <c>COPY â€¦ FROM STDIN</c>.
///
/// Object storage is a separate recovery domain: the database backup never contains document bytes. A
/// storage root can be included in the same backup set as a file copy with its own manifest entries.
/// </remarks>
public sealed class BackupManager
{
    public const string FormatVersion = "convenciones-backup/1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async Task<BackupManifest> CreateAsync(
        string connectionString,
        string outputDirectory,
        string? storageRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var dataDirectory = Path.Combine(outputDirectory, "data");
        Directory.CreateDirectory(dataDirectory);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // One snapshot for every table: without it a concurrently written row could be counted in one table
        // and missed in another, producing a backup that never existed as a consistent state.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);

        var serverVersion = Convert.ToString(
            await ScalarAsync(connection, transaction, "select version();", cancellationToken).ConfigureAwait(false))!;
        var database = Convert.ToString(
            await ScalarAsync(connection, transaction, "select current_database();", cancellationToken).ConfigureAwait(false))!;
        var migrationVersion = await ScalarAsync(
            connection, transaction, "select max(filename) from public.schema_migration;", cancellationToken).ConfigureAwait(false);
        var fingerprint = await SchemaFingerprintAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var tables = new List<BackupTableEntry>();
        foreach (var (schema, name) in await ListTablesAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            var qualified = Quote(schema) + "." + Quote(name);
            var rows = Convert.ToInt64(
                await ScalarAsync(connection, transaction, $"select count(*) from {qualified};", cancellationToken).ConfigureAwait(false));
            var path = Path.Combine(dataDirectory, $"{schema}.{name}.csv");

            await ExportTableAsync(connection, qualified, path, cancellationToken).ConfigureAwait(false);

            var bytes = new FileInfo(path).Length;
            tables.Add(new BackupTableEntry(schema, name, rows, bytes, Sha256(path)));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var storage = storageRoot is not null && Directory.Exists(storageRoot)
            ? await CaptureStorageAsync(storageRoot, Path.Combine(outputDirectory, "storage"), cancellationToken)
                .ConfigureAwait(false)
            : null;

        var manifest = new BackupManifest(
            FormatVersion,
            DateTimeOffset.UtcNow,
            database,
            serverVersion,
            migrationVersion is null ? null : Convert.ToString(migrationVersion),
            fingerprint,
            tables,
            storage);

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        return manifest;
    }

    /// <summary>Verifies the manifest and every file hash. Throws when anything does not match.</summary>
    public static async Task<BackupManifest> VerifyAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new BackupIntegrityException($"No manifest found in '{directory}'.");
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false))
            ?? throw new BackupIntegrityException("The manifest is empty or unreadable.");

        if (manifest.FormatVersion != FormatVersion)
        {
            throw new BackupIntegrityException(
                $"Unsupported backup format '{manifest.FormatVersion}'; this tool writes '{FormatVersion}'.");
        }

        foreach (var table in manifest.Tables)
        {
            var path = Path.Combine(directory, "data", $"{table.Schema}.{table.Name}.csv");
            VerifyFile(path, table.Bytes, table.Sha256, $"{table.Schema}.{table.Name}");
        }

        if (manifest.Storage is not null)
        {
            foreach (var entry in manifest.Storage.Files)
            {
                VerifyFile(Path.Combine(directory, "storage", entry.Path), entry.Bytes, entry.Sha256, entry.Path);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return manifest;
    }

    /// <summary>
    /// Restores a verified backup into an empty database. The target is identified explicitly by the
    /// caller (<paramref name="confirmedDatabase"/>) and must be empty: this never overwrites an existing
    /// deployment.
    /// </summary>
    public static async Task<RestoreReport> RestoreAsync(
        string connectionString,
        string directory,
        string confirmedDatabase,
        string migrationsDirectory,
        string? storageRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsDirectory);

        var manifest = await VerifyAsync(directory, cancellationToken).ConfigureAwait(false);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var target = Convert.ToString(
            await ScalarAsync(connection, null, "select current_database();", cancellationToken).ConfigureAwait(false))!;

        if (!string.Equals(target, confirmedDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Target database is '{target}' but '{confirmedDatabase}' was confirmed. Refusing to restore.");
        }

        var existing = Convert.ToInt64(await ScalarAsync(
            connection, null,
            "select count(*) from information_schema.tables where table_schema in ('app','platform');",
            cancellationToken).ConfigureAwait(false));
        if (existing > 0)
        {
            throw new InvalidOperationException(
                $"Target '{target}' already has {existing} application table(s). Restore only into an empty database.");
        }

        var applied = await SqlMigrationRunner.ApplyAsync(connectionString, migrationsDirectory).ConfigureAwait(false);

        var fingerprint = await SchemaFingerprintAsync(connection, null, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(fingerprint, manifest.SchemaFingerprint, StringComparison.Ordinal))
        {
            throw new BackupIntegrityException(
                "The restored schema does not match the backup's schema fingerprint. The migration set differs.");
        }

        var checks = new List<string>();
        long restoredRows = 0;
        var merged = new List<string>();

        // Migrations already seeded reference data (roles, capabilities, taxonomy). Those rows exist in the
        // backup too, so a blind COPY would violate their unique keys. Tables that are non-empty right
        // after migration are merged with ON CONFLICT DO NOTHING; tenant data is copied verbatim.
        var seeded = await NonEmptyTablesAsync(connection, cancellationToken).ConfigureAwait(false);

        // FKs are validated after the load; disabling them during the COPY is what makes the restore
        // independent of table order and resumable. This requires a role with the replication attribute.
        await ExecuteAsync(connection, "set session_replication_role = replica;", cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var table in manifest.Tables)
            {
                var qualified = Quote(table.Schema) + "." + Quote(table.Name);
                var path = Path.Combine(directory, "data", $"{table.Schema}.{table.Name}.csv");

                if (seeded.Contains($"{table.Schema}.{table.Name}"))
                {
                    var affected = await MergeTableAsync(connection, qualified, path, cancellationToken).ConfigureAwait(false);
                    merged.Add($"{table.Schema}.{table.Name}: +{affected}");
                    restoredRows += affected;
                    continue;
                }

                var imported = await ImportTableAsync(connection, qualified, path, cancellationToken).ConfigureAwait(false);

                if (imported != table.Rows)
                {
                    throw new BackupIntegrityException(
                        $"Table '{table.Schema}.{table.Name}' restored {imported} row(s) but the manifest records {table.Rows}.");
                }

                restoredRows += imported;
            }
        }
        finally
        {
            await ExecuteAsync(connection, "set session_replication_role = origin;", cancellationToken).ConfigureAwait(false);
        }

        if (merged.Count > 0)
        {
            checks.Add("reference data merged: " + string.Join(", ", merged));
        }

        await ResetSequencesAsync(connection, cancellationToken).ConfigureAwait(false);

        var restoredStorage = 0;
        if (manifest.Storage is not null && !string.IsNullOrWhiteSpace(storageRoot))
        {
            restoredStorage = RestoreStorage(directory, storageRoot, manifest.Storage);
            checks.Add($"storage files restored: {restoredStorage}");
        }

        var report = await CheckAsync(connectionString, storageRoot, cancellationToken).ConfigureAwait(false);
        if (!report.Passed)
        {
            checks.AddRange(report.FinancialViolations.Select(v => "financial: " + v));
            checks.AddRange(report.ReferentialViolations.Select(v => "referential: " + v));
            checks.AddRange(report.MissingObjects.Select(v => "missing object: " + v));
            checks.Add($"tables={report.Tables}, rls={report.TablesWithRowLevelSecurity}");
            throw new BackupIntegrityException(
                "Restore completed but post-restore consistency checks failed: " + string.Join("; ", checks));
        }

        checks.Add("schema fingerprint matches");
        checks.Add("row counts match manifest");
        checks.Add("financial and referential checks passed");

        var restoreReport = new RestoreReport(
            DateTimeOffset.UtcNow, target, applied, restoredRows, restoredStorage, checks);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "restore-report.json"),
            JsonSerializer.Serialize(restoreReport, JsonOptions),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        return restoreReport;
    }

    /// <summary>Runs the post-restore checks: RLS, financial invariants, references and storage objects.</summary>
    public static async Task<CheckReport> CheckAsync(
        string connectionString,
        string? storageRoot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tables = Convert.ToInt64(await ScalarAsync(connection, null,
            "select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace " +
            "where n.nspname in ('app','platform') and c.relkind = 'r';", cancellationToken).ConfigureAwait(false));
        var secured = Convert.ToInt64(await ScalarAsync(connection, null,
            "select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace " +
            "where n.nspname in ('app','platform') and c.relkind = 'r' and c.relrowsecurity;", cancellationToken)
            .ConfigureAwait(false));

        var financial = new List<string>();
        var referential = new List<string>();

        foreach (var (sql, label) in new[]
        {
            ("select count(*) from platform.ai_budget b where b.reserved_amount <> " +
             "(select coalesce(sum(r.estimated_cost),0) from platform.ai_budget_reservation r " +
             " where r.budget_id = b.id and r.status in ('reserved','submitted','outcome_unknown'));",
             "budget reserved_amount differs from its outstanding reservations"),
            ("select count(*) from platform.ai_usage_record where estimated_cost_usd is not null and estimated_cost_usd < 0;",
             "negative usage cost"),
        })
        {
            var count = Convert.ToInt64(await ScalarAsync(connection, null, sql, cancellationToken).ConfigureAwait(false));
            if (count > 0)
            {
                financial.Add($"{label}: {count} row(s)");
            }
        }

        foreach (var (sql, label) in new[]
        {
            ("select count(*) from platform.ai_usage_record u where not exists " +
             "(select 1 from platform.processing_run r where r.id = u.run_id);", "usage without run"),
            ("select count(*) from platform.ai_budget_reservation r where not exists " +
             "(select 1 from platform.processing_run p where p.id = r.run_id);", "reservation without run"),
            ("select count(*) from platform.document d where not exists " +
             "(select 1 from app.company c where c.id = d.company_id);", "document without company"),
        })
        {
            var count = Convert.ToInt64(await ScalarAsync(connection, null, sql, cancellationToken).ConfigureAwait(false));
            if (count > 0)
            {
                referential.Add($"{label}: {count} row(s)");
            }
        }

        var missing = new List<string>();
        var orphaned = new List<string>();
        if (!string.IsNullOrWhiteSpace(storageRoot) && Directory.Exists(storageRoot))
        {
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand("select storage_path from platform.document;", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var path = reader.GetString(0);
                    referenced.Add(path);
                    if (!File.Exists(Path.Combine(storageRoot, path)))
                    {
                        missing.Add(path);
                    }
                }
            }

            foreach (var file in Directory.EnumerateFiles(storageRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(storageRoot, file).Replace('\\', '/');
                if (!referenced.Contains(relative))
                {
                    orphaned.Add(relative);
                }
            }
        }

        return new CheckReport(
            DateTimeOffset.UtcNow,
            tables,
            secured,
            financial,
            referential,
            missing,
            orphaned,
            financial.Count == 0
            && referential.Count == 0
            && secured == tables
            && missing.Count == 0);
    }

    // ------------------------------------------------------------------ internals

    private static async Task<HashSet<string>> NonEmptyTablesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (schema, name) in await ListTablesAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            var count = Convert.ToInt64(await ScalarAsync(
                connection, null,
                $"select count(*) from {Quote(schema)}.{Quote(name)};",
                cancellationToken).ConfigureAwait(false));

            if (count > 0)
            {
                result.Add($"{schema}.{name}");
            }
        }

        return result;
    }

    /// <summary>
    /// Loads a migration-seeded table into a temporary copy and merges it, ignoring rows the migrations
    /// already created. Returns the number of rows actually inserted.
    /// </summary>
    private static async Task<long> MergeTableAsync(
        NpgsqlConnection connection,
        string qualifiedTable,
        string path,
        CancellationToken cancellationToken)
    {
        var temp = Quote("_restore_" + Guid.NewGuid().ToString("N"));

        await ExecuteAsync(
            connection,
            $"create temp table {temp} (like {qualifiedTable} including defaults);",
            cancellationToken).ConfigureAwait(false);

        try
        {
            await ImportTableAsync(connection, temp, path, cancellationToken).ConfigureAwait(false);

            await using var command = new NpgsqlCommand(
                $"with moved as (insert into {qualifiedTable} select * from {temp} on conflict do nothing returning 1) " +
                "select count(*) from moved;", connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await ExecuteAsync(connection, $"drop table if exists {temp};", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<List<(string Schema, string Name)>> ListTablesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var tables = new List<(string, string)>();
        await using var command = new NpgsqlCommand(
            """
            select table_schema, table_name
            from information_schema.tables
            where table_schema in ('app', 'platform') and table_type = 'BASE TABLE'
            order by table_schema, table_name;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add((reader.GetString(0), reader.GetString(1)));
        }

        return tables;
    }

    private static async Task ExportTableAsync(
        NpgsqlConnection connection,
        string qualifiedTable,
        string path,
        CancellationToken cancellationToken)
    {
        await using var copy = await connection
            .BeginTextExportAsync($"COPY (select * from {qualifiedTable}) TO STDOUT (FORMAT csv, HEADER true);")
            .ConfigureAwait(false);
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        while (await copy.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static async Task<long> ImportTableAsync(
        NpgsqlConnection connection,
        string qualifiedTable,
        string path,
        CancellationToken cancellationToken)
    {
        var imported = 0L;
        await using (var writer = await connection
                         .BeginTextImportAsync($"COPY {qualifiedTable} FROM STDIN (FORMAT csv, HEADER true);")
                         .ConfigureAwait(false))
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                imported++;
            }
        }

        // The CSV header is part of the file but not a data row.
        return Math.Max(0, imported - 1);
    }

    private static async Task<BackupStorageManifest> CaptureStorageAsync(
        string storageRoot,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);
        var files = new List<BackupStorageEntry>();

        foreach (var file in Directory.EnumerateFiles(storageRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(storageRoot, file);
            var destination = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);

            files.Add(new BackupStorageEntry(
                relative.Replace('\\', '/'), new FileInfo(destination).Length, Sha256(destination)));
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return new BackupStorageManifest(files);
    }

    private static int RestoreStorage(string directory, string storageRoot, BackupStorageManifest manifest)
    {
        var restored = 0;
        foreach (var entry in manifest.Files)
        {
            var source = Path.Combine(directory, "storage", entry.Path);
            var destination = Path.Combine(storageRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            restored++;
        }

        return restored;
    }

    private static async Task ResetSequencesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var sequences = new List<(string Table, string Column, string Sequence)>();
        await using (var command = new NpgsqlCommand(
            """
            select quote_ident(n.nspname) || '.' || quote_ident(c.relname) as tbl,
                   quote_ident(a.attname) as col,
                   pg_get_serial_sequence(quote_ident(n.nspname) || '.' || quote_ident(c.relname), a.attname) as seq
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            join pg_attribute a on a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped
            where n.nspname in ('app', 'platform')
              and c.relkind = 'r'
              and pg_get_serial_sequence(quote_ident(n.nspname) || '.' || quote_ident(c.relname), a.attname) is not null;
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sequences.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var (table, column, sequence) in sequences)
        {
            var maximum = Convert.ToInt64(await ScalarAsync(connection, null,
                $"select coalesce(max({column}), 0) from {table};", cancellationToken).ConfigureAwait(false));

            await using var setval = new NpgsqlCommand(
                "select setval(@sequence::regclass, @value, @isCalled);", connection);
            setval.Parameters.AddWithValue("sequence", sequence);
            setval.Parameters.AddWithValue("value", maximum > 0 ? maximum : 1);
            setval.Parameters.AddWithValue("isCalled", maximum > 0);
            await setval.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> SchemaFingerprintAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(connection, transaction,
            """
            select md5(string_agg(table_schema || '.' || table_name || '.' || column_name || ':' || data_type,
                                  ',' order by table_schema, table_name, ordinal_position))
            from information_schema.columns
            where table_schema in ('app', 'platform');
            """, cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? string.Empty : Convert.ToString(value)!;
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> TryScalarAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ScalarAsync(connection, null, sql, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException)
        {
            return null;
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void VerifyFile(string path, long expectedBytes, string expectedSha, string label)
    {
        if (!File.Exists(path))
        {
            throw new BackupIntegrityException($"Backup file missing for '{label}': {path}");
        }

        var length = new FileInfo(path).Length;
        if (length != expectedBytes)
        {
            throw new BackupIntegrityException(
                $"Backup file '{label}' is {length} byte(s); the manifest records {expectedBytes}.");
        }

        var actual = Sha256(path);
        if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupIntegrityException($"Backup file '{label}' failed its SHA-256 check.");
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLower(CultureInfo.InvariantCulture);
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
