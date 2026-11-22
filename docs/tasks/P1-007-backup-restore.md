# P1-007 — Backup and restore

**Status:** implemented and tested for the database; object storage covered by a directory-level backup and
consistency checks. A local, timed restore drill now exists (2026-11-22). Scheduling, retention, managed
PITR and the production-target RTO remain open.

## What was delivered

| Component | Location |
|---|---|
| Backup/restore/check tool | `src/Convenciones.Backup` |
| Runbook and limitations | `docs/operations/p1-007-backup-restore.md` |
| Integration tests | `tests/Convenciones.IntegrationTests/BackupRestoreTests.cs` |
| Timed restore drill and its caveats | [docs/operations/restore-drill.md](../operations/restore-drill.md) |

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

`BackupRestoreTests` (13 tests, PostgreSQL 17 containers): tamper detection; restore into a new empty
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

## Measured drill (2026-11-22)

The full lifecycle `create → verify → restore (into a clean database) → check` was executed on a
production-shaped synthetic set: 10,000 documents, 5,000 agreements, 50,000 agreement clauses, 50,000
extracted clauses, 11,200 usage records, 11,000 reservations, 25,000 audit rows — 221,549 rows across 46
tables — plus 10,000 storage objects (122 MB). The backup set was 229.9 MB.

| Phase | Exit | Duration |
|---|---|---|
| `create` | 0 | 14.08 s |
| `verify` | 0 | 2.68 s |
| `restore` (32 migrations + 221,529 inserted rows + 10,000 files) | 0 | 16.77 s |
| `check` (46 tables, 46 with RLS, no violations) | 0 | 1.98 s |
| **Total lifecycle** | — | **35.74 s** |
| `restore` into a non-empty target | 1 (refused, unchanged) | — |

Row counts matched table by table, `check` passed with no financial, referential, missing-object, orphan or
incomplete findings, and three spot-checked documents had a file SHA-256 equal to their stored checksum.

**This is a local Docker Desktop baseline, not a production RTO.** It shares one disk between set and
target, has no network hop, never touches Supabase and exercised 19 tables at zero rows. Full record,
reading guidance and limits: [restore-drill.md](../operations/restore-drill.md). The first response that
uses this path is in the [operations runbook](../operations/runbook.md).

## Open decisions

- Backup frequency and retention (not invented here).
- Managed PITR availability and cost — must be verified with the provider.
- Whether storage backup is scheduled or operator-run.
- Measured RTO, to be established by a drill on production-sized data. **Partly answered:** the local
  drill above measures the lifecycle on production-shaped data (35.74 s); the drill against a Supabase
  target — the one that would justify an RTO in a release note — has not been run.

These are owner decisions; they are tracked in
[pending owner sign-offs](../operations/owner-signoffs.md).
