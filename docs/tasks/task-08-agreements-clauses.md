# T8 — Agreements and clauses API

**Status: implemented and verified — PR #21.** Branch `task/21-agreements-clauses`. Executed results are
in [§9](#9-implementation-record).

## 1. Objective

The core of the product: author a collective agreement, its clauses and annexes, classify every
clause, and publish the agreement as a recorded act — scoped to the author's company.

## 2. Scope

**In:** `Convenciones.Agreements` aggregate; agreement create/read/list/update; composed title (Q-1);
identity `(company_id, valid_from)`; integer clause numbering (Q-2), mandatory heading (Q-3/A-16),
numeric ordering, clause create/read/update/delete/reclassify; annex metadata; `agreement.publish`
gate, publish/unpublish with actor and timestamp (Q-7); headings taxonomy read; per-company scoping in
the API and in RLS.

**Out:** comparison workspace (T9), negotiation (T11), audit read endpoint (T13), search (R2-02),
clause alignment (R2-01), rich-text sanitisation (see §11), agreement deletion (the contract has no
delete operation; publication state is the lifecycle).

## 3. Endpoints

| Method | Path | Capability | Notes |
|---|---|---|---|
| `GET` | `/agreements` | `agreement.read` | Paged; `companyId`, `state`, `q`; composed title and clause count in each summary |
| `POST` | `/agreements` | `agreement.write` | `companyId` defaults to the session's company; creating into another company is **403** |
| `GET` | `/agreements/{id}` | `agreement.read` | Details with clauses and annexes |
| `PATCH` | `/agreements/{id}` | `agreement.write` | Requires `If-Match`; changing `validFrom` into an existing identity is **409** |
| `POST` | `/agreements/{id}/publish` | `agreement.publish` | ≥ 1 clause required; records actor and timestamp |
| `POST` | `/agreements/{id}/unpublish` | `agreement.publish` | Clears both; refused on a draft |
| `GET` `POST` | `/agreements/{id}/clauses` | `.read` / `.write` | Numeric order on read; duplicate number **409** |
| `GET` `PATCH` `DELETE` | `/agreements/{id}/clauses/{clauseId}` | `.read` / `.write` | PATCH/DELETE require `If-Match` |
| `PUT` | `/agreements/{id}/clauses/{clauseId}/heading` | `agreement.write` | Reclassification only; `If-Match` |
| `GET` `POST` | `/agreements/{id}/annexes` | `.read` / `.write` | Metadata; duplicate name (case-insensitive) **409** |
| `PATCH` `DELETE` | `/agreements/{id}/annexes/{annexId}` | `agreement.write` | `If-Match` |
| `GET` | `/comparison/headings` | `comparison.read` | Read-only taxonomy; `groupIds`, `includeExcluded` |

**Another company's agreement, clause or annex is 404**, never 403: a 403 confirms existence. Creating
one for another company is 403 because there is no existing row to hide.

## 4. Domain rules

### 4.1 Identity and title (Q-1)

- Identity is `(CompanyId, ValidFrom)`. The API pre-checks the aggregate and the database's
  `agreement_company_valid_from_key` is the second line; both produce **409**.
- The title is **composed on read** from the company's current name and the validity period:
  `"Empresa · 2026–2028"`, or `"Empresa · desde 2026"` when there is no end date. There is no
  `title` or `number` column; renaming the company changes the title immediately.
- `validTo` cannot precede `validFrom`; `scope` is required.

### 4.2 Clauses (Q-2, Q-3/A-16)

- Numbers are positive **integers**. Uniqueness is `(agreementId, number)`, enforced by the aggregate
  and by `agreement_clause_number_key`; ordering is numeric, not lexical.
- Every clause requires an existing, active `ComparisonHeading`. A missing, unknown or inactive
  heading is **400**; the database column is `NOT NULL`.
- An exclusion note is allowed only when `isExcludedFromCompilation` is true (the same rule as the
  database CHECK).
- Clause bodies are stored as provided; see §11 for the sanitisation gap.

### 4.3 Publication (Q-7)

- Only `agreement.publish` may change the state. `Publish` requires at least one clause and is
  refused on an already-published agreement; `Unpublish` is refused on a draft. Both failures are
  **409 `invalid_state`**.
- Publishing sets `publishedAt` and `publishedBy` from the session; unpublishing clears both. The
  database CHECK enforces that the pair is all-or-nothing.
- Each transition changes only the agreement, so the audit interceptor writes exactly one
  `audit_entry`; the test asserts the count and the changed fields.
- **The database enforces the capability too.** `app.guard_agreement_publication()` refuses a state
  change by a caller without `agreement.publish`, even via direct SQL. The test connects as a
  transcriptor (write but not publish) and proves the trigger refuses.

### 4.4 Tenancy

The API loads through a company check on every endpoint. T8 also closes the database gap: migration
`20260104000100_agreement_tenant_rls.sql` scopes agreement, clause and annex policies by
`platform.can_access_company()`, in addition to the capability. A raw query as company B sees zero
rows and zero clauses, asserted in the test.

## 5. Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Agreements/Agreement.cs` | Aggregate: header, publication, identity, composed title |
| `src/Convenciones.Agreements/AgreementClause.cs` | Clause entity and its rules |
| `src/Convenciones.Agreements/AgreementAnnex.cs` | Annex metadata |
| `src/Convenciones.Agreements/ComparisonHeading.cs` | Read-only taxonomy entities |
| `src/Convenciones.Agreements/AgreementConfiguration.cs` | EF mappings, xmin concurrency, relationships |
| `src/Convenciones.Agreements/AgreementRuleException.cs` | Validation/conflict/invalid-state rule failure |
| `src/Convenciones.Api/Endpoints/AgreementEndpoints.cs` | Agreements, clauses, annexes, publish/unpublish |
| `src/Convenciones.Api/Endpoints/ComparisonHeadingEndpoints.cs` | Taxonomy read |
| `src/Convenciones.Api/Infrastructure/DatabaseSessionInterceptor.cs` | Propagates claims and `app_api` role to the business connection |
| `supabase/migrations/20260104000000_audit_writer_read_policy.sql` | Lets the writer read back its own audit row |
| `supabase/migrations/20260104000100_agreement_tenant_rls.sql` | Company-scoped agreement RLS |
| `tests/…/AgreementFixture.cs`, `AgreementTests.cs` | 12 integration tests |

## 6. Acceptance criteria

1. Two agreements for one company with the same `validFrom` are **409**, and the database constraint
   independently refuses the duplicate — the test inserts directly and asserts `23505` on
   `agreement_company_valid_from_key`.
2. Clause numbers are integers; a duplicate is **409**; ordering is numeric (`2` before `10`); zero is
   **400**.
3. A clause without a heading — absent, unknown, inactive or `Guid.Empty` — is **400
   `validation_failed`**, and no row is created.
4. Publishing requires `agreement.publish`: a transcriptor gets **403**, the row stays Draft, and the
   audit count does not change. A direct SQL update by the same caller is refused by the database.
5. Publishing records actor and timestamp, writes exactly **one** audit entry, and unpublishing clears
   both fields and writes one more. An empty or already-published agreement is **409 `invalid_state`**.
6. The composed title contains the company name and the period and is **not stored**; renaming the
   company changes it, and `information_schema` has no `title`/`number` columns.
7. A user of company A cannot read or write company B's agreement: every endpoint is 404, creating
   into B is 403, the row and clauses are unchanged, the list excludes it, and RLS hides it from a raw
   query as B.

Additional covered behaviour: list scoping/pagination/filtering, header `If-Match` (missing 400, stale
409, current 200) and identity collision 409, clause update/reclassify/delete round trip, annex
metadata with case-insensitive duplicate names, and the heading taxonomy's excluded-heading filter.

## 7. Test matrix

`Convenciones.IntegrationTests.AgreementTests`, 12 tests against a real PostgreSQL container:

| # | Test | Key assertions |
|---|---|---|
| 1 | `Duplicate_company_and_start_date_are_refused_by_the_api_and_the_database` | 409 + code; direct insert 23505 on the named constraint; one row |
| 2 | `Clause_numbers_are_integers_ordered_numerically_and_unique_within_the_agreement` | order `[2, 10]`; duplicate 409; zero 400; two rows |
| 3 | `A_clause_without_a_heading_is_refused` | missing/unknown heading 400; no rows; valid heading 201 |
| 4 | `Publishing_without_the_capability_is_forbidden_in_the_api_and_at_the_database` | 403; state/audit unchanged; trigger 42501 mentioning `agreement.publish` |
| 5 | `Publishing_records_the_actor_and_timestamp_and_unpublishing_clears_them` | actor/time in DB; +1 audit each; empty publish 409 `invalid_state` |
| 6 | `The_composed_title_follows_the_company_name_and_is_not_stored` | title contents; rename reflected; no title/number columns |
| 7 | `Another_company_cannot_read_or_write_the_agreement` | 404 on every route; create 403; unchanged; list and raw RLS hidden |
| 8 | `Header_updates_require_the_current_row_version_and_respect_identity` | 400 missing, 409 stale, 200 current, 409 collision, no partial write |
| 9 | `Clause_update_reclassify_and_delete_round_trip` | update, reclassify, delete; versions advance; row gone |
| 10 | `Annex_metadata_round_trips_and_names_are_unique_ignoring_case` | 201, duplicate 409, update, delete, empty list |
| 11 | `List_is_company_scoped_paged_and_filterable_and_reports_the_composed_title` | totals vs SQL; no company B rows; pages disjoint; state/q filters |
| 12 | `Heading_taxonomy_read_hides_excluded_headings_by_default` | excluded heading hidden by default, included on request; invalid group id 400 |

## 8. Risks

- **Clause body sanitisation is not implemented** (§11). The body is stored as text. Until T14 renders
  it, there is no HTML execution path; T14 must either sanitise on write or render as text. This is a
  blocking prerequisite, not a resolved item.
- **`If-Match` on every child mutation** makes the client carry one version per clause/annex. That is
  the contract, but T14 must implement it carefully; the tests cover the failure modes.
- **Composed titles change when a company is renamed.** That is the point of Q-1, but reports that
  captured a title string are snapshots, not stable identifiers; use the agreement id.
- **The claims/RLS changes** are cross-cutting (see §9). They make the database enforce the caller,
  but also mean every business connection now runs as `app_api`; a missing grant would surface as a
  policy failure. The whole existing suite was rerun after the change.

## 9. Implementation record

### Cross-cutting prerequisites found while making T8 executable

1. **The business connection never carried claims.** The session resolver set
   `request.jwt.claims` on its own short-lived transaction, but the `DbContext` connection did not, so
   every RLS policy and every capability trigger saw an anonymous session. Publishing would have been
   refused for everyone. `DatabaseSessionInterceptor` now writes the caller's claims, switches to
   `app_api`, and sanitises both on every connection open (empty claims + `reset role` when there is
   no session).
2. **The audit writer could not read back its own row.** EF's `INSERT ... RETURNING id, occurred_at`
   applies SELECT policies, and only `audit.read` holders could read `audit_entry`; an editor's
   upload was rejected with `42501` on `audit_entry`. Migration `20260104000000` narrows the read
   policy to `audit.read OR actor_id = current_user_id()`.
3. **Agreements had capability-only RLS.** Migration `20260104000100` adds the company predicate to
   agreement, clause and annex read/write policies.

### Verified by execution

```
dotnet build Convenciones.slnx -> Compilación correcta. 0 advertencias, 0 errores
dotnet test  Convenciones.slnx -> UnitTests         10/10
                               -> IntegrationTests  86/86   (12 new AgreementTests)
```

Total automated tests: **96**, 0 failed. The 12 agreement tests run against a disposable PostgreSQL 17
container with the real migrations and seed.

## 10. Left undone

- Rich-text sanitisation for clause bodies (§11) — blocking for T14.
- `GET /agreements/{id}/audit` (T13); the audit rows are already written.
- Clause-body content formatting and the authoring UI (T14).
- Comparison (T9), negotiation (T11), search (R2-02), alignment (R2-01).
- Agreement deletion is not a contract operation; the lifecycle is Draft/Published.

## 11. The sanitisation gap, stated plainly

The schema comment says clause `body` is "sanitised rich text" on write. **T8 does not sanitise it.**
No HTML sanitiser was added, and a hand-rolled one would be worse than none. The API stores the body
as supplied; the current SPA (T7) has no clause viewer, so nothing renders it as HTML today. Before
T14 renders clause bodies, it must either add an allow-list sanitiser on write (the intended design)
or render the body strictly as text. This record exists so that a future reviewer does not mistake
the schema comment for an implemented control.
