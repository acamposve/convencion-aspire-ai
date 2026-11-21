using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
                target, backup, database, MigrationsDirectory(), null, CancellationToken.None);

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
            await BackupManager.RestoreAsync(target, backup, database, MigrationsDirectory(), null, CancellationToken.None);

            await Should.ThrowAsync<InvalidOperationException>(() =>
                BackupManager.RestoreAsync(target, backup, database, MigrationsDirectory(), null, CancellationToken.None));
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
                BackupManager.RestoreAsync(target, backup, "some_other_database", MigrationsDirectory(), null, CancellationToken.None));
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
                target, backup, database, MigrationsDirectory(), restoredRoot, CancellationToken.None);

            report.RestoredStorageFiles.ShouldBeGreaterThanOrEqualTo(1);
            File.Exists(Path.Combine(restoredRoot, relative)).ShouldBeTrue();

            var clean = await BackupManager.CheckAsync(target, restoredRoot, CancellationToken.None);
            clean.MissingObjects.ShouldBeEmpty();
            clean.Passed.ShouldBeTrue();

            File.Delete(Path.Combine(restoredRoot, relative));
            var missing = await BackupManager.CheckAsync(target, restoredRoot, CancellationToken.None);
            missing.MissingObjects.ShouldContain(relative);
            missing.Passed.ShouldBeFalse();

            await File.WriteAllTextAsync(Path.Combine(restoredRoot, "orphan.bin"), "x");
            var orphan = await BackupManager.CheckAsync(target, restoredRoot, CancellationToken.None);
            orphan.OrphanObjects.ShouldContain("orphan.bin");
        }
        finally
        {
            await DropDatabaseAsync(database);
        }
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
