# T10 — Legal library API

**Status:** implemented and verified; independent-review findings resolved — PR #23, branch
`task/23-legal-library`. Executed results are in [§9](#9-implementation-record).

## 1. Objective

The statutes and articles that comparison reads, and the single labour statute the schema enforces.

## 2. Scope

**In:** statute CRUD with kind; article CRUD with integer numbering and a mandatory heading; a partial
update that cannot silently erase a field; the single labour statute invariant surfaced as a clear 409
naming the index that refuses it; article text retrieval; the statute articles the comparison workspace
returns for a heading, behind the capability that governs them.

**Out:** the 2,672 unclassified legacy articles' worklist (R2-05); AI classification (FUT-02); the
statute editor UI (T14); any legacy statute content — the library still ships empty (Art. V.2, Q-6).

## 3. Decisions

| Decision | Outcome |
|---|---|
| **"Heading" in AC 3** | An article has two different headings and only one is mandatory. `title` — the article's own heading ("Artículo 12 — Vacaciones") — is **required**, enforced by the aggregate and by `statute_article_title_not_blank`. The **comparison classification** (`heading_id`) stays **optional**, which is approval item A-12 and the reason 89.6% of the legacy corpus could be loaded at all. Recorded in the decision log as **[ADR-023](../architecture/decisions.md)**, not only here. |
| **Who refuses the second LabourLaw** | Only the partial unique index. The handler does **not** pre-check: a pre-check races, and AC 1 asks for proof that the index refuses. The API attempts the insert and translates the index's refusal into a 409 whose detail names `statute_single_labour_law_key`. |
| **Tenancy** | **None.** Statutes carry no `company_id` and no `TenantScope` call appears in the legal endpoints or queries. Any active caller of any company — and a caller with no company at all — reads the same library. Asserted over HTTP, in SQL and by a column assertion. |
| **`kind` is immutable** | `PATCH /statutes/{id}` cannot change it. Turning an `OtherStatute` into the labour statute would have to obey the single-labour-law rule; that is a separate operation if it is ever needed. |
| **`PATCH` is a partial update** | A field that is not sent is **unchanged** (the company endpoint's rule); a nullable field sent as **`null` is cleared**; a required field sent as `null` is **400** and nothing changes. Expressed by `PatchField<T>`, which distinguishes "not sent" from "sent as null" — the distinction a plain nullable property cannot carry. The audit entry records only the fields that actually changed. |
| **Comparison capability** | `GET /comparison` returns statutory text, so it requires **`comparison.read` *and* `statute.read`** (ADR-024). The other comparison routes keep `comparison.read` alone. No seeded role changes behaviour, because every role holding `comparison.read` also holds `statute.read`; the rule is enforced for the roles that will exist later. |
| **CQRS shape** | One handler **type** per use case on both sides (Art. IV.4, ADR-022): five commands and five queries, each owning its request and response record. Queries receive their `IQueryable` explicitly and cannot write. |
| **Persistence port** | The module owns its commands but must not see the API's `DbContext` (Art. IV.6). It declares `IStatuteStore`; `StatuteStore` in the API implements it. |

## 4. Endpoints

| Method | Path | Capability | Purpose |
|---|---|---|---|
| `GET` | `/statutes` | `statute.read` | Paged; `kind` (`LabourLaw`/`OtherStatute`), `q` on the name |
| `POST` | `/statutes` | `statute.write` | 201 + `Location` |
| `GET` | `/statutes/{id}` | `statute.read` | Detail with articles in numeric order |
| `PATCH` | `/statutes/{id}` | `statute.write` | Partial: name, date, source, document; requires `If-Match` |
| `GET` | `/statutes/{id}/articles` | `statute.read` | All articles, numeric order |
| `POST` | `/statutes/{id}/articles` | `statute.write` | 201 + `Location` |
| `GET` | `/statutes/{id}/articles/{articleId}` | `statute.read` | Article text |
| `PATCH` | `/statutes/{id}/articles/{articleId}` | `statute.write` | Partial: number, title, summary, body, heading, note; requires `If-Match` |
| `DELETE` | `/statutes/{id}/articles/{articleId}` | `statute.write` | Requires `If-Match` |

No endpoint deletes a statute: a statute owns its articles by cascade and deleting the labour statute
would be a destructive library operation with no stated requirement. Recorded as left undone.

### 4.1 Response shapes

```jsonc
// GET /statutes?kind=OtherStatute&page=1&pageSize=25
{
  "items": [
    {
      "id": "…", "kind": "OtherStatute", "name": "Ley Orgánica de Precios Justos",
      "publishedOn": "2014-01-30", "source": "Gaceta Oficial 40.340",
      "documentId": null, "articleCount": 12, "rowVersion": 1234
    }
  ],
  "page": 1, "pageSize": 25, "total": 3
}
```

```jsonc
// POST /statutes/{id}/articles
{
  "number": 12,                    // integer only (Q-2)
  "title": "Artículo 12 — Vacaciones",   // required: the article's own heading
  "summary": "…",
  "body": "…",
  "headingId": null,               // optional classification (A-12)
  "economicComparisonNote": null
}
// 201 → { "id": "…", "number": 12, "title": "…", "body": "…",
//         "headingId": null, "economicComparisonNote": null, "rowVersion": 1234 }
```

```jsonc
// PATCH /statutes/{id}          If-Match: "1234"
{ "name": "Ley Renombrada" }                       // publication date and source are untouched
{ "publishedOn": null, "source": null }            // explicit nulls clear the nullable fields
{ "name": null }                                    // 400: a required field cannot be cleared
```

`headingName` is deliberately absent from the article response: resolving it would couple the legal
module to the comparison taxonomy for a display convenience, and `GET /comparison/headings` already
serves the names. `headingId` is the fact.

### 4.2 Failures, and what produces them

| Situation | Response | Produced by |
|---|---|---|
| Second `LabourLaw` statute | **409 `conflict`**, detail names `statute_single_labour_law_key` | the partial unique index |
| Duplicate statute name, case-insensitive, on create or rename | **409 `conflict`**, detail names `statute_name_key` | the unique index |
| Duplicate article number in a statute | **409 `conflict`**, detail names `statute_article_number_key` | the aggregate *and* the unique index |
| Article number 0 or negative | **400 `validation_failed`** | the aggregate, then the CHECK constraint |
| Article with a blank heading or body, or `name` blank | **400 `validation_failed`** | the aggregate, then the CHECK constraints |
| Required field sent as `null` in a `PATCH` | **400 `validation_failed`**, nothing changed | `StatutePatch` + the aggregate |
| Unknown/inactive `headingId`; empty Guid | **400 `validation_failed`** | the handler, against `comparison_heading` |
| Unknown `kind` in body or filter, including the string `"1"` | **400 `validation_failed`** | explicit parsing — `Enum.TryParse` alone would accept a number |
| Body that cannot be bound (a number where a string is expected, invalid JSON, a missing required parameter) | **400 `malformed_request`** as `application/problem+json`, with `code` and `errors` | the routing binder throws; `GlobalExceptionHandler` reports it |
| Wrong `Content-Type` for a JSON endpoint | **415 `unsupported_media_type`** as `application/problem+json`, status unchanged | the framework refuses before the endpoint; the status-code-pages middleware adds the body |
| Missing `If-Match` | **400 `validation_failed`** | the endpoint |
| Stale `If-Match` | **409 `concurrent_modification`** | the handler, then `xmin` |
| Unknown or invisible statute/article | **404 `not_found`** | the endpoint |
| Caller without `statute.write` | **403 `forbidden`**, detail names the capability | the identity middleware, then RLS |
| `GET /comparison` without `statute.read` | **403 `forbidden`**, detail names `statute.read` | the endpoint declaration |

## 5. Implementation

| File | Purpose |
|---|---|
| `src/Convenciones.Legal/Statute.cs` | Aggregate root: kind, name, provenance, articles; the single-labour-law rule documented where it lives (the index) |
| `src/Convenciones.Legal/StatuteArticle.cs` | Child entity: integer number, mandatory heading, optional classification (A-12) |
| `src/Convenciones.Legal/StatuteRuleException.cs` | Validation / conflict / state, translated by the endpoint |
| `src/Convenciones.Legal/StatuteConfiguration.cs` | Column-by-column mapping, `xmin` as the concurrency token |
| `src/Convenciones.Legal/PatchField.cs` | "not sent" vs "sent as null": the type and its JSON converter |
| `src/Convenciones.Legal/StatutePatch.cs` | Resolves a partial update against the current state, in one testable place |
| `src/Convenciones.Legal/StatuteContracts.cs` | Request/response records, the partial-update request types and the strict `kind` parser |
| `src/Convenciones.Legal/StatuteCommands.cs` | Five command handlers, one per use case, plus `StatuteCommandResult` |
| `src/Convenciones.Legal/StatuteQueryHandlers.cs` | Five query handlers, query/response records and the paging bounds |
| `src/Convenciones.Legal/IStatuteStore.cs` | The persistence port the module is allowed to use |
| `src/Convenciones.Legal/StatuteResponses.cs` | One mapper, so every route answers with the same shape |
| `src/Convenciones.Api/Infrastructure/StatuteStore.cs` | The port's only implementation, over the shared context |
| `src/Convenciones.Api/Endpoints/StatuteEndpoints.cs` | Nine routes, `If-Match`, and the three unique-index translations |
| `src/Convenciones.Api/Infrastructure/GlobalExceptionHandler.cs` | Reports an unreadable request as `malformed_request` instead of an empty 400 |
| `src/Convenciones.Api/Program.cs` | `statute_kind` enum mapping, handler registration, `ThrowOnBadRequest`, endpoint mapping |
| `src/Convenciones.Api/Endpoints/ComparisonEndpoints.cs` | Fills `statuteArticles` (bounded, with its total) behind `statute.read` |
| `tests/…/StatuteFixture.cs` | Own container: two companies, five users (one `comparison.read` without `statute.read`), heading taxonomy, data helpers |
| `tests/…/StatuteTests.cs` | 20 acceptance tests |
| `tests/…/StatuteAggregateTests.cs` | 13 domain invariant tests |
| `tests/…/StatutePatchTests.cs` | 5 partial-update and JSON round-trip tests |
| `supabase/tests/constraint-tests.sql` | E5–E10: blank heading, blank name, number 0, unclassified accepted, duplicate name, blank body |
| `supabase/tests/rls-tests.sql` | R29–R33 the library's policies; R34–R35 agreement tenancy in the other direction |

### 5.1 Two handler types per use case

Art. IV.4 requires commands and queries as distinct handler types that never share a model. Both sides
are classes: `CreateStatuteHandler` … `DeleteStatuteArticleHandler` load the aggregate through
`IStatuteStore`; `ListStatutesHandler` … `GetStatuteArticlesForHeadingHandler` take their `IQueryable`
explicitly and project with `AsNoTracking`, so a query has no way to write. ADR-022 records the rule and
notes that the earlier static query helpers in Agreements and Comparison are the remaining exception.

### 5.2 The three unique indexes are the guard, and the 409 says so

Naming the index in the detail is deliberate. "Duplicate" tells a caller nothing it can act on;
`statute_single_labour_law_key` tells it which rule refused the write, and for a rule that spans rows
the index *is* the rule.

### 5.3 Why "not sent" needed a type

The first implementation treated `PATCH` as a full replacement, so a rename silently erased
`publishedOn` and `source` — the audit trail recorded the loss (`{"Source":{"from":…,"to":null}}`). A
plain `string?` cannot tell "leave it alone" from "clear it", so `PatchField<T>` carries `IsSet` and a
JSON converter sets it whenever the property is present, including as `null`. `JsonIgnoreCondition
.WhenWritingDefault` keeps unset fields out of the body, so the same record is easy to use from a test
and a client.

## 6. Acceptance criteria

1. **A second `LabourLaw` statute is refused with 409 naming the constraint, and the partial unique
   index is what refused it.** The API returns 409 whose detail contains
   `statute_single_labour_law_key`; a direct SQL insert bypassing the handler fails with `23505` and
   `ConstraintName = statute_single_labour_law_key`; the row count stays 1; a second `OtherStatute` is
   accepted. ✔
2. **Article numbers are integers, unique per statute, ordered numerically.** The list returns
   `[1, 2, 10]`, the wire values are JSON numbers, a duplicate in the same statute is **409** and the
   unique index refuses it at the database, the same number in a different statute is **201**, and `0`
   is **400**. ✔
3. **An article without its heading is refused.** Title absent, blank and whitespace-only are all
   **400**; the database CHECK refuses the same row when the API is bypassed; a blank body is **400**.
   An article with **no comparison classification** is accepted, which is A-12 (ADR-023). ✔
4. **Statutes are company-independent.** A company-B editor reads a statute created by company A, in
   the list, the detail and the articles; a user with **no company** reads the same list; there is no
   `company_id` column on either table; and the RLS read policy returns the whole library to a
   company-B caller. ✔
5. **Writes require `statute.write`.** A negotiator (read only) gets **403** with a detail naming the
   capability on create, update, article create and article delete, and the rows are unchanged; at the
   database the same caller's insert is refused with `42501` while an editor's succeeds. ✔
6. **`PATCH` is partial (review).** A rename preserves `publishedOn` and `source`, and the audit entry
   does not mention them; an explicit `null` clears a nullable field; a required field sent as `null`
   is **400**; an article patch changes only the fields sent. ✔
7. **The comparison requires `statute.read` (review).** A caller with `comparison.read` only gets
   **403** naming `statute.read` from the projection, while candidates still answer **200**. ✔

## 7. Test matrix

`StatuteTests` — 20 integration tests against the real PostgreSQL container:

| # | Test | Asserts |
|---|---|---|
| 1 | `A_second_labour_law_is_refused_by_the_api_and_by_the_partial_unique_index` | 409 naming the index; SQL `23505` + constraint name; count stays 1; `OtherStatute` unrestricted |
| 2 | `Article_numbers_are_integers_unique_per_statute_and_ordered_numerically` | `[1,2,10]` on the wire; duplicate **409**; `0` **400**; other statute **201**; index refuses at SQL |
| 3 | `An_article_without_its_heading_is_refused` | missing/blank title **400**; blank body **400**; SQL `23514` + `statute_article_title_not_blank`; unclassified accepted; unknown heading **400**; empty Guid **400** |
| 4 | `Statutes_are_readable_by_a_user_of_any_company_and_carry_no_company_predicate` | company B reads everything; no-company user too; zero `company_id` columns; RLS count equals the table count |
| 5 | `Writes_require_statute_write_in_the_api_and_at_the_database` | four **403**s naming the capability; rows unchanged; RLS `42501` for the negotiator, allowed for the editor |
| 6 | `Statute_updates_require_the_current_row_version_and_unique_names` | missing **400**; stale **409**; current **200** with a new `rowVersion`; rename collision **409** naming `statute_name_key` |
| 7 | `Article_text_round_trips_and_can_be_removed` | body retrieved; partial patch changes only what is sent; stale delete **409**; delete **204**; then **404** |
| 8 | `Statute_and_article_writes_are_audited` | `legal_library` module, `statute`/`statute_article` entity types, `created`/`updated`/`deleted` actions |
| 9 | `The_list_is_paged_and_filterable_by_kind_and_name` | kind filter, `q` search, page size, **400** for an invalid kind |
| 10 | `The_comparison_returns_statute_articles_under_the_heading_but_never_for_an_excluded_one` | one article, integer number, body; other heading and unclassified absent from the JSON; excluded heading empty |
| 11 | `A_referenced_document_must_exist` | unknown `documentId` **400** and nothing created |
| 12 | `A_partial_update_keeps_omitted_fields_and_an_explicit_null_clears_a_nullable_one` | rename preserves date and source; audit omits them; explicit nulls clear; `name: null` **400** |
| 13 | `A_partial_article_update_changes_only_the_fields_it_sends` | title changes alone; `headingId: null` clears; `number: null` and `title: null` are **400** |
| 14 | `Creating_a_statute_with_a_name_that_already_exists_is_refused` | **409** naming `statute_name_key`, case-insensitively |
| 15 | `The_comparison_bounds_statute_articles_at_100_and_reports_the_real_total` | 105 classified articles → 100 returned, total 105, numbers 1–100 |
| 16 | `The_comparison_requires_statute_read_because_it_returns_statutory_text` | **403** naming `statute.read`; candidates **200**; a caller with both **200** |
| 17 | `A_document_must_be_live_and_visible_to_the_caller` | live document attached; soft-deleted **400**; another company's **400** with the same detail |
| 18 | `An_article_is_only_addressable_through_its_own_statute` | foreign article id **404**; own statute **200** |
| 19 | `A_body_that_is_not_valid_json_for_the_contract_is_reported_as_a_problem` | **400**, `application/problem+json`, `code = malformed_request` |
| 20 | `A_wrong_content_type_keeps_its_status_and_gains_a_problem_body` | **415** unchanged, `application/problem+json`, `code = unsupported_media_type` |

`StatuteAggregateTests` — 13 unit tests, no server and no container: name required; kind fixed at
creation; an undefined kind refused; blank optionals become null; number must be positive; heading and
body required; unclassified allowed and an empty `Guid` refused; numbers unique within a statute and
not across statutes; renumbering; removal; numeric projection order; the `kind` parser rejecting `"1"`.

`StatutePatchTests` — 5 unit tests: `{"source": null}` deserialises as *set*; unset fields are omitted
when serialising; a set null is written as `null`; merging keeps omitted values and applies explicit
nulls; a required field sent as null survives the merge so the command can refuse it.

Database suite: constraint tests E5–E10 (blank heading, blank name, number 0, unclassified accepted,
duplicate name, blank body), RLS R29–R35 (library reads by any company and by a user with no company,
`statute.write` required, agreement tenancy in both directions).

## 8. Risks

- **A company predicate applied by reflex.** Statutes are global. One `TenantScope.CanAccess` in the
  statute endpoints would make the library invisible to every user with a company — and to none
  without, which would look like "the library is empty". Tests 4 and 17 assert the opposite, and the
  SQL suite asserts the same.
- **The partial-update rule is subtle and load-bearing.** A serializer change that turned "omitted"
  into "null" would start deleting publication dates again, and the API would still answer 200. The
  unit tests pin the JSON round trip, the integration test pins the HTTP behaviour and the audit entry.
- **The single-labour-law rule has one guard, on purpose.** No pre-check means the refusal is the
  database's. If a future migration renames the index, the message degrades to the generic 409 that
  still names whatever refused the write.
- **The comparison bounding is per request, not per statute.** No cap exists on the number of articles
  under one heading inside the array beyond 100; `statuteArticleTotal` makes truncation visible, and a
  caller that needs more must narrow the heading.
- **Statutory text has no publication concept.** Agreements are published and filtered; statutes are
  not, so an entry is visible to `statute.read` holders as soon as it is stored. Inventing a
  publication workflow for the library would be a business rule nobody stated (Q-6).
- **`GET /statutes/{id}/articles` is unbounded.** The labour statute carries 556 articles in the legacy
  corpus; the response is one document and the T14 editor will need paging if the library grows.

## 9. Implementation record

### Verified by execution

```
dotnet build Convenciones.slnx -> Compilación correcta. 0 advertencias, 0 errores
dotnet test  Convenciones.slnx -> UnitTests         28/28   (13 aggregate + 5 patch + 10 existing)
                               -> IntegrationTests 113/113   (20 new StatuteTests)
pnpm --dir src/Convenciones.Web run test -> Test Files 8 passed (8), Tests 44 passed (44)
supabase/tests/run-validation.ps1 ->
    constraint-tests  PASS=41 FAIL=0   (6 new statute assertions: E5–E10)
    rls-tests         PASS=37 FAIL=0   (7 statute/reference assertions: R29–R33, agreement tenancy R34–R35)
    reproducibility   two independent builds produced an identical schema
    VALIDATION PASSED
```

Total repository tests at this commit: **141 .NET + 44 web = 185**, 0 failed.

### Review remediation — finding to resolution

| Review finding | Resolution | Evidence |
|---|---|---|
| **M1** `PATCH` cleared omitted fields (proved: `{"Source":{"from":"Gaceta Oficial 40.340","to":null}}`) | `PatchField<T>` + `StatutePatch`: omitted = unchanged, explicit `null` = clear, required `null` = 400; the "partial update" wording is now accurate | tests 12–13, `StatutePatchTests` (5), `StatuteAggregateTests` unchanged |
| **M2** the SQL suite could not detect an agreement tenant regression (R7 passed under a tenant-blind policy) | **R34** asserts a company-B editor sees 0 agreements and 0 clauses; **R35** asserts the cross-company update affects 0 rows; no policy was weakened | `rls-tests.sql` R34–R35; mutation below |
| **M3** `/comparison` served statutory text under `comparison.read` alone | both capabilities required on the projection (`comparison.read` + `statute.read`), recorded as ADR-024 and in api-contracts §4.4 | test 16 with a transient `comparison_only` role |
| **L1** non-string `kind` produced a bare 400 | binding failures throw (`ThrowOnBadRequest`) and `GlobalExceptionHandler` reports `malformed_request`; a wrong content type (415) also gains a problem body, with its status unchanged (`RequestFailureProblem` + status-code-pages for the refusals the framework writes itself) | tests 19 and 20; verified by probe: `POST /statutes` as `text/plain` → **415** `unsupported_media_type`, as `{}`-invalid JSON → **400** `malformed_request`, `POST /agreements` with a numeric date → **400** `malformed_request`, `GET /comparison` without `headingId` → **400** `malformed_request`; `401`/`404`/`405` still have no body and the same status |
| **L2** `StatuteStore` said the document check was "not company-scoped" | comment corrected: the predicate names no company but the query runs as `app_api`, so the document policies scope it; another company's document is indistinguishable from a missing one | test 17 (live → 201; deleted → 400; foreign → 400 with an identical detail) |
| **L3** `statuteArticleTotal` was documented as "the real total" but reports 0 for an excluded heading | the contract now defines it as the number in scope for the projection (0 when excluded), with `total > length` meaning truncated | api-contracts §4.4; tests 10 and 15 |
| **L4** the article list is unbounded | unchanged behaviour, now recorded as a risk (§8) rather than omitted | — |
| **L5** missing negative tests | duplicate name on create (14), truncation (15), foreign/deleted document (17), cross-statute article (18), empty Guid classification (3), malformed body (19) | the table in §7 |
| **L6** T10 mapped *any* unique violation to 409 | unchanged: the only unique indexes on these tables are the three named ones plus the primary key, and an unknown refusal still names the index it came from | reviewed; noted as acceptable |
| **L7** queries were static methods, not handler types | five query handler types (ADR-022 annotation); the static class is gone | §5.1 |
| **L8** stray blank line in `Program.cs` | removed with the registration block | — |

### The SQL assertions are real — mutation checks

The review's central objection to R7 was that it passes whether or not the tenant predicate exists.
The same method was applied to the new assertions, in the disposable container, each mutation applied
and the suite re-run:

| Mutation | Result |
|---|---|
| `agreement_api_read` replaced with a capability-only policy (tenant predicate removed) | **FAIL: R34 a company B editor saw 2 agreement(s)** — while **R7 still passes**, confirming R7 alone cannot see this |
| `agreement_publication_guard` trigger dropped | **FAIL: R15 a writer without agreement.publish published an agreement** — while **R34 passes**, confirming the two assertions test different controls |

So R15 tests the publication guard and R34 tests the tenant predicate; neither is vacuous, and R7 is
honest about what it does (capability-based visibility of drafts).

### The pre-existing database-validation failure, found and fixed

The first run of `run-validation.ps1` on this branch reported `RLS and authorisation tests PASS=33
FAIL=2`:

```
FAIL: R7 active negotiator saw 0 agreements, expected 2
FAIL: R15 a writer without agreement.publish published an agreement
```

Neither failure was a product defect. T8's migration `20260104000100_agreement_tenant_rls.sql` made
agreements company-scoped, but `rls-tests.sql` created its `user_profile` rows in the *documents*
section near the end of the file, so R7 and R15 ran for users with no company. The fix is in the
fixture: the company-A memberships are created with the fixtures, and the writer gets one before the
read-visibility block. No policy changed; the re-run reports `PASS=37 FAIL=0`.

## 10. Left undone

- **No statute deletion.** The contract lists none, and cascading a 556-article labour statute away has
  no stated requirement.
- **A body above the 22 MiB transport limit is not answered consistently (pre-existing, outside this
  task).** Probed on the running API: a 23 MB JSON body produced either a connection reset or **500
  `internal_error`**, never the contract's **413 `payload_too_large`**. The handler only narrowed which
  exceptions get a specific response, so this behaviour is unchanged from before the remediation; the
  handler *does* map a 413 if the framework reports one. The document upload's 20 MiB business limit is
  separate, endpoint-produced and tested at **413**. Needs its own task (transport limits, T18/T19).
- **No paging for `GET /statutes/{id}/articles`.** One response per statute; the T14 editor will need
  paging if the library grows past a few hundred articles.
- **`GET /statutes` has no sort parameter.** Ordering is fixed (kind, then name) and documented; the
  contract's allow-listed sorting is not needed until a screen asks for it.
- **The worklist of unclassified articles (R2-05)** and **AI classification (FUT-02)** stay out.
- **The statute editor UI (T14).** The API exposes `headingId` without `headingName`; the screen will
  join the taxonomy it already fetches for the comparison.
