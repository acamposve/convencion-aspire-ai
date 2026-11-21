# P1-007 â€” Backup and restore

**Status:** logical backup, isolated restore, consistency checks and object-storage recovery are implemented
and tested against PostgreSQL 17. Scheduling, retention and RPO/RTO commitments remain operator/product
decisions and are **not** set by this work.

## 1. What this protects â€” and what it does not

| Protected | How |
|---|---|
| Application schema | reproduced from `supabase/migrations` (single source of truth, ADR-023) |
| Tenant, company and identity data | per-table CSV export inside one `REPEATABLE READ` snapshot |
| Financial ledger, reservations, budgets, audit | same export; restore verifies invariants |
| Document metadata and processing state | same export |
| Document bytes | separate storage domain: a directory copy with its own SHA-256 manifest |
| Integrity of the set | manifest with per-file SHA-256, row counts, schema fingerprint, migration high-water mark |

| Not protected by this tool | Why |
|---|---|
| Managed PITR / continuous WAL archiving | capability of the hosting provider, **not verified** by this work; do not assume it is enabled |
| Supabase Storage bucket export | requires provider credentials; documented as an operator step, not tested here |
| Secrets, configuration, DNS | outside the database; manage with the deployment platform |
| Cross-region copies | infrastructure decision |

## 2. Architecture

```
backup set/
  manifest.json            migration version, schema fingerprint, table row counts + SHA-256
  data/app.company.csv     one file per table, COPY ... TO STDOUT (FORMAT csv, HEADER true)
  data/platform.*.csv
  storage/<relative>       optional copy of the document storage root
  restore-report.json      written by a successful restore
```

- **One snapshot.** `create` opens a `REPEATABLE READ` transaction before counting or exporting, so every
  table reflects the same database state. Without it a concurrently written row could be counted in one
  table and missed in another.
- **Schema is not dumped.** It is applied from the repository's migrations during restore, then the target
  schema fingerprint must equal the backup's; a mismatch aborts **before** any data is loaded.
- **Reference data is merged, not duplicated.** Migrations seed roles, capabilities and taxonomy; those
  tables are merged with `ON CONFLICT DO NOTHING` when the target already contains them. Tenant data is
  copied verbatim, and its row counts must match the manifest exactly.
- **Restore is atomic.** The whole data load runs in one transaction with `SET LOCAL
  session_replication_role = replica`; a failure rolls back to the migrated, empty schema, the tool removes
  the migration history and schemas it created, and writes `restore-failed.json`. A retry with a good set
  starts from an empty target again.
- **Manifest paths are untrusted.** Schema and table names must match `^[a-z][a-z0-9_]*$`, and every
  manifest path is canonicalized and must stay inside the backup or storage root. Absolute paths and `..`
  segments are rejected.
- **Storage is a separate recovery domain.** The database never contains document bytes, and a database
  backup alone cannot recover a lost document.

## 3. Commands

The tool is `src/Convenciones.Backup` (`dotnet run --project src/Convenciones.Backup -- â€¦`).

```bash
# create a set (optionally including a storage root)
create  --connection "<postgres cs>" --output ./backups/2026-11-21 [--storage-root /var/convenciones/documents]

# verify every hash and length before trusting the set
verify  --directory ./backups/2026-11-21

# restore into an EMPTY database whose name is explicitly confirmed
restore --connection "<target cs>" --directory ./backups/2026-11-21 \
        --migrations ./supabase/migrations --confirm convenciones_restore \
        [--storage-root /var/restore/documents] [--replace]

# post-restore consistency report (exit code 0 only when every check passes)
check   --connection "<target cs>" [--storage-root /var/restore/documents] [--directory ./backups/2026-11-21]
```

`--replace` is required to write into a non-empty storage root. `check --directory` compares every table's
row count against the manifest (so an incomplete restore cannot pass) and reports a `restore-failed.json`
marker if one is present.

Exit codes: `0` success, `1` failure, `2` usage, `3` integrity failure (tampering, truncation, schema
mismatch, failed post-restore checks).

## 4. Restore runbook

1. **Prepare an isolated target.** A new database or a new cluster. Never point `restore` at staging or
   production: the tool refuses a non-empty target and requires `--confirm` to equal the target database
   name, but the operator is still responsible for the connection string.
2. **Create the cluster roles** if the target is a fresh cluster (the application roles are created by the
   bootstrap migrations; a cluster restored from scratch also needs the login roles the deployment uses).
3. **Verify the set** with `verify`. Do not proceed on an integrity failure.
4. **Restore the database** with `restore`. It applies migrations, compares the applied migration
   high-water mark and the schema fingerprint (columns, constraints, indexes, triggers, RLS policies and
   RLS status), loads data in one transaction with `session_replication_role = replica` (requires a role
   with the replication attribute), resets sequences, then runs `check` and writes
   `restore-report.json`. It aborts and rolls back if the checks fail.
5. **Restore object storage** by copying the `storage/` directory into the target storage root (the tool
   does this when `--storage-root` is supplied), or by re-hydrating the bucket from the provider's export.
6. **Run `check` again** with the storage root: it reports missing objects (database rows without a file)
   and orphan objects (files without a row).
7. **Validate before cutover:** row counts vs manifest, `check` exit 0, a sample download through the API
   with a tenant account, and the financial totals of a known company.

## 5. Consistency between database and storage snapshots

The two domains are captured at different moments. After a restore:

| Situation | Action |
|---|---|
| Row exists, object missing | restore that object from the storage backup; if it is gone, the document is unreadable â€” mark it (do not delete the row or its financial history) and inform the tenant |
| Object exists, no row | orphan: quarantine the object, do not delete it automatically |
| Row + object from different instants | content may differ from metadata; re-extract or re-verify the document rather than trusting the pair |
| Processing run in flight at backup time | it restores as it was; leases expire and the worker's recovery reclaims it. Financial reservations remain and the sweeper settles unknown outcomes at their estimate |
| Financial rows | **never deleted or rewritten by recovery.** Ledger, reservations and audit are restored as-is; reconciliation follows its normal delta path |

## 6. Security

- Backup sets contain personal and financial data. Store them encrypted at rest (volume or archive
  encryption) and access-controlled; never commit them to the repository.
- The manifest contains no credentials and no document contents, only paths, sizes and hashes.
- Restore requires a privileged connection (replication role). Use a dedicated operator credential, not
  the application role.
- `restore` refuses non-empty targets and mismatched confirmation, so a wrong connection string cannot
  silently replace an existing deployment.
- Tenant isolation is preserved because the restore is a whole-database load; no per-tenant path exists.

## 7. RPO / RTO â€” capability, not a promise

This tool provides **on-demand logical backups**. It does not schedule them, and it does not provide
point-in-time recovery.

- **RPO** is the interval between backups. It is a scheduling decision; the tool records
  `createdAt` in the manifest, which is the measurement point.
- **RTO** is restore duration. Measure it by timing a `restore` of a production-sized set into an isolated
  environment; do not quote a number that has not been measured.
- Managed PITR, if the hosting plan supports it, must be **verified in the provider console** and tested
  with an actual restore before it is mentioned in any commitment.

No numerical RPO/RTO is asserted by this document.

## 8. Limitations

- Logical, consistent-snapshot backup â€” not WAL/PITR. A crash between backups loses everything since the
  last set.
- `restore` requires `session_replication_role`, i.e. a superuser or a role with `REPLICATION`.
- Reference-data tables are merged; rows deleted from a reference table after the backup are not
  resurrected, and runtime changes that collide on a unique key are skipped (reported in the checks).
- Storage backup is a directory copy. Supabase Storage requires the provider's own export tooling; the
  file layout and manifest format are compatible, but that path is not covered by the automated tests.
- No scheduler, retention or off-site replication is included.

## 9. Test evidence

`tests/Convenciones.IntegrationTests/BackupRestoreTests` (PostgreSQL 17 containers):

- backup creation, verification and tamper detection (a modified CSV fails the SHA-256 check);
- restore into a **new empty database** with row counts matching the manifest;
- tenant, user, document, run, usage, budget, reservation and audit rows preserved with their values;
- restore refused on a non-empty target and on a mismatched `--confirm`;
- storage backup, restore, missing-object and orphan-object detection.

## 10. Open decisions

- Backup frequency, retention and off-site location (product/operations).
- Whether managed PITR is available on the chosen plan and what it costs.
- Whether document storage backup is operator-run or automated.
- Measured RTO for the production data size, to be established by a drill.
