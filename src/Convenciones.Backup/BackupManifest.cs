using System.Text.Json.Serialization;

namespace Convenciones.Backup;

/// <summary>Serializable description of one logical backup set.</summary>
public sealed record BackupManifest(
    string FormatVersion,
    DateTimeOffset CreatedAt,
    string SourceDatabase,
    string ServerVersion,
    string? MigrationVersion,
    string SchemaFingerprint,
    IReadOnlyList<BackupTableEntry> Tables,
    BackupStorageManifest? Storage);

public sealed record BackupTableEntry(
    string Schema,
    string Name,
    long Rows,
    long Bytes,
    string Sha256);

public sealed record BackupStorageManifest(IReadOnlyList<BackupStorageEntry> Files);

public sealed record BackupStorageEntry(string Path, long Bytes, string Sha256);

/// <summary>Structured result of a restore, written next to the backup as JSON.</summary>
public sealed record RestoreReport(
    DateTimeOffset CompletedAt,
    string TargetDatabase,
    int AppliedMigrations,
    long RestoredRows,
    int RestoredStorageFiles,
    IReadOnlyList<string> Checks);

/// <summary>Structured result of the post-restore consistency checks.</summary>
public sealed record CheckReport(
    DateTimeOffset CheckedAt,
    long Tables,
    long TablesWithRowLevelSecurity,
    IReadOnlyList<string> FinancialViolations,
    IReadOnlyList<string> ReferentialViolations,
    IReadOnlyList<string> MissingObjects,
    IReadOnlyList<string> OrphanObjects,
    IReadOnlyList<string> IncompleteTables,
    bool FailedRestore,
    bool Passed);

/// <summary>Raised when a backup cannot be trusted: tampering, truncation or a format mismatch.</summary>
public sealed class BackupIntegrityException(string message) : Exception(message);
