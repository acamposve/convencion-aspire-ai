# Validation report — target PostgreSQL schema

**Result: PASSED.** Every mandatory validation item was executed and produced a recorded result,
except two that are explicitly listed as not executable in this environment (§8).

Reproduce with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./supabase/tests/run-validation.ps1
```

---

## 1. Environment

| Item | Value |
|---|---|
| Server | **PostgreSQL 17.11** (`postgres:17-alpine`, `x86_64-pc-linux-musl`) |
| Runtime | Docker 29.9.2 on Linux containers |
| Client | `psql` 17.11 inside the container |
| Database | `convenciones`, created empty for every run |
| Auth | `POSTGRES_HOST_AUTH_METHOD=trust` on a throwaway container — **no password exists anywhere in the artifacts** |
| Supabase CLI | **not installed** (targets 2.122.0; see §8) |
| Remote Supabase | **never contacted.** No `db push`, no project reference, no credentials |

The container is disposable: it is created by the runner and removed with `docker rm -f`.

---

## 2. Build from scratch — PASS

A fresh database was dropped and recreated, then all migrations were applied in filename order with
`ON_ERROR_STOP=1`, followed by the seed.

```
=== 2. build from scratch ===
  applied 20260101000000_bootstrap.sql
  applied 20260101000100_platform_identity.sql
  applied 20260101000200_platform_documents_audit.sql
  applied 20260101000300_app_catalog.sql
  applied 20260101000400_app_comparison.sql
  applied 20260101000500_app_legal.sql
  applied 20260101000600_app_agreements.sql
  applied 20260101000700_app_negotiation.sql
  applied 20260101000800_app_publishing.sql
  applied 20260101000900_grants.sql
  applied 20260101001000_rls_policies.sql
  11 migrations applied
  applying seed.sql
```

11 migrations, 1 seed, zero errors.

**One defect was found and fixed during this validation:** two indexes in the comparison migration
shared a name within the `app` schema (`comparison_heading_group_name_key` on two different
tables), which PostgreSQL rejects. Renamed to `comparison_heading_name_key` and
`comparison_heading_order_idx`. This is exactly the kind of error a from-scratch build exists to
catch; a schema applied incrementally to an existing database might never have surfaced it.

---

## 3. Structural verification — PASS

`supabase/tests/verify.sql`, 14 checks. Actual measured values:

| Check | Expected | Measured | Verdict |
|---|---|---|---|
| Schemas present | `app`, `platform` | 2 (+ `public`, unused) | PASS |
| Tables | 31 | **31** (23 `app`, 8 `platform`) | PASS |
| Columns | — | 295 | — |
| Primary keys | one per table | **31** | PASS |
| Foreign keys | > 0, all with explicit rules | **72** | PASS |
| FKs left on database default (`NO ACTION`) | 0 | **0** | PASS |
| CHECK constraints | — | **65** | — |
| Unique indexes (incl. PKs) | — | **62** of 95 indexes | — |
| Native enum types | 6 | **6** | PASS |
| RLS enabled on every table | 100% | **31 of 31** | PASS |
| Tables without RLS | 0 | **0** | PASS |
| RLS policies | > 0 | **160** | PASS |
| Triggers | — | **32** | — |
| Legacy table names present | **0** | **0** | PASS |
| Legacy column names present | **0** | **0** | PASS |

The two legacy-name checks searched for the 25 legacy table names and 11 representative legacy
column names (`codigo_empresa`, `codigo_contrato`, `status_publicacion`, `estatus_bitacora`,
`status_articulo`, `articulo_cerrado`, `campo_comparativo`, `nro_articulos`, `total_articulos`,
`Clave_usuario`, `codigo_bitacora`). Zero matches: no mechanical translation occurred.

### Index inventory relevant to the expected queries

| Query pattern | Supporting index |
|---|---|
| Clauses of an agreement, in numeric order | `agreement_clause_order_idx (agreement_id, number_int, number_suffix)` |
| Comparison: clauses by heading | `agreement_clause_heading_idx (heading_id)` |
| Published agreements for the portal | `agreement_published_idx (valid_from desc) WHERE state = 'Published'` |
| Statute articles by heading | `statute_article_heading_idx (heading_id) WHERE heading_id IS NOT NULL` |
| Agreement history per company | `agreement_company_idx (company_id, valid_from desc)` |
| Audit by entity | `audit_entry_entity_idx (entity_type, entity_id, occurred_at desc)` |
| Clause-number uniqueness | `agreement_clause_number_key (agreement_id, number_int, coalesce(number_suffix,''))` |

---

## 4. Constraint and referential integrity — PASS (32 assertions, 0 failures)

`supabase/tests/constraint-tests.sql`, run inside a single transaction that is rolled back, so the
database is left with zero business rows.

| Group | Assertions | Result |
|---|---|---|
| A — agreement invariants | 3 | 3 PASS |
| B — clause invariants | 8 | 8 PASS |
| C — geography cross-reference | 3 | 3 PASS |
| D — negotiation case consistency | 6 | 6 PASS |
| E — legal library | 4 | 4 PASS |
| F — documents | 3 | 3 PASS |
| G — audit immutability | 2 | 2 PASS |
| H — delete behaviour | 3 | 3 PASS |

Selected actual outputs:

```
PASS: A1 agreement_validity_order rejects valid_to < valid_from
PASS: A2 agreement_publication_consistency rejects Published without actor/timestamp
PASS: A3 one agreement per company per start date
PASS: B1 clause number unique within an agreement
PASS: B2 the same clause number in a DIFFERENT agreement is allowed
PASS: B3 clause classification is mandatory (NOT NULL heading_id)
PASS: B8 duplicate suffixed number rejected (unique on number+suffix)
PASS: C1 composite FK rejects a state that is not in the given country
PASS: C2 composite FK rejects a municipality that is not in the given state
PASS: D1 composite FK rejects a meeting belonging to a different case
PASS: D2 composite FK rejects a petition belonging to a different case
PASS: D3 a petition and meeting from the same case are accepted
PASS: D4 Closed requires closed_at
PASS: D5 Promoted requires promoted_agreement_id
PASS: D6 petition state is a native enum; unknown values are rejected by the type system
PASS: E1 only one LabourLaw statute may exist
PASS: E3 statute article numbers are unique within a statute
PASS: E4 exclusion is contradictory in an economically-compared group and is rejected
PASS: F1 document content type allow-list enforced in the database
PASS: F2 document size limit (20 MiB) enforced in the database
PASS: G1 audit_entry rejects UPDATE
PASS: G2 audit_entry rejects DELETE
PASS: H1 ON DELETE RESTRICT protects a company that has agreements
PASS: H2 ON DELETE CASCADE removed 1 clause(s) with their agreement
```

**Referential integrity** was tested in both directions: legitimate references are accepted
(B2, D3, F3, H3) and illegitimate ones are rejected (C1, C2, C3, D1, D2, H1). Both halves matter —
a suite that only proves rejections could be satisfied by a schema that rejects everything.

---

## 5. RLS and authorisation — PASS (22 assertions, 0 failures)

`supabase/tests/rls-tests.sql`. Part 1 runs against a database with **zero users**; Part 2 creates
fixtures inside a transaction and rolls them back.

| # | Assertion | Result |
|---|---|---|
| R0 | Database has no user accounts before bootstrap | PASS |
| R1 | Bootstrap insert allowed while no user exists | PASS |
| R2 | Bootstrap window inert once a user exists | PASS |
| R3 | `anon` sees only the published agreement | PASS |
| R4 | `anon` cannot read a draft agreement even by id | PASS |
| R5 | `anon` sees only clauses of published agreements | PASS |
| R6 | `anon` has no privilege on the `platform` schema at all | PASS |
| R7 | Active analyst sees both agreements, including drafts | PASS |
| R8 | A **blocked** user sees nothing, on every request | PASS |
| R9 | An **expired** user sees nothing, on every request | PASS |
| R10 | A valid token with **no provisioned profile** sees nothing | PASS |
| R11 | The `authenticated` role is SELECT-only; direct writes are refused | PASS |
| R12 | An analyst without `company.write` is refused by RLS on INSERT | PASS |
| R13 | An editor with `company.write` is allowed to insert | PASS |
| R14 | An analyst without `agreement.write` affects 0 rows on UPDATE | PASS |
| R15 | The publication trigger refuses `agreement.write` without `agreement.publish` | PASS |
| R15b | An editor with `agreement.publish` can publish | PASS |
| R16 | An editor without `negotiation.write` affects 0 rows | PASS |
| R17 | A negotiator with `negotiation.promote` can promote a closed case | PASS |
| R18 | The audit trail is invisible without `audit.read` (0 rows, not an error) | PASS |
| R19 | An administrator with `audit.read` can read the audit trail | PASS |
| R20 | A non-manager reads only their own account (1 of 6) | PASS |

Selected actual outputs:

```
PASS: R2 the bootstrap window is inert once a user exists
PASS: R6 anon has no privilege on the platform schema at all
PASS: R8 a BLOCKED user sees nothing on every request
PASS: R10 an authenticated token with NO provisioned profile sees nothing
PASS: R15 the publication guard trigger refuses agreement.write without agreement.publish
PASS: R20 a non-manager reads only their own account (1 of 6)
```

### 5.1 A finding produced by the tests — RLS filters silently

The first run of this suite produced **three apparent failures** (publish by an analyst, promotion by
an editor, audit read by an analyst). They were not schema failures; they were wrong test
expectations. The measured behaviour is:

| Operation | Failing policy behaviour |
|---|---|
| `INSERT` whose `WITH CHECK` fails | **raises** `insufficient_privilege` |
| `SELECT` / `UPDATE` / `DELETE` whose `USING` fails | **silently matches zero rows** — no error |

This is secure (nothing is changed or revealed) but invisible to the caller. The tests were corrected
to assert row counts for filtering and exceptions only where an error is genuinely raised, and a
dedicated transient role (`writer_only`: `agreement.write` **without** `agreement.publish`) was added
so the publication guard is actually reached — a capability-less caller is filtered out by RLS
*before* the trigger runs.

> **Requirement for the API layer:** every mutating endpoint must inspect the affected row count and
> map zero to `404 not_found` (or `403`). Returning `200 OK` because no exception was thrown would
> report success for a write that never happened — which is precisely the legacy failure mode where
> `execute()` returned `true` regardless of outcome.

---

## 6. No records imported from MySQL — PASS

After migrations and seed, on a freshly built database:

| Table group | Rows | Expected |
|---|---|---|
| `app.company`, `app.agreement`, `app.agreement_clause`, `app.negotiation_case`, `app.statute`, `app.news_article` | **0** | 0 |
| `platform.user_account` | **0** | 0 (Art. VI.6) |
| Seeded reference: `platform.capability` | 22 | 22 |
| Seeded reference: `platform.role` | 4 | 4 |
| Seeded reference: `platform.role_capability` | 53 | 53 |
| Seeded reference: `app.comparison_heading_group` | 5 | 5 |
| Seeded reference: `app.comparison_heading` | 1 | 1 |
| Seeded reference: `app.country` | 1 | 1 |

Runner output: `business and identity rows present: 0  (expected 0)`.

The 10,634 legacy rows are absent. Verified independently by the legacy-name scans in §3 and by the
row counts above. Reference data was **re-authored**, not exported: the five heading groups and one
country are typed into `seed.sql` by hand.

---

## 7. Reproducibility — PASS

Two independent builds (drop database, recreate, apply 11 migrations, seed) were each dumped with
`pg_dump --schema-only -n app -n platform` and compared.

```
fingerprint, build 1: 578bcf778b10bc4bd16d13d88fb3fba4
fingerprint, build 2: 6dc1ce09ef1301f8d3b4ba7df01f07d0
diff: only the \restrict / \unrestrict random token emitted by pg_dump 17.x
after normalising that token: two 4,450-line dumps, byte-identical
```

`pg_dump` 17 inserts a random `\restrict` token per invocation as a security measure. With that
single token normalised, the two schema dumps are identical. **The schema is reproducible.**

Object counts after rebuild also matched: 31 tables, 172 constraints, 160 policies, 32 triggers.

---

## 8. Secrets and environment dependencies — PASS

Scanned all files under `supabase/` for credential-shaped content:

| Pattern searched | Matches |
|---|---|
| `password = …`, `postgres://`, `postgresql://` | 0 |
| JWT-shaped strings (`eyJ…`), `sb_secret`, `service_role_key`, `SUPABASE_SERVICE` | 0 |
| The four legacy credential sets (values not reproduced here) | 0 |
| `api_key = …` | 0 |

**Result: clean.** Additionally:

- The validation container uses **trust authentication** on a disposable database, so no password
  was needed even transiently.
- No environment variable, absolute path or machine-specific value appears in any migration. The
  migrations are plain SQL executed by `psql`; they contain no Supabase-specific syntax.
- Migrations are idempotent only in the sense that each runs once and in order; none of them drops
  or recreates an object that a later one depends on.

### Environment dependencies identified

| Dependency | Where | Impact |
|---|---|---|
| PostgreSQL **13+** | `gen_random_uuid()` used as a column default | Core since PG 13; Supabase runs 15+ |
| PostgreSQL **11+** | `GENERATED ALWAYS AS IDENTITY` | Satisfied |
| Membership in the table-owning role | Migrations run as the schema owner | Standard for Supabase migrations |
| `pg_cron`, `pgcrypto`, extensions | **not used** | No extension dependency at all |

---

## 9. Limitations and tests that could not be executed

Honest list, with the reason for each.

| # | Not executed | Reason | Mitigation |
|---|---|---|---|
| 1 | `supabase start`, `supabase db reset`, `supabase db diff` | **The Supabase CLI is not installed** in this environment. `config.toml` targets CLI **2.122.0** and follows its documented schema, but it has not been parsed by the CLI | Migrations are standard SQL and were applied by `psql` exactly as the CLI would apply them. The CLI must validate `config.toml` as the first step of the next task |
| 2 | Supabase Auth behaviour (token issuance, `sub` claim shape, sign-up disabled) | Requires a running Supabase stack | The schema reads `request.jwt.claims ->> 'sub'`; the API must set that claim from the validated token. Verified only that policies behave correctly for a given `sub` |
| 3 | Supabase Storage (bucket privacy, signed URL issuance) | Requires a running Supabase stack | The schema stores metadata only; bucket policy is a Supabase configuration task |
| 4 | PostgREST exposure | **Disabled deliberately** (`[api] enabled = false`) | Not a gap — the architecture forbids direct SPA access to PostgreSQL |
| 5 | Remote Supabase apply (`db push`) | **Prohibited without explicit approval** | — |
| 6 | Application-level tests (API endpoints, EF Core mapping) | No application code exists yet | Schema-level only, as scoped |
| 7 | Load/performance testing | No dataset exists (no legacy migration) | Index inventory is justified by access pattern, not by measurement. Flagged in `target-schema.md` §11 |
| 8 | `ALTER TYPE … ADD VALUE` in a transaction | Not exercised | Enum evolution is a migration task; PostgreSQL 17 permits it in a transaction, older versions do not |
| 9 | Role membership in a real Supabase deployment | Roles `anon`/`authenticated` are created conditionally because a plain instance lacks them | In Supabase they pre-exist and the `do` block is a no-op |
| 10 | Deleting a role that is assigned to users | Not exercised | `ON DELETE CASCADE` on `user_role` would remove assignments; the API should refuse instead |

### Known design limitations carried into the next task

1. **No `tenant_id`.** Single-tenant by approval decision A-04. Adding tenancy later requires a
   column on every table, a JWT claim, and a predicate in every one of the 160 policies.
2. **`user_account` has no FK to `auth.users`.** A Supabase Auth user could exist without an
   authorisation record. Such a user is inert (test R10) but provisioning must be asserted by an
   integration test.
3. **`user_profile.company_id` is not enforced anywhere.** Approval item A-37 is still open.
4. **RLS filters silently** (§5.1). The API must check affected row counts.
5. **Performance is unmeasured.** Every index is justified by an expected query; none is justified
   by a measurement, because there is no data to measure against.

---

## 10. Mandatory validation checklist

| # | Requirement | Status | Evidence |
|---|---|---|---|
| 1 | Build the database from scratch in a disposable local environment | **PASS** | §2 — 11 migrations on an empty `postgres:17-alpine` container |
| 2 | Apply all migrations in order | **PASS** | §2 — filename order, `ON_ERROR_STOP=1`, zero errors |
| 3 | Verify tables, keys, indexes, constraints, policies | **PASS** | §3 — 31 tables, 72 FKs, 65 CHECKs, 95 indexes, 160 policies, all measured |
| 4 | Run referential integrity tests | **PASS** | §4 — 32 assertions, including both accept and reject cases |
| 5 | Confirm the schema can be recreated reproducibly | **PASS** | §7 — two builds, byte-identical dumps after normalising `pg_dump`'s random token |
| 6 | Confirm no records were imported from MySQL | **PASS** | §6 — 0 business and identity rows; legacy-name scans return 0 |
| 7 | Verify no secrets or accidental environment dependencies | **PASS** | §8 — clean scan; no extension, path or credential dependency |
| 8 | Document limitations and tests that could not be executed | **PASS** | §9 — 10 items, each with a reason |

**Overall: PASSED.** 54 assertions executed (32 constraint + 22 RLS), 0 failures, plus 14 structural
checks and a reproducibility proof.
