# T19 restore drill — timed backup and restore of a production-shaped set

**Status:** measured on 2026-11-22, one run, on a disposable local PostgreSQL 17 container. The numbers
below are an **environment-specific baseline** for this workstation, not a production RTO and not a
commitment of any kind. The backup schedule, retention and the RTO that must be rehearsed against
Supabase are still unsigned owner decisions ([owner sign-offs](../operations/owner-signoffs.md#1-release-and-acceptance-sign-offs)).

This drill exists because T19's first acceptance criterion is that "a restore is rehearsed and timed, with
the measured duration recorded", and because [P1-007 §7](p1-007-backup-restore.md)
refuses to quote an RTO that has not been measured.

## 1. What this establishes — and what it does not

Establishes, for the logical backup tool `src/Convenciones.Backup` on this host:

- the full lifecycle `create → verify → restore (into a clean, empty database) → check` runs green on a
  set shaped like the confirmed profile (10,000 documents, 5,000 agreements, 50,000 clauses) including
  10,000 storage objects;
- per-phase wall-clock durations and exit codes;
- that row counts, the schema fingerprint, the financial and referential invariants and the
  database↔storage pairing survive the round trip;
- that a restore into a **non-empty** target is refused instead of overwriting a deployment.

Does **not** establish, and does not claim:

- a production RTO. The target was a local container on the same disk as the backup set; production is a
  managed Supabase project reached over a network with its own pooler, disk and cold-cache behaviour;
- the cost or duration of a **Supabase Storage** export/import: the storage side of this drill is a
  directory copy of a local filesystem, which is exactly what the tool supports and not what the provider
  does ([P1-007 §8](p1-007-backup-restore.md#8-limitations));
- managed PITR. No point-in-time recovery was tested or assumed;
- behaviour under load: the source database was idle.
- anything about the tables this dataset left empty (§3.3).

## 2. Environment

| Item | Value |
|---|---|
| Host | LENOVO 20TBS1P900 (Lenovo ThinkPad), Windows 11 Pro 10.0.26300 |
| CPU | 11th Gen Intel Core i5-1135G7 @ 2.40 GHz — 4 cores / 8 logical processors |
| RAM | 15.7 GiB host; Docker Desktop VM with 8 CPUs and 7.6 GiB |
| Disk | 475.5 GiB, 172.7 GiB free (backup set, restored copy and container volume on the same device) |
| Docker | Docker Desktop 29.9.3, `overlayfs` storage driver |
| PostgreSQL | `postgres:17-alpine`, server 17.12, default settings, `max_connections = 100` |
| Backup tool | `src/Convenciones.Backup`, Release build, invoked as `dotnet run --project src/Convenciones.Backup -c Release --no-build` |
| .NET | SDK 10.0.404 and runtime 10.0.15, used through the user-profile host `%USERPROFILE%\.dotnet\dotnet.exe` |
| `.NET` SDK caveat | `global.json` pins `10.0.401` with `rollForward: latestFeature`; the machine-level `10.0.401`/`10.0.402`/`10.0.403` directories are empty leftovers, so the user-profile `10.0.404` host was used. `global.json` was **not** modified |
| Source database | `convenciones_source`, schema from `supabase/migrations` (32 applied), 135 MB |
| Restore target | `convenciones_restore`, a new empty database in the same cluster |
| Connection | local superuser `postgres` (the tool needs `session_replication_role`, i.e. a replication-capable role) |
| Timing method | PowerShell `Stopwatch` around each CLI invocation, including .NET host start-up (~1 s per phase) |

Port note: the first container start failed because Windows had TCP `55432` inside an excluded port range;
the drill ran on `54329`. That is a host detail, not a result.

## 3. Dataset

Synthetic only — no real contract, company, user or financial data. Generated with a disposable SQL seed
script and a local file generator, both deleted after the drill (§9). Timestamps, identifiers and amounts
are random or derived deterministically; nothing is copied from the legacy material.

### 3.1 Database

| Table | Rows | Table | Rows |
|---|---|---|---|
| `platform.document` | 10,000 | `platform.processing_run` | 6,000 |
| `app.agreement` | 5,000 | `platform.processing_attempt` | 5,600 |
| `app.agreement_clause` | 50,000 | `platform.document_text` | 6,000 |
| `platform.extracted_clause` | 50,000 | `platform.clause_classification` | 40,000 |
| `platform.ai_usage_record` | 11,200 | `platform.ai_budget` | 200 |
| `platform.ai_budget_reservation` | 11,000 | `platform.ai_budget_audit` | 200 |
| `platform.audit_entry` | 25,000 | `platform.company_quota` | 200 |
| `app.company` | 200 | `platform.processing_usage` | 200 |
| `platform.user_account` / `user_profile` / `user_role` | 201 / 201 / 201 | `platform.worker_heartbeat` | 1 |
| **All 46 `app`/`platform` tables** | **221,549** | | |

Shape choices that matter for a restore test:

- **Statuses are mixed, not uniform**: 5,400 `Completed` runs, 200 `Failed` (with `error_code`), 150
  `Queued`, 50 `Cancelled`; document `extraction_status` is derived from those runs, and 100 documents are
  soft-deleted (`deleted_at` set) while their object still exists.
- **Financial data satisfies the tool's invariants**: `platform.ai_budget.reserved_amount` is the sum of
  the outstanding reservations (`reserved`, `submitted`, `outcome_unknown`) per budget, and the reservation
  statuses spread across `settled` / `reserved` / `submitted` / `outcome_unknown` / `released` with the
  matching `submitted_at` / `settled_at` columns. `check` recomputes those invariants after the restore.
- **Reference data is the migration seed**: `comparison_heading`, `comparison_heading_group`, `country`,
  `capability`, `clause_category`, `role` and `role_capability` were left exactly as the migrations wrote
  them, so the restore also exercises the "merge reference data instead of duplicating it" path.
- **`audit_entry` is an identity column** and the append-only trigger was in force during the seed; the
  restore loads it through `COPY` with `session_replication_role = replica`.

### 3.2 Object storage

| Item | Value |
|---|---|
| Objects | 10,000 files under `documents/<company-id>/<document-id>.pdf` |
| Total bytes | 122,000,680 (116.3 MiB), mean 12.2 KiB, range 4 KiB–20 KiB |
| Content | `%PDF-1.7` header followed by deterministic pseudo-random bytes (not valid PDFs; nothing extracts them) |
| Integrity | real SHA-256 of every file written back into `platform.document.checksum_sha256`, so the restore can be checked against the database, not only against the manifest |

Storage **volume** is deliberately not production-shaped: 10,000 × ~12 KiB keeps the drill inside a laptop
while the count, path layout and hashing cost are realistic. The bucket is a separate recovery domain and
the tool copies a directory, so file *size* changes the copy time roughly linearly (§6).

### 3.3 What the dataset does not cover

Nineteen tables were left empty: `agreement_annex`, `company_category`, `company_sector`, `company_type`,
`economic_activity`, `employer_offer`, `meeting_agreement`, `municipality`, `negotiation_*`,
`news_article`, `news_category`, `state`, `statute`, `statute_article`, `union_petition`,
`ai_budget_shadow`, `document_object_deletion`. They are in the backup set (46 tables are exported and
their row counts are compared by `check`), but this drill proved them at zero rows, not with data. The
integration suite covers most of them with small fixtures
([P1-007 §9](p1-007-backup-restore.md#9-test-evidence)); a second drill with populated negotiation, news
and statute tables is the natural follow-up.

## 4. Commands and exit codes

Source: `convenciones_source`; target: `convenciones_restore`. Connection strings keep the password out of
this page.

```powershell
# 0. disposable cluster and schema
docker run -d --name t19-drill-pg -e POSTGRES_PASSWORD=<drill> `
  -e POSTGRES_DB=convenciones_source -p 54329:5432 postgres:17-alpine
$env:ConnectionStrings__convenciones = "Host=localhost;Port=54329;Database=convenciones_source;Username=postgres;Password=<drill>"
$env:MIGRATIONS_PATH = "C:\proyectos\convenciones\supabase\migrations"
dotnet run --project src/Convenciones.Migrator -c Release --no-build

# 1. production-shaped seed (ad hoc SQL, §3) and 10,000 storage objects under .\storage
#    (row counts and hashes recorded above)

# 2. lifecycle under test
dotnet run --project src/Convenciones.Backup -c Release --no-build -- create `
  --connection $env:ConnectionStrings__convenciones `
  --output .\backup --storage-root .\storage

dotnet run --project src/Convenciones.Backup -c Release --no-build -- verify --directory .\backup

# 3. clean target, then restore into it
docker exec t19-drill-pg psql -U postgres -d postgres -c "create database convenciones_restore"

dotnet run --project src/Convenciones.Backup -c Release --no-build -- restore `
  --connection "Host=localhost;Port=54329;Database=convenciones_restore;Username=postgres;Password=<drill>" `
  --directory .\backup --migrations C:\proyectos\convenciones\supabase\migrations `
  --confirm convenciones_restore --storage-root .\restore-storage

dotnet run --project src/Convenciones.Backup -c Release --no-build -- check `
  --connection "Host=localhost;Port=54329;Database=convenciones_restore;Username=postgres;Password=<drill>" `
  --storage-root .\restore-storage --directory .\backup
```

| # | Phase | Command | Exit | Duration |
|---|---|---|---|---|
| 0 | Cluster + schema | `docker run … postgres:17-alpine`; `Convenciones.Migrator` | 0 | container ready when `pg_isready` answered; 3.6 s for 32 migrations |
| 1 | Seed + storage | ad hoc SQL + file generator | 0 | 55.4 s seed; 12.6 s files; the checksum write was not timed separately |
| 2 | **create** | `create --output .\backup --storage-root .\storage` | 0 | **14.08 s** |
| 3 | **verify** | `verify --directory .\backup` | 0 | **2.68 s** |
| 4 | Clean target | `create database convenciones_restore` | 0 | **0.23 s** |
| 5 | **restore** | `restore --confirm convenciones_restore --storage-root .\restore-storage` | 0 | **16.77 s** |
| 6 | **check** | `check --storage-root .\restore-storage --directory .\backup` | 0 | **1.98 s** |
| 7 | Negative path | `restore` into the non-empty `convenciones_source` | **1** | not timed |

`create` reported `{"tables":46,"rows":221549,"storageFiles":10000}`.

The backup set was 241,090,180 bytes (229.9 MiB): 116,703,532 bytes of table CSV (111.3 MiB), 122,000,680
bytes of storage copies (116.3 MiB) and the manifest. Manifest facts: format
`convenciones-backup/1`, migration high-water mark `20261121000700_worker_heartbeat.sql`, schema
fingerprint `fa30a70cddedbff2e10d387e42ec951d`.

The negative path produced the intended refusal and changed nothing:

```text
failed: Target 'convenciones_source' already has 46 application table(s). Restore only into an empty database.
```

## 5. Verification results

`restore` wrote `restore-report.json` with `AppliedMigrations: 32`, `RestoredRows: 221,529` and
`RestoredStorageFiles: 10,000`, and reported:

| Check | Result |
|---|---|
| Reference data merged, not duplicated | `platform.capability: +23`, `platform.clause_category: +0` — 20 reference rows the migrations had already inserted were skipped by the merge, so `RestoredRows` is 221,529 = 221,549 − 20 |
| Schema fingerprint | matches the backup (columns, constraints, indexes, triggers, RLS policies and RLS status) |
| Row counts vs manifest | match for all 46 tables |
| Financial and referential invariants | passed (budget reservation sum, usage attribution, orphan-free references, sequence high-water marks) |
| Storage | 10,000 files restored |

Post-restore independent queries against `convenciones_restore`:

| Verification | Expected | Observed |
|---|---|---|
| `platform.document` | 10,000 | 10,000 |
| `app.agreement` / `app.agreement_clause` | 5,000 / 50,000 | 5,000 / 50,000 |
| `platform.extracted_clause` / `clause_classification` | 50,000 / 40,000 | 50,000 / 40,000 |
| `platform.ai_usage_record` / `ai_budget_reservation` | 11,200 / 11,000 | 11,200 / 11,000 |
| `platform.audit_entry` | 25,000 | 25,000 |
| Non-empty tables | 27 | 27, every count identical to the source |
| Storage files in the restored root | 10,000 / 122,000,680 bytes | 10,000 / 122,000,680 bytes |
| `check` report | `Passed: true`, 46 tables, 46 with RLS | no financial, referential, missing-object, orphan or incomplete findings |
| Restored database size | ~source | 130 MB vs 135 MB source (differing free space only) |
| Spot-checked document bytes | file SHA-256 = `platform.document.checksum_sha256` | 3 of 3 matched (4,133 / 4,170 / 4,207-byte files) |
| Tenancy spread | 200 companies | documents and agreements span all 200 companies |

The backup tool's own integration suite (`BackupRestoreTests`, 13 tests, PostgreSQL 17 containers) was
re-run on the same Release build after the drill: 13 passed, 0 failed. It covers the paths the drill does
not stress — tamper detection, refusal on a mismatched `--confirm`, and missing/orphan storage objects.

## 6. Phase timings, read correctly

| Phase | Seconds | Share | What it actually measures |
|---|---|---|---|
| `create` | 14.08 | 39% | one `REPEATABLE READ` snapshot, 46 `COPY … TO STDOUT`, SHA-256 of 241 MB, directory copy of 10,000 files |
| `verify` | 2.68 | 8% | SHA-256 of every file in the set (241 MB) and length checks |
| clean target | 0.23 | 1% | `CREATE DATABASE` |
| `restore` | 16.77 | 47% | 32 migrations (~3.6 s, measured separately), schema fingerprint, one transactional `COPY` load of the 221,549-row CSV set (221,529 inserted after the reference merge), 10,000 file copies, sequence reset, and an embedded `check` |
| `check` | 1.98 | 6% | 46 count queries, 10 invariant queries, sequence scan, 10,000-file existence scan |

Total lifecycle `create → check`: **35.74 s** for a 230 MiB set and 221,549 rows.

Reading it honestly:

- The set is dominated by two costs that scale differently: table bytes (111 MiB) and object count
  (10,000 files). A production set with 1–2 MB PDFs would spend most of its time in the file copy and the
  SHA-256 passes, not in the database.
- `restore` here includes applying 32 migrations to an empty database. On a fresh cluster that cost is
  paid once. The tool does not instrument its internal phases, so the split between migration, data load,
  file copy and the embedded `check` is an **inference** from the separately measured 3.6 s migration run,
  not a measurement.
- The backup and the restore target shared one physical disk and one CPU with no network. A Supabase
  restore reads over the network through the session pooler and writes into managed storage.
- One run, no repetition: this is a measured baseline, not a distribution.

**Operational caveat (explicit):** these timings are specific to this workstation, this Docker Desktop VM,
this PostgreSQL 17 container and local-disk I/O. They must **not** be read as a production RTO, and they do
not predict a Supabase restore over the network, a provider-side storage export, or the provider's own
maintenance behaviour. The RTO to publish is the one measured against the production-shaped set in the
production target environment, after the owner decides which RTO to rehearse
([owner sign-off #3](../operations/owner-signoffs.md)).

## 7. Limits of this drill

- Local only; no Supabase project, no pooler, no TLS, no cross-region path, no provider storage export.
- The connection used the cluster superuser, so row-level security was bypassed during seed and restore
  (`session_replication_role = replica`), exactly as the tool's documented privilege model requires
  ([P1-007 §8](p1-007-backup-restore.md#8-limitations)). A privileged operator credential is the
  production equivalent, not the application role.
- Nineteen tables carried zero rows (§3.3); storage objects were small (§3.2).
- Single run, idle source, no concurrent writes; the `REPEATABLE READ` snapshot was therefore never under
  contention.
- No scheduler, no retention, no off-site copy: the drill ran the lifecycle by hand once.

## 8. Consequences for the runbook

- The restore procedure in [runbook.md](runbook.md#5-backup-and-restore) now quotes a measured local
  figure with its caveat, instead of an estimate.
- [P1-007 §7](p1-007-backup-restore.md) records the same number and
  keeps "no numerical RTO/RPO commitment" as the policy: a local baseline is not a promise.
- The drill must be repeated (a) after the owner decides the RTO to rehearse, and (b) against a Supabase
  target before any release note mentions recovery time.

## 9. Reproduction and cleanup

Reproduction needs Docker, a `postgres:17` image, the Release build and a synthetic seed. The seed SQL
and the storage generator were **disposable**: they lived outside the repository and were deleted after
the metrics were recorded, so this page is the record of what they produced (§3), not a copy of them. To
repeat the drill, regenerate a set with at least the row counts above, keep the financial invariants
consistent (`reserved_amount` = outstanding reservations), write the real SHA-256 of each object into
`platform.document.checksum_sha256`, then run the four commands in §4 and record exit codes and durations.

Cleanup performed after the run:

| Artifact | Disposition |
|---|---|
| Container `t19-drill-pg` (source and restore databases, seeded data) | `docker rm -f` — verified absent |
| Backup set `%TEMP%\t19-drill\backup` (241 MB, 10,047 files) | deleted with the workspace |
| Storage root `%TEMP%\t19-drill\storage` (10,000 objects) and the restored copy `%TEMP%\t19-drill\restore-storage` | deleted with the workspace |
| Seed SQL, checksum SQL, timing log, phase stdout | deleted with the workspace |
| Repository | nothing added: `git status` shows no drill artifact; `docs/project-assessment.md` was already untracked before the drill and is untouched |
