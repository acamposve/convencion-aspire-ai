# Release readiness audit — Convenciones MVP

**Reviewer:** independent audit of `main` at `92dfa3d` (PR #7 merged).
**Method:** every claim below was produced by running a command against this commit. Nothing is
inferred from documentation, and nothing is asserted that was not executed.

---

## Verdict: **NOT RELEASABLE**

The primary workflow cannot be completed because it does not exist. At this commit the repository
contains a **working database schema** and a **working infrastructure skeleton** — and no
application: no authentication, no business endpoints, no document upload, no processing, no
frontend, no CI.

That is not a defect in what was built; the approved scope so far was governance, audits, the
database and one infrastructure task. It is a statement about readiness: **0 of the 9 steps of the
required vertical slice are implemented.**

Per the release criteria in the brief — a security defect, a tenant-isolation failure, a migration
that cannot be applied from scratch, or a primary workflow that cannot be completed — **the primary
workflow criterion fails outright.**

---

## 1. What exists, measured

| Artefact | Count on `main` |
|---|---|
| C# source files | 14 |
| C# projects | 6 |
| SQL migrations | 11 |
| TypeScript / React files | **0** |
| `package.json` | **0** |
| CI workflow files | **0** |
| Business HTTP endpoints | **0** |
| EF Core `DbSet` declarations | **0** |
| `IEntityTypeConfiguration` implementations | **0** |

The only HTTP surface is `/health/live` and `/health/ready`.

---

## 2. Checks executed, with results

### 2.1 Full solution compilation — **PASS**

```
dotnet clean Convenciones.slnx
dotnet build Convenciones.slnx
→ Compilación correcta.  0 Advertencia(s).  0 Errores.  exit 0
```

### 2.2 Unit and integration tests — **PASS (9 of 9)**

```
dotnet test Convenciones.slnx --no-build
→ Convenciones.UnitTests          3 passed, 0 failed
→ Convenciones.IntegrationTests   6 passed, 0 failed
```

Scope of what those 9 tests actually prove:

| Test | Proves |
|---|---|
| 3 × migration file ordering/naming | The 11 files are ordered, timestamped and uniquely prefixed |
| 2 × health endpoints | A real API pipeline answers `/health/live` and `/health/ready` against a real database |
| 4 × schema | 31 tables exist, RLS is enabled on every one, 22 capabilities / 4 roles / 0 users are seeded |

**They do not test any business behaviour, because there is none.** Coverage of the MVP workflow is
0%.

### 2.3 PostgreSQL rebuilt from scratch with versioned migrations — **PASS**

```
docker run … postgres:17-alpine          (empty database)
dotnet run --project src/Convenciones.Migrator
→ Applied 11 migration(s) and the reference seed.  exit 0
```

Verified state afterwards:

```
31 tables, 160 policies, 22 capabilities, 4 roles, 0 users
```

Each migration runs in its own transaction; a failure rolls back and exits non-zero.

### 2.4 Database-level constraint suite — **PASS (32 assertions)**

The Phase 4 suite re-run against the freshly built database:

```
PASS=32  FAIL=0
```

Covers: date-range validity, publication consistency, duplicate clause numbers, mandatory clause
classification, geography composite FKs, same-case composite FKs, single labour statute, document
content-type and size limits, audit immutability, and delete rules (`RESTRICT` vs `CASCADE`).

### 2.5 Database-level RLS and denied-access suite — **PASS (22 assertions)**

```
PASS=22  FAIL=0
```

Covers, and these are the only tenant/authorization controls that exist:

| Assertion | Result |
|---|---|
| Bootstrap window opens once and is inert afterwards | PASS |
| `anon` sees only published agreements, never drafts | PASS |
| `anon` has no privilege on the `platform` schema | PASS |
| Blocked user sees nothing on every request | PASS |
| Expired user sees nothing on every request | PASS |
| Valid token with **no provisioned profile** sees nothing | PASS |
| `authenticated` role is SELECT-only; direct writes refused | PASS |
| Capability-gated writes refused without the capability | PASS |
| Publication requires `agreement.publish` (database guard) | PASS |
| Promotion requires `negotiation.promote` | PASS |
| Audit trail invisible without `audit.read` | PASS |

**These prove the schema's security model works. They do not prove the application uses it**, because
the application does not exist yet.

### 2.6 Entity / EF Core / schema consistency — **NOT VERIFIABLE (vacuous)**

```
DbSet properties declared       : 0
IEntityTypeConfiguration impls  : 0
domain modules                  : 0
```

Two of the three things to compare do not exist. `ConvencionesDbContext` is an empty shell with a
default schema. The check cannot pass or fail — it has no subject. Reported as a finding (§4, H1),
not as a pass.

### 2.7 Authentication and authorization of sensitive operations — **NOT APPLICABLE**

There are no sensitive operations. No `MapGet`/`MapPost`/`MapPut`/`MapDelete`/`MapGroup` exists
anywhere in `src/`. No JWT validation, no Supabase Auth integration, no profile resolution, no
capability filter — the architecture documents describe all of it and none of it is code.

### 2.8 Tenant isolation — **FAILS (not implemented)**

- `platform.document` has **no `company_id`**; no table carries a tenant or owner column beyond
  `user_profile.company_id`, which nothing enforces.
- No application code applies a company predicate anywhere.
- The RLS policies that exist scope by **role and account state**, not by **company**. Two active
  users from different companies read the same rows.
- The proposal that would fix this (`docs/proposals/document-workflow-schema.md` §B.1) is unapproved
  and unimplemented.

This is a failing release criterion in the sense that the requirement is unaddressed, not that a
control was defeated — there is no data path to defeat yet.

### 2.9 Uploaded file validation — **NOT IMPLEMENTED**

No upload endpoint. The database constrains `content_type` to a three-value allow-list and
`size_bytes ≤ 20 MiB`, and the migration comments require byte-sniffing rather than trusting the
declared type — but **no code performs a sniff, computes a checksum, or issues a signed URL.**

### 2.10 Errors, duplicates and processing failures — **PARTIAL**

Present: an RFC 9457 `IExceptionHandler` that logs once, returns `code` + `correlationId`, and never
leaks exception detail; fail-fast startup on missing configuration; readiness returning 503 when the
schema is absent.

Absent: any duplicate handling (no business endpoints), any retry logic, and any processing status
transitions. `platform.document.extraction_status` exists and is never written.

### 2.11 Secrets in the repository — **FAILS**

| Scope | Result |
|---|---|
| New stack (`src`, `tests`, `supabase`, `docs`, `tools`, scripts) | **CLEAN** — one match, a documentation line describing the scan pattern, not a credential |
| **Legacy tree (committed)** | **17 hardcoded `mysql_connect` sites with literal credentials**, including what appear to be production accounts |

The new application references none of them, and the repository is private. But the criterion is
"absence of secrets in the repository", and it is not met. First raised in Phase 1 §S1; still true.

### 2.12 No MySQL data imported — **PASS**

```
app.company=0  app.agreement=0  app.agreement_clause=0  app.negotiation_case=0
app.statute=0  app.news_article=0  platform.user_account=0
legacy table names present: 0
```

Reference data is re-authored by hand (22 capabilities, 4 roles, 5 heading groups, 1 country).

### 2.13 Reproducible Aspire configuration — **FAILS**

Re-verified at this commit, sixth run:

```
containers provisioned : postgres-txkaesnx, pgadmin-turkbfhn     ← start correctly
processes running      : Convenciones.AppHost                    ← only the host
api / migrator         : never start
```

The application model is correct (`--publisher manifest` lists both as `project.v0`), the containers
reach `Running`, but DCP logs the project resources as `Starting -> Waiting` and its watch stream
fails with `http2: response body closed`. Attempts made: `AspireUseCliBundle=true`, and clearing seven
stale `dcp` processes. Reproduced on six runs; environmental rather than a model defect.

**Consequence:** "one command brings up the MVP" is **not true**. The documented fallback (run the
migrator and API directly) is verified and works.

### 2.14 CI build and test execution — **FAILS (absent)**

No `.github/workflows` directory. Constitution Art. VII.3 requires CI as the merge gate; nothing runs
on a pull request today. The tests pass **because a human ran them**, which is exactly the condition
the constitution was written to prevent.

### 2.15 Documentation sufficiency — **PARTIAL**

23 documents, ~7,000 lines: constitution, architecture, database, legacy analysis, operations.
Strength: the schema can be rebuilt and understood by a newcomer from written instructions.
Gap: `docs/operations/local-development.md` honestly documents the Aspire failure, but there is
**no demonstration script** for the MVP workflow because the workflow does not exist. The
`mvp-scope.md` acceptance criteria (47 of them) describe a system that is not built; a reader could
mistake them for implemented behaviour.

---

## 3. Operational features (what actually runs today)

| Feature | State |
|---|---|
| API process with structured logging and OTLP tracing | Works |
| `/health/live`, `/health/ready` (readiness requires reachable DB **and** `app` schema) | Works, tested |
| Consistent error contract (RFC 9457, no detail leakage) | Works, not exercised by business code |
| Schema + reference data provisioning as a separate, non-privileged step | Works, tested |
| PostgreSQL 17 with 31 tables, 72 FKs, 65 CHECKs, 160 RLS policies, append-only audit | Works, tested |
| Authentication, authorization, tenancy, upload, processing, UI, CI | **Do not exist** |

---

## 4. Findings

### Blocking

| # | Finding | Evidence |
|---|---|---|
| **B1** | **No authentication.** No Supabase Auth integration, no JWT validation, no profile resolution, no capability filter | 0 endpoint declarations in `src/` |
| **B2** | **No business endpoints.** The 9-step vertical slice is 0% implemented; the primary workflow cannot be completed | 0 `Map*` business routes |
| **B3** | **Tenant isolation unaddressed.** `platform.document` has no `company_id`; RLS scopes by role, not company | schema inspection; proposal §B.1 unapproved |
| **B4** | **No upload, validation, storage or processing.** `extraction_status` never written | no code path |
| **B5** | **No frontend.** No TypeScript, no `package.json` | inventory |
| **B6** | **No CI.** Nothing runs on a pull request | no `.github/workflows` |
| **B7** | **17 hardcoded credentials committed in the legacy tree** | `mysql_connect` scan |
| **B8** | **Aspire does not start the stack** in this environment; one-command run is not reproducible | 6 runs, DCP `http2` failure |

### High priority

| # | Finding |
|---|---|
| **H1** | EF Core has zero entity mappings — the domain/EF/schema consistency requirement has no subject. The first domain module must land mappings *and* a test that they match the SQL schema |
| **H2** | Optimistic concurrency (`xmin`/`If-Match`) and the audit interceptor exist in the schema design but not in code; `audit_entry` is never written by the application |
| **H3** | No request size limits, rate limiting, or CORS policy configured on the API |
| **H4** | `mvp-scope.md` lists 47 acceptance criteria with nothing marking them unimplemented — a release-communication risk |
| **H5** | No production readiness at all: no deployment target, no secrets-manager configuration, no backup/restore plan, no rollback strategy. Migrations have never been applied to any real environment |

### Minor

| # | Finding | Action |
|---|---|---|
| **M1** | `Directory.Build.props` claimed Domain purity was "enforced by the project file". Nothing enforces it — no Domain projects, no architecture tests | **FIXED in this PR** |
| **M2** | `InvariantGlobalization=true` may affect Spanish casing/culture operations. Sorting is done by PostgreSQL, so risk is low, but it is a global switch with no test covering it | Document or reconsider when the first culture-sensitive formatting lands |
| **M3** | `MigrationFileTests` hard-codes the 11 filenames, so every new migration requires editing the test — an invitation to careless edits | Acceptable as a schema-version guard; note the cost |
| **M4** | Readiness returns 503 including which schema is missing; fine for probes, should be network-restricted in production | Add to production checklist |
| **M5** | No `.editorconfig`; `TreatWarningsAsErrors` is global but the AppHost opts out (reason documented) | Low value; leave |

---

## 5. Residual risks

1. **The audits are more mature than the software.** ~7,000 lines of design and analysis against 14
   source files. The risk is that the documentation is read as evidence of a system. It is not.
2. **The database is complete but unexercised by an application.** Its constraints have never
   rejected a real request, only a synthetic one.
3. **RLS has never been tested through the API.** It is tested as SQL with simulated claims. Once
   real tokens arrive, the gap between "claims the API sets" and "claims the policies expect" is the
   most likely place for an authorization hole.
4. **The legacy tree is a live credential store in the repository.** Rotation is still outstanding
   from Phase 1.
5. **Aspire's failure is unresolved.** If it is not fixed before the demo, the demo depends on a
   manual two-command fallback, which is a poor story for a reviewer.
6. **No CI means no regression barrier.** Everything verified here was verified by hand, and will
   stay verified only until someone breaks it.

---

## 6. How to reproduce what was verified

```powershell
# 1. Build and test
dotnet build Convenciones.slnx
dotnet test  Convenciones.slnx --no-build        # expect 9/9

# 2. Rebuild the database from scratch
docker run --rm -d --name convenciones-dev `
  -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=convenciones `
  -p 55432:5432 postgres:17-alpine
$env:ConnectionStrings__convenciones = 'Host=localhost;Port=55432;Database=convenciones;Username=postgres'
$env:MIGRATIONS_PATH = (Resolve-Path ./supabase/migrations).Path
dotnet run --project src/Convenciones.Migrator    # expect: Applied 11 migration(s)…

# 3. Re-run the database security suites
docker cp supabase convenciones-dev:/sb
docker exec convenciones-dev psql -U postgres -d convenciones -f /sb/tests/constraint-tests.sql   # 32 PASS
docker exec convenciones-dev psql -U postgres -d convenciones -f /sb/tests/rls-tests.sql          # 22 PASS

# 4. Run the API and probe it
dotnet run --project src/Convenciones.Api
curl.exe -k https://localhost:<port>/health/live      # 200
curl.exe -k https://localhost:<port>/health/ready     # 200, or 503 if the schema is missing
```

---

## 7. Demonstration available today

An honest demo can show: the schema rebuilding from scratch, its constraints rejecting bad data, RLS
denying anonymous and blocked callers, and the API reporting healthy/unhealthy readiness. **It cannot
show a user signing in, uploading a contract, extracting text, or reading the result**, because none
of that is implemented.

---

## 8. Tasks required before production

Ordered; the first four are the MVP itself.

1. **Authentication** — Supabase Auth integration, JWT validation, profile resolution, capability
   enforcement on every endpoint, with a route-enumeration test proving 401/403 on all of them.
2. **Tenant isolation** — approve and apply proposal §B.1 (`company_id` + scoping policy), answer
   A-37, and add an integration test proving a user of company A cannot read or write company B.
3. **Documents** — upload with byte-sniffed content type, size limits, server-generated storage keys,
   private bucket, signed downloads; apply proposals §B.2/§B.3.
4. **Processing** — extraction for the confirmed formats, status transitions, controlled retries,
   failure recording, and text retrieval; plus the React/Vite/MUI screens for the workflow.
5. **CI** — a GitHub Actions workflow building and testing on every PR, then branch protection or the
   local hook to make it a gate (Art. VII.3, VIII.1).
6. **Remove the legacy tree** from the repository and **rotate the 17 exposed credentials**.
7. **Resolve Aspire** or document the fallback as the official run path.
8. **Production readiness** — deployment target, secrets management, backup/restore, migration
   rollback plan, health-check network restriction, and a first controlled migration against
   Supabase **with explicit approval**.

---

## 9. Fixes applied in this audit

| # | Change | Verification |
|---|---|---|
| **M1** | `Directory.Build.props` comment corrected: it claimed Domain purity was enforced by the build file when nothing enforces it | `dotnet build` → 0 warnings, 0 errors; `dotnet test` → 9/9 passed, re-run after the change |

No other change was made. Every other finding is outside the scope approved so far, and fixing them
during an audit would mean shipping unverified work under the guise of review.
