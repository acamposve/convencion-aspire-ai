using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Backup;
using Npgsql;
using Shouldly;
using Xunit;

namespace Convenciones.IntegrationTests;

/// <summary>
/// P1-007: a backup that verifies, restores into an isolated database, and keeps tenants, financial
/// records and audit history intact. Object storage is exercised as its own recovery domain.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BackupRestoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), "conv-backup-" + Guid.NewGuid().ToString("N"));
    private readonly List<Seeded> _seeded = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }

        foreach (var item in _seeded)
        {
            await ExecuteAsync(fixture.ConnectionString,
                $"delete from platform.ai_usage_record where company_id = '{item.Company}';" +
                $"delete from platform.ai_budget_reservation where company_id = '{item.Company}';" +
                $"delete from platform.ai_budget_shadow where company_id = '{item.Company}';" +
                $"delete from platform.ai_budget where company_id = '{item.Company}';" +
                $"delete from platform.ai_budget_audit where company_id = '{item.Company}';" +
                $"delete from platform.processing_run where company_id = '{item.Company}';" +
                $"delete from platform.document where id = '{item.Document}';" +
                $"delete from platform.user_account where id = '{item.User}';" +
                $"delete from app.company where id = '{item.Company}';");
        }
    }

    [Fact]
    public async Task Backup_verifies_and_detects_tampering()
    {
        var backup = Path.Combine(_workingDirectory, "set");
        await BackupManager.CreateAsync(fixture.ConnectionString, backup, null, CancellationToken.None);

        var manifest = await BackupManager.VerifyAsync(backup, CancellationToken.None);
        manifest.Tables.ShouldNotBeEmpty();
        manifest.SchemaFingerprint.ShouldNotBeNullOrWhiteSpace();
        manifest.MigrationVersion.ShouldNotBeNullOrWhiteSpace();

        var target = Path.Combine(backup, "data", "app.company.csv");
        await File.AppendAllTextAsync(target, "tampered,row\n");

        await Should.ThrowAsync<BackupIntegrityException>(() =>
            BackupManager.VerifyAsync(backup, CancellationToken.None));
    }

    [Fact]
    public async Task Restore_into_an_empty_database_preserves_tenants_and_financial_records()
    {
        var seeded = await SeedMarkerAsync();

        var backup = Path.Combine(_workingDirectory, "restore-set");
        var manifest = await BackupManager.CreateAsync(fixture.ConnectionString, backup, null, CancellationToken.None);

        var database = "restore_" + Guid.NewGuid().ToString("N")[..12];
        var target = await CreateDatabaseAsync(database);

        try
        {
            var report = await BackupManager.RestoreAsync(
                target, backup, database, MigrationsDirectory(), null, false, CancellationToken.None);

            report.AppliedMigrations.ShouldBeGreaterThan(20);
            report.RestoredRows.ShouldBeGreaterThan(0);
            report.RestoredRows.ShouldBeLessThanOrEqualTo(manifest.Tables.Sum(table => table.Rows));
            report.Checks.ShouldContain("financial and referential checks passed");

            (await ScalarAsync(target, $"select count(*) from app.company where id = '{seeded.Company}'"))
                .ShouldBe("1");
            (await ScalarAsync(target, $"select count(*) from platform.user_account where id = '{seeded.User}'"))
                .ShouldBe("1");
            (await ScalarAsync(target, $"select count(*) from platform.processing_run where id = '{seeded.Run}'"))
                .ShouldBe("1");
            (await ScalarAsync(target, $"select count(*) from platform.ai_usage_record where invocation_id = '{seeded.Invocation}'"))
                .ShouldBe("1");
            (await ScalarAsync(target,
                $"select input_tokens || ':' || output_tokens from platform.ai_usage_record where invocation_id = '{seeded.Invocation}'"))
                .ShouldBe("1000:500");
            (await ScalarAsync(target,
                $"select spent_amount || ':' || reserved_amount from platform.ai_budget where id = '{seeded.Budget}'"))
                .ShouldBe("12.000000:0.000000");
            (await ScalarAsync(target,
                $"select status || ':' || settled_amount from platform.ai_budget_reservation where invocation_id = '{seeded.Invocation}'"))
                .ShouldBe("settled:12.000000");
            (await ScalarAsync(target, $"select count(*) from platform.ai_budget_audit where company_id = '{seeded.Company}'"))
                .ShouldBe("1");

            // Every table's row count must match the manifest exactly.
            var referenceData = new[]
            {
                "platform.capability", "platform.role", "platform.role_capability", "platform.clause_category",
            };

            foreach (var table in manifest.Tables.Where(t => !referenceData.Contains($"{t.Schema}.{t.Name}")))
            {
                var count = await ScalarAsync(target,
                    $"select count(*) from \"{table.Schema}\".\"{table.Name}\";");
                count.ShouldBe(table.Rows.ToString(), $"row count for {table.Schema}.{table.Name}");
            }
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Restore_refuses_a_target_that_is_not_empty()
    {
        var backup = Path.Combine(_workingDirectory, "non-empty-set");
        await BackupManager.CreateAsync(fixture.ConnectionString, backup, null, CancellationToken.None);

        var database = "restore_" + Guid.NewGuid().ToString("N")[..12];
        var target = await CreateDatabaseAsync(database);

        try
        {
            await BackupManager.RestoreAsync(target, backup, database, MigrationsDirectory(), null, false, CancellationToken.None);

            await Should.ThrowAsync<InvalidOperationException>(() =>
                BackupManager.RestoreAsync(target, backup, database, MigrationsDirectory(), null, false, CancellationToken.None));
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Restore_refuses_a_mismatched_target_confirmation()
    {
        var backup = Path.Combine(_workingDirectory, "confirm-set");
        await BackupManager.CreateAsync(fixture.ConnectionString, backup, null, CancellationToken.None);

        var database = "restore_" + Guid.NewGuid().ToString("N")[..12];
        var target = await CreateDatabaseAsync(database);

        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                BackupManager.RestoreAsync(target, backup, "some_other_database", MigrationsDirectory(), null, false, CancellationToken.None));
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Storage_is_backed_up_restored_and_checked()
    {
        var storageRoot = Path.Combine(_workingDirectory, "storage-src");
        var restoredRoot = Path.Combine(_workingDirectory, "storage-dst");
        Directory.CreateDirectory(storageRoot);

        var seeded = await SeedMarkerAsync();
        var relative = $"company/{seeded.Company}/{seeded.Document}.pdf";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(storageRoot, relative))!);
        await File.WriteAllTextAsync(Path.Combine(storageRoot, relative), "pdf-bytes");
        await ExecuteAsync(fixture.ConnectionString,
            $"update platform.document set storage_path = '{relative}' where id = '{seeded.Document}';");

        var backup = Path.Combine(_workingDirectory, "storage-set");
        var manifest = await BackupManager.CreateAsync(fixture.ConnectionString, backup, storageRoot, CancellationToken.None);
        manifest.Storage.ShouldNotBeNull();
        manifest.Storage!.Files.Count.ShouldBeGreaterThanOrEqualTo(1);

        var database = "restore_" + Guid.NewGuid().ToString("N")[..12];
        var target = await CreateDatabaseAsync(database);

        try
        {
            var report = await BackupManager.RestoreAsync(
                target, backup, database, MigrationsDirectory(), restoredRoot, false, CancellationToken.None);

            report.RestoredStorageFiles.ShouldBeGreaterThanOrEqualTo(1);
            File.Exists(Path.Combine(restoredRoot, relative)).ShouldBeTrue();

            var clean = await BackupManager.CheckAsync(target, restoredRoot, null, CancellationToken.None);
            clean.MissingObjects.ShouldBeEmpty();
            clean.Passed.ShouldBeTrue();

            File.Delete(Path.Combine(restoredRoot, relative));
            var missing = await BackupManager.CheckAsync(target, restoredRoot, null, CancellationToken.None);
            missing.MissingObjects.ShouldContain(relative);
            missing.Passed.ShouldBeFalse();

            await File.WriteAllTextAsync(Path.Combine(restoredRoot, "orphan.bin"), "x");
            var orphan = await BackupManager.CheckAsync(target, restoredRoot, null, CancellationToken.None);
            orphan.OrphanObjects.ShouldContain("orphan.bin");
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Verify_rejects_path_traversal_in_storage_entries()
    {
        var storageRoot = Path.Combine(_workingDirectory, "traversal-storage");
        Directory.CreateDirectory(storageRoot);
        await File.WriteAllTextAsync(Path.Combine(storageRoot, "doc.pdf"), "x");

        var backup = Path.Combine(_workingDirectory, "traversal-set");
        await BackupManager.CreateAsync(fixture.ConnectionString, backup, storageRoot, CancellationToken.None);

        var manifestPath = Path.Combine(backup, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["Storage"]!["Files"]![0]!["Path"] = "../../escape.txt";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        await Should.ThrowAsync<BackupIntegrityException>(() =>
            BackupManager.VerifyAsync(backup, CancellationToken.None));
    }

    [Fact]
    public async Task Verify_rejects_invalid_table_identifiers()
    {
        var backup = Path.Combine(_workingDirectory, "identifier-set");
        await BackupManager.CreateAsync(fixture.ConnectionString, backup, null, CancellationToken.None);

        var manifestPath = Path.Combine(backup, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["Tables"]![0]!["Name"] = "../evil";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        await Should.ThrowAsync<BackupIntegrityException>(() =>
            BackupManager.VerifyAsync(backup, CancellationToken.None));
    }

    [Fact]
    public async Task Restore_rejects_a_migration_mismatch_and_leaves_the_target_empty()
    {
        var backup = Path.Combine(_workingDirectory, "migration-set");
        await BackupManager.CreateAsync(fixture.ConnectionString, backup, null, CancellationToken.None);

        var manifestPath = Path.Combine(backup, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        manifest["MigrationVersion"] = "9999_not_applied.sql";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());

        var database = "restore_" + Guid.NewGuid().ToString("N")[..12];
        var target = await CreateDatabaseAsync(database);

        try
        {
            await Should.ThrowAsync<BackupIntegrityException>(() =>
                BackupManager.RestoreAsync(target, backup, database, MigrationsDirectory(), null, false, CancellationToken.None));

            (await ScalarAsync(target,
                "select count(*) from information_schema.tables where table_schema in ('app','platform');"))
                .ShouldBe("0", "a refused restore cleans the migrated schema");
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task A_failed_restore_rolls_back_and_can_be_retried()
    {
        var good = Path.Combine(_workingDirectory, "good-set");
        await BackupManager.CreateAsync(fixture.ConnectionString, good, null, CancellationToken.None);

        var corrupt = Path.Combine(_workingDirectory, "corrupt-set");
        CopyDirectory(good, corrupt);

        var tableFile = Path.Combine(corrupt, "data", "app.company.csv");
        await File.AppendAllTextAsync(tableFile, "only-one-column\n");
        await RewriteManifestFileEntryAsync(corrupt, "app.company");

        var database = "restore_" + Guid.NewGuid().ToString("N")[..12];
        var target = await CreateDatabaseAsync(database);

        try
        {
            await Should.ThrowAsync<PostgresException>(() =>
                BackupManager.RestoreAsync(target, corrupt, database, MigrationsDirectory(), null, false, CancellationToken.None));

            File.Exists(Path.Combine(corrupt, "restore-failed.json")).ShouldBeTrue("a failure marker is written");
            (await ScalarAsync(target,
                "select count(*) from information_schema.tables where table_schema in ('app','platform');"))
                .ShouldBe("0", "the transactional load and cleanup leave an empty target");

            var report = await BackupManager.RestoreAsync(
                target, good, database, MigrationsDirectory(), null, false, CancellationToken.None);

            report.RestoredRows.ShouldBeGreaterThan(0);
            File.Exists(Path.Combine(good, "restore-failed.json")).ShouldBeFalse();
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Check_reports_incomplete_tables_against_the_manifest()
    {
        await SeedMarkerAsync();

        var backup = Path.Combine(_workingDirectory, "count-set");
        await BackupManager.CreateAsync(fixture.ConnectionString, backup, null, CancellationToken.None);

        var database = "restore_" + Guid.NewGuid().ToString("N")[..12];
        var target = await CreateDatabaseAsync(database);

        try
        {
            await BackupManager.RestoreAsync(target, backup, database, MigrationsDirectory(), null, false, CancellationToken.None);
            await ExecuteAsync(target, "delete from platform.ai_budget_audit;");

            var check = await BackupManager.CheckAsync(target, null, backup, CancellationToken.None);

            check.IncompleteTables.ShouldContain(item => item.StartsWith("platform.ai_budget_audit", StringComparison.Ordinal));
            check.Passed.ShouldBeFalse();
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Check_reports_cross_tenant_attribution()
    {
        var first = await SeedMarkerAsync();
        var second = await SeedMarkerAsync();
        var invocation = Guid.NewGuid();

        await ExecuteAsync(fixture.ConnectionString,
            $"insert into platform.ai_usage_record (invocation_id, company_id, document_id, run_id, attempt_id, stage, " +
            $"invocation_no, provider, input_tokens, output_tokens, usage_source, estimated_cost_usd, currency, " +
            $"pricing_version, pricing_input_per_million, pricing_output_per_million, period) values " +
            $"('{invocation}', '{second.Company}', '{first.Document}', '{first.Run}', '{first.Attempt}', 'extraction', 2, " +
            $"'azure-foundry', 1, 1, 'provider', 0.000001, 'USD', 'test-v1', 0.61, 1.82, (date_trunc('month', now()))::date);");

        try
        {
            var check = await BackupManager.CheckAsync(fixture.ConnectionString, null, null, CancellationToken.None);

            check.ReferentialViolations.ShouldContain(item => item.Contains("different company", StringComparison.Ordinal));
            check.Passed.ShouldBeFalse();
        }
        finally
        {
            await ExecuteAsync(fixture.ConnectionString,
                $"delete from platform.ai_usage_record where invocation_id = '{invocation}';");
        }
    }

    [Fact]
    public async Task Sequence_is_aligned_after_restoring_identity_rows()
    {
        await ExecuteAsync(fixture.ConnectionString,
            "insert into platform.audit_entry (module, entity_type, entity_id, action) values " +
            "('audit', 'backup', 'one', 'created'), ('audit', 'backup', 'two', 'created');");

        var backup = Path.Combine(_workingDirectory, "sequence-set");
        await BackupManager.CreateAsync(fixture.ConnectionString, backup, null, CancellationToken.None);

        var database = "restore_" + Guid.NewGuid().ToString("N")[..12];
        var target = await CreateDatabaseAsync(database);

        try
        {
            await BackupManager.RestoreAsync(target, backup, database, MigrationsDirectory(), null, false, CancellationToken.None);

            var maximum = Convert.ToInt64(await ScalarAsync(target, "select max(id) from platform.audit_entry;"));
            maximum.ShouldBeGreaterThan(0);

            await ExecuteAsync(target,
                "insert into platform.audit_entry (module, entity_type, entity_id, action) values " +
                "('audit', 'backup', 'three', 'created');");

            var newId = Convert.ToInt64(await ScalarAsync(target,
                "select id from platform.audit_entry where entity_id = 'three';"));
            newId.ShouldBeGreaterThan(maximum, "the identity sequence continues after the restored maximum");
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Restore_refuses_a_non_empty_storage_root_without_replace()
    {
        var storageRoot = Path.Combine(_workingDirectory, "replace-src");
        Directory.CreateDirectory(storageRoot);
        await File.WriteAllTextAsync(Path.Combine(storageRoot, "doc.pdf"), "x");

        var backup = Path.Combine(_workingDirectory, "replace-set");
        await BackupManager.CreateAsync(fixture.ConnectionString, backup, storageRoot, CancellationToken.None);

        var restoredRoot = Path.Combine(_workingDirectory, "replace-dst");
        Directory.CreateDirectory(restoredRoot);
        await File.WriteAllTextAsync(Path.Combine(restoredRoot, "existing.bin"), "keep");

        var database = "restore_" + Guid.NewGuid().ToString("N")[..12];
        var target = await CreateDatabaseAsync(database);

        try
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                BackupManager.RestoreAsync(target, backup, database, MigrationsDirectory(), restoredRoot, false, CancellationToken.None));
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
    }
    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static async Task RewriteManifestFileEntryAsync(string backup, string table)
    {
        var manifestPath = Path.Combine(backup, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;

        foreach (var entry in manifest["Tables"]!.AsArray())
        {
            if (entry is null
                || (string?)entry["Schema"] != table.Split('.')[0]
                || (string?)entry["Name"] != table.Split('.')[1])
            {
                continue;
            }

            var path = Path.Combine(backup, "data", $"{table}.csv");
            var bytes = new FileInfo(path).Length;
            using var stream = File.OpenRead(path);
            entry["Bytes"] = bytes;
            entry["Sha256"] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Seeded(Guid Company, Guid User, Guid Document, Guid Run, Guid Attempt, Guid Invocation, Guid Budget);

    private async Task<Seeded> SeedMarkerAsync()
    {
        var company = Guid.NewGuid();
        var user = Guid.NewGuid();
        var document = Guid.NewGuid();
        var run = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var invocation = Guid.NewGuid();
        var budget = Guid.NewGuid();
        var suffix = company.ToString("N")[..8];

        await ExecuteAsync(fixture.ConnectionString,
            $"insert into app.company (id, name) values ('{company}', 'Backup marker {suffix}');" +
            $"insert into platform.user_account (id, email, display_name) values ('{user}', 'backup-{suffix}@example.test', 'Backup');" +
            $"insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) " +
            $"values ('{document}', 'marker/{suffix}.pdf', 'marker.pdf', 'application/pdf', 10, '{user}', '{company}');" +
            $"insert into platform.processing_run (id, document_id, company_id) values ('{run}', '{document}', '{company}');" +
            $"insert into platform.processing_attempt (id, run_id, company_id, attempt_no) values ('{attempt}', '{run}', '{company}', 1);" +
            $"insert into platform.ai_usage_record (invocation_id, company_id, document_id, run_id, attempt_id, stage, invocation_no, " +
            $"provider, input_tokens, output_tokens, usage_source, estimated_cost_usd, currency, pricing_version, " +
            $"pricing_input_per_million, pricing_output_per_million, period) values " +
            $"('{invocation}', '{company}', '{document}', '{run}', '{attempt}', 'extraction', 1, 'azure-foundry', 1000, 500, " +
            $"'provider', 0.001520, 'USD', 'test-v1', 0.61, 1.82, (date_trunc('month', now()))::date);" +
            $"insert into platform.ai_budget (id, company_id, period_start, period_end, limit_amount, spent_amount, reserved_amount, enforcement) " +
            $"values ('{budget}', '{company}', (date_trunc('month', now()))::date, (date_trunc('month', now()) + interval '1 month - 1 day')::date, 100, 12, 0, 'enforced');" +
            $"insert into platform.ai_budget_reservation (invocation_id, budget_id, company_id, run_id, attempt_id, stage, " +
            $"estimated_cost, settled_amount, currency, status, origin, submitted_at, settled_at, expires_at) values " +
            $"('{invocation}', '{budget}', '{company}', '{run}', '{attempt}', 'extraction', 10, 12, 'USD', 'settled', 'reservation', now(), now(), now() + interval '10 minutes');" +
            $"insert into platform.ai_budget_audit (company_id, period_start, actor, action, amount, details) values " +
            $"('{company}', (date_trunc('month', now()))::date, 'backup-test', 'enforcement_activated', 0, '{{}}'::jsonb);");

        var result = new Seeded(company, user, document, run, attempt, invocation, budget);
        _seeded.Add(result);
        return result;
    }

    private async Task<string> CreateDatabaseAsync(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await ExecuteAsync(builder.ConnectionString, $"create database \"{database}\";");

        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
    }

    private async Task DropDatabaseAsync(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await ExecuteAsync(builder.ConnectionString, $"drop database if exists \"{database}\" with (force);");
    }

    private static string MigrationsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "supabase", "migrations");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("supabase/migrations was not found above the test binary.");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }
}
