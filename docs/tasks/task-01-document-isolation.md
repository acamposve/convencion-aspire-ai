# T1 — Document tenant isolation, processing state and the Task 6 decisions

**Status: implemented and verified. Awaiting review (PR #9).**
Branch `task/9-document-isolation` · base `main` @ `ca8a034`.

---

## 1. Objective

Resolve the blocking security findings before any document endpoint exists:

1. Give `platform.document` an owning company and a tenant predicate that actually denies.
2. Make processing state auditable enough for controlled retries.
3. Apply the four outstanding Task 6 decisions the merged schema still contradicted.

## 2. Scope

**In:** two forward migrations, the seed's role set, the constraint and RLS test suites, and the
schema documentation.

**Out (deliberately):**

- Any application code, endpoint, authentication or upload. That is T2 and T4.
- Editing the eleven merged migrations. Forward-only (constitution Art. VIII.6).
- Supabase Storage. It needs a Supabase project, which requires your approval.
- Full-text search, OCR, AI classification.

## 3. Files touched

| File | Change |
|---|---|
| `supabase/migrations/20260102000000_align_product_decisions.sql` | **New** — Q-1, Q-2, Q-4 |
| `supabase/migrations/20260102000100_document_processing.sql` | **New** — B.1, B.2, B.3, tenant policies |
| `supabase/seed.sql` | Five roles (Q-9), `company.read_all` capability, grants |
| `supabase/tests/constraint-tests.sql` | Suffix tests replaced with integer-only proofs; `Promoted` state removed |
| `supabase/tests/rls-tests.sql` | 8 cross-tenant assertions; `analyst` → `negotiator` |
| `tests/…/SchemaTests.cs` | 32 tables, 5 roles, 23 capabilities, isolation objects |
| `tests/…/MigrationFileTests.cs` | 13 migrations in the expected order |
| `docs/database/target-schema.md` | §12 records the delta |
| `.gitignore` | Ignores the per-developer `launchSettings.json` |

## 4. Risks identified before starting

| Risk | Outcome |
|---|---|
| The tenant predicate is vacuous — passes tests while filtering nothing | **Materialised.** Caught by the new tests; see §5.2 |
| Dropping merged columns breaks existing test fixtures | Materialised (agreement `title`, `number_suffix`, `Promoted`) — found by running the suites, all fixed |
| Enum swap invalidates CHECK constraints that name the old values | Handled: constraints dropped and rebuilt around the type change |
| Non-ASCII characters corrupted by PowerShell round-trips | **Materialised.** Repaired; see §5.3 |

## 5. Checks executed

### 5.1 Migrations applied to an empty database

```
docker run postgres:17-alpine (empty)
dotnet run --project src/Convenciones.Migrator
→ Applied 13 migration(s) and the reference seed.   exit 0
```

Measured afterwards: **32 tables, 163 RLS policies, 23 capabilities, 5 roles, 0 users**.

### 5.2 The tenant rule was broken, and the tests proved it

First run of the cross-tenant assertions — 23 passed, **7 failed**:

```
FAIL: R21 company A editor saw 2 documents, expected 1
FAIL: R22 company A editor saw 2 document_text rows, expected 1
FAIL: R23 company B editor saw 2 documents, expected 1
FAIL: R25 company A editor modified 1 row(s) of company B document
FAIL: R26 company A editor deleted 1 row(s) of company B document
FAIL: R27 company A editor created a company B document
FAIL: R28 a user with no company saw 2 documents
```

**Root cause:** the approved design said `company.read` unlocks cross-company access, but that
capability already means *read the company catalogue* and the seed grants it to **all five roles**.
The predicate was therefore always true.

Confirmed by measurement before fixing:

```
administrator | 1
editor        | 1
negotiator    | 1
superadmin    | 1
transcriptor  | 1
```

**Fix:** a distinct capability, `company.read_all`, granted only to SuperAdmin and Admin, and the
migration header now explains why. **Correction confirmed by the Product Owner.**

### 5.3 Encoding corruption found and repaired

`Get-Content`/`Set-Content` round-trips read UTF-8 as ANSI and re-wrote it, corrupting em dashes and
accents, and converting a BOM into a literal `?` that broke the migrator at position 1
(`42601: syntax error at or near "?"`). Repaired by reversing the double-encoding plus two targeted
replacements. Every touched file now verified as valid UTF-8, no BOM, zero replacement characters.

### 5.4 Final state — all suites green

```
constraint suite : PASS=32  FAIL=0
RLS suite        : PASS=30  FAIL=0     (was 22; +8 cross-tenant)
dotnet build     : Compilación correcta. 0 Advertencias, 0 Errores
dotnet test      : UnitTests 3/3 · IntegrationTests 7/7   → 10/10
```

The eight isolation assertions, all passing:

```
R21 company A editor sees exactly 1 document (its own)
R22 company A editor reads text only of its own document
R23 company B editor sees exactly 1 document (its own)
R24 company.read_all holder sees both company documents and NOT the unattached one
R25 company A editor updating company B document affects 0 rows
R26 company A editor deleting company B document affects 0 rows
R27 creating a document for another company is refused by RLS
R28 a user with no company sees 0 documents
```

### 5.5 Environment incident

Mid-task a Windows installer began rewriting the .NET installation (`host\fxr` missing,
`msiexec`/`setup` running, `dotnet --version` failing). I did not kill an MSI in flight. The build and
test results in §5.4 were produced **after** the SDK was repaired to 10.0.402, on this exact tree —
so no claim in this record rests on an unverified build.

## 6. Status

**Done and verified.** PR #9 open, mergeable.

## 7. Left undone

- Everything that needs an endpoint: authentication, company resolution, upload, processing,
  retrieval. All of T2–T6.
- Registering any Supabase project or bucket.
- Documenting `company.read_all` in `docs/architecture/security-model.md` — the capability matrix
  there still lists the four-role model and `analyst`. It should be updated with T2, when the API
  actually enforces it.
