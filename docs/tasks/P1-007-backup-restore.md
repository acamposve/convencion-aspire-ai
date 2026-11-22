# P1-007 — Backup and restore

**Status:** implemented and tested for the database; object storage covered by a directory-level backup and
consistency checks. Scheduling, retention and RPO/RTO remain open.

## What was delivered

| Component | Location |
|---|---|
| Backup/restore/check tool | `src/Convenciones.Backup` |
| Runbook and limitations | `docs/operations/p1-007-backup-restore.md` |
| Integration tests | `tests/Convenciones.IntegrationTests/BackupRestoreTests.cs` |

## Design decisions

1. **Schema comes from migrations, data from the backup.** The manifest records the migration high-water
   mark and a schema fingerprint; a restore into a schema that does not match aborts before loading data.
2. **One snapshot.** All tables are exported inside a single `REPEATABLE READ` transaction, so the set is a
   real database state rather than a per-table sampling.
3. **Storage is a separate domain.** Bytes are copied with their own manifest; the database never contains
   them. Missing and orphaned objects are reported, not guessed at.
4. **Restore is fail-closed.** Empty target required, database name confirmed explicitly, migrations
   applied, fingerprint compared, row counts compared, post-restore checks must pass.
5. **Financial history is immutable across recovery.** Restored as-is; no repair passes rewrite ledger,
   reservation or audit rows.

## Test evidence

`BackupRestoreTests` (5 tests, PostgreSQL 17 containers): tamper detection; restore into a new empty
database with all row counts matching; tenant/financial/audit preservation verified by value; refusal on
non-empty and mis-confirmed targets; storage backup/restore plus missing and orphan detection.

Full suite at the time of writing: unit 244, integration 328, Release build 0 warnings / 0 errors.

## Hardening (audit follow-up)

- Manifest-derived paths are confined to the backup/storage root; schema and table names must match a SQL
  identifier allowlist; storage capture skips reparse points and de-duplicates paths.
- The data load is one transaction; a failure rolls back, removes the migration history and schemas it
  created, and writes `restore-failed.json`. A retry with a good set works from an empty target.
- The applied migration high-water mark must match the manifest, and the schema fingerprint now covers
  constraints, indexes, triggers, RLS policies and RLS status, not only columns.
- `check --directory` compares every table's row count to the manifest; checks also cover cross-tenant
  attribution and identity-sequence alignment.
- Storage restore refuses a non-empty root unless `--replace` is passed.

## Open decisions

- Backup frequency and retention (not invented here).
- Managed PITR availability and cost — must be verified with the provider.
- Whether storage backup is scheduled or operator-run.
- Measured RTO, to be established by a drill on production-sized data.

These are owner decisions; they are tracked in
[pending owner sign-offs](../operations/owner-signoffs.md).
