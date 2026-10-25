# T10 — Legal library API

**Status:** implemented and verified — PR #23. Branch `task/23-legal-library`. Executed results are in
[§9](#9-implementation-record).

## 1. Objective

The statutes and articles that comparison reads, and the single labour statute the schema enforces.

## 2. Scope

**In:** statute CRUD with kind; article CRUD with integer numbering and a mandatory heading; the
single labour statute invariant surfaced as a clear 409 naming the index that refuses it; article text
retrieval; the statute articles the comparison workspace returns for a heading.

**Out:** the 2,672 unclassified legacy articles' worklist (R2-05); AI classification (FUT-02); the
statute editor UI (T14); any legacy statute content — the library still ships empty (Art. V.2, Q-6).

## 3. Decisions taken before starting

| Decision | Outcome |
|---|---|
| **"Heading" in AC 3** | The mandatory field is `statute_article.title` — the article's own heading ("Artículo 12 — Vacaciones"). The **comparison classification** (`heading_id`) stays **nullable**, which is approval item A-12 and the reason 89.6% of the legacy corpus could be loaded at all. AC 3's parenthetical describes why classification was the legacy problem; requiring it here would reverse an approved decision. Tested both ways: a blank title is **400** and the database CHECK refuses it; an article with no classification is accepted. |
| **Who refuses the second LabourLaw** | Only the partial unique index. The handler does **not** pre-check: a pre-check races, and AC 1 asks for proof that the index refuses. The API therefore attempts the insert and translates the index's refusal into a 409 whose detail names `statute_single_labour_law_key`. |
| **Tenancy** | **None.** Statutes carry no `company_id` and no `TenantScope` call appears in the legal endpoints or queries. Any active caller of any company — and a caller with no company at all — reads the same library. Asserted over HTTP, in SQL and by a column assertion. |
| **`kind` is immutable** | `PATCH /statutes/{id}` cannot change it. Turning an `OtherStatute` into the labour statute would have to obey the single-labour-law rule; that is a separate operation if it is ever needed, not a side effect of a rename. |
| **Persistence port** | The module owns its commands (Art. IV.4) but must not see the API's `DbContext` (Art. IV.6). It declares `IStatuteStore`; `StatuteStore` in the API implements it. Six members, one implementation, no second caller invented. |
| **Comparison integration** | `GET /comparison` now fills `statuteArticles`, which T9 left empty on purpose. The array is bounded at 100 articles per heading and the response carries `statuteArticleTotal`, so truncation is visible rather than silent. `ComparisonStatuteArticleView.ArticleNumber` (a string) became `Number` (an integer) — Q-2 is integer numbering and the field had never carried a value. |

## 4. Endpoints

| Method | Path | Capability | Purpose |
|---|---|---|---|
| `GET` | `/statutes` | `statute.read` | Paged; `kind` (`LabourLaw`/`OtherStatute`), `q` on the name |
| `POST` | `/statutes` | `statute.write` | 201 + `Location` |
| `GET` | `/statutes/{id}` | `statute.read` | Detail with articles in numeric order |
| `PATCH` | `/statutes/{id}` | `statute.write` | Name, date, source, document; requires `If-Match` |
| `GET` | `/statutes/{id}/articles` | `statute.read` | All articles, numeric order |
| `POST` | `/statutes/{id}/articles` | `statute.write` | 201 + `Location` |
| `GET` | `/statutes/{id}/articles/{articleId}` | `statute.read` | Article text |
| `PATCH` | `/statutes/{id}/articles/{articleId}` | `statute.write` | Full replacement; requires `If-Match` |
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

`headingName` is deliberately absent from the article response: resolving it would couple the legal
module to the comparison taxonomy for a display convenience, and `GET /comparison/headings` already
serves the names. `headingId` is the fact.

### 4.2 Failures, and what produces them

| Situation | Response | Produced by |
|---|---|---|
| Second `LabourLaw` statute | **409 `conflict`**, detail names `statute_single_labour_law_key` | the partial unique index |
| Duplicate statute name (case-insensitive) | **409 `conflict`**, detail names `statute_name_key` | the unique index |
| Duplicate article number in a statute | **409 `conflict`**, detail names `statute_article_number_key` | the aggregate *and* the unique index |
| Article number 0 or negative | **400 `validation_failed`** | the aggregate, then the CHECK constraint |
| Article with a blank heading or body | **400 `validation_failed`** | the aggregate, then the CHECK constraint |
| Unknown/inactive `headingId` | **400 `validation_failed`** | the handler, against `comparison_heading` |
| Unknown `kind` in body or filter, including `"1"` | **400 `validation_failed`** | explicit parsing — `Enum.TryParse` alone would accept a number |
| Missing `If-Match` | **400 `validation_failed`** | the endpoint |
| Stale `If-Match` | **409 `concurrent_modification`** | the handler, then `xmin` |
| Unknown or invisible statute/article | **404 `not_found`** | the endpoint |
| Caller without `statute.write` | **403 `forbidden`**, detail names the capability | the identity middleware, then RLS |

## 5. Implementation

| File | Purpose |
|---|---|
| `src/Convenciones.Legal/Statute.cs` | Aggregate root: kind, name, provenance, articles; the single-labour-law rule documented where it lives (the index) |
| `src/Convenciones.Legal/StatuteArticle.cs` | Child entity: integer number, mandatory heading, optional classification (A-12) |
| `src/Convenciones.Legal/StatuteRuleException.cs` | Validation / conflict / state, translated by the endpoint |
| `src/Convenciones.Legal/StatuteConfiguration.cs` | Column-by-column mapping, `xmin` as the concurrency token |
| `src/Convenciones.Legal/StatuteContracts.cs` | Request/response records and the strict `kind` parser |
| `src/Convenciones.Legal/StatuteCommands.cs` | Five command handlers, one per use case, plus `StatuteCommandResult` |
| `src/Convenciones.Legal/IStatuteStore.cs` | The persistence port the module is allowed to use |
| `src/Convenciones.Legal/StatuteQueries.cs` | Read side: list, detail, articles, article, comparison projection |
| `src/Convenciones.Legal/StatuteResponses.cs` | One mapper, so every route answers with the same shape |
| `src/Convenciones.Api/Infrastructure/StatuteStore.cs` | The port's only implementation, over the shared context |
| `src/Convenciones.Api/Endpoints/StatuteEndpoints.cs` | Nine routes, `If-Match`, and the three unique-index translations |
| `src/Convenciones.Api/Program.cs` | `statute_kind` enum mapping, handler and store registration, endpoint mapping |
| `src/Convenciones.Api/Endpoints/ComparisonEndpoints.cs` | Fills `statuteArticles` (bounded, with the real total) |
| `tests/…/StatuteFixture.cs` | Own container: two companies, four users, heading taxonomy |
| `tests/…/StatuteTests.cs` | 11 acceptance tests |
| `tests/…/StatuteAggregateTests.cs` | 13 domain invariant tests |
| `supabase/tests/constraint-tests.sql` | E5–E8: blank heading, blank name, number 0, unclassified article accepted |
| `supabase/tests/rls-tests.sql` | R29–R33: any company reads; `statute.write` is required to write |

### 5.1 Why the command handlers are separate types

Constitution Art. IV.4 requires commands and queries as distinct handlers that never share a model.
The five commands are classes; the query side is a static class with one method per use case, the shape
T9 used for `ComparisonQueries`, because the queries hold no state and a class per projection would be
ceremony. Reads project straight from `IQueryable` and every one of them is `AsNoTracking`.

### 5.2 The three unique indexes are the guard, and the 409 says so

Naming the index in the detail is deliberate. "Duplicate" tells a caller nothing it can act on;
`statute_single_labour_law_key` tells it which rule refused the write, and for a rule that spans rows
the index *is* the rule.

## 6. Acceptance criteria

1. **A second `LabourLaw` statute is refused with 409 naming the constraint, and the partial unique
   index is what refused it.** The API returns 409 whose detail contains
   `statute_single_labour_law_key`; a direct SQL insert bypassing the handler fails with `23505` and
   `ConstraintName = statute_single_labour_law_key`; the row count stays 1; a second `OtherStatute` is
   accepted. ✔
2. **Article numbers are integers, unique per statute, ordered numerically.** The list returns
   `[1, 2, 10]`, the wire values are JSON numbers, a duplicate in the same statute is **409** and the
   unique index refuses it at the database, the same number in a different statute is **201**, and
   `0` is **400**. ✔
3. **An article without its heading is refused.** Title absent, blank and whitespace-only are all
   **400**; the database CHECK `statute_article_title_not_blank` refuses the same row when the API is
   bypassed; a blank body is **400**. An article with **no comparison classification** is accepted,
   which is A-12. ✔
4. **Statutes are company-independent.** A company-B editor reads a statute created by company A, in
   the list, the detail and the articles; a user with **no company** reads the same list; there is no
   `company_id` column on either table; and the RLS read policy returns the whole library to a
   company-B caller. The deliberate difference from agreements is stated in code and tested. ✔
5. **Writes require `statute.write`.** A negotiator (read only) gets **403** with a detail naming the
   capability on create, update, article create and article delete, and the rows are unchanged; at the
   database the same caller's insert is refused with `42501` while an editor's succeeds. ✔

## 7. Test matrix

`StatuteTests` — 11 integration tests against the real PostgreSQL container:

| # | Test | Asserts |
|---|---|---|
| 1 | `A_second_labour_law_is_refused_by_the_api_and_by_the_partial_unique_index` | 409 naming the index; SQL `23505` + constraint name; count stays 1; `OtherStatute` unrestricted |
| 2 | `Article_numbers_are_integers_unique_per_statute_and_ordered_numerically` | `[1,2,10]` on the wire; duplicate **409**; `0` **400**; other statute **201**; index refuses at SQL |
| 3 | `An_article_without_its_heading_is_refused` | missing/blank title **400**; blank body **400**; SQL `23514` + `statute_article_title_not_blank`; unclassified accepted; unknown heading **400** |
| 4 | `Statutes_are_readable_by_a_user_of_any_company_and_carry_no_company_predicate` | company B reads everything; no-company user too; zero `company_id` columns; RLS count equals the table count |
| 5 | `Writes_require_statute_write_in_the_api_and_at_the_database` | four **403**s naming the capability; rows unchanged; RLS `42501` for the negotiator, allowed for the editor |
| 6 | `Statute_updates_require_the_current_row_version_and_unique_names` | missing **400**; stale **409**; current **200** with a new `rowVersion`; rename collision **409** naming `statute_name_key` |
| 7 | `Article_text_round_trips_and_can_be_removed` | body retrieved; full replacement clears the classification; stale delete **409**; delete **204**; then **404** |
| 8 | `Statute_and_article_writes_are_audited` | `legal_library` module, `statute`/`statute_article` entity types, `created`/`updated`/`deleted` actions |
| 9 | `The_list_is_paged_and_filterable_by_kind_and_name` | kind filter, `q` search, page size, **400** for an invalid kind |
| 10 | `The_comparison_returns_statute_articles_under_the_heading_but_never_for_an_excluded_one` | one article, integer number, body; other heading and unclassified absent from the JSON; excluded heading empty |
| 11 | `A_referenced_document_must_exist` | unknown `documentId` **400** and nothing created |

`StatuteAggregateTests` — 13 unit tests, no server and no container: name required; kind fixed at
creation; an undefined kind refused; blank optionals become null; number must be positive; heading and
body required; unclassified allowed and an empty `Guid` refused; numbers unique within a statute and
not across statutes; renumbering; removal; numeric projection order; the `kind` parser rejecting `"1"`.

## 8. Risks

- **A company predicate applied by reflex.** Statutes are global. One `TenantScope.CanAccess` in the
  statute endpoints would make the library invisible to every user with a company — and to none
  without, which would look like "the library is empty". Test 4 asserts a company-B user and a
  no-company user both read it, and the SQL suite asserts the same.
- **The single-labour-law rule has one guard, on purpose.** No pre-check means the refusal is the
  database's. That is correct and testable, but it also means the API's error message depends on
  mapping `PostgresException.ConstraintName`; if a future migration renames the index, the message
  degrades to the generic 409 that still names whatever refused it.
- **The comparison includes statute articles with no publication step.** Agreements are published and
  filtered; statutes are not, so a draft-quality article is visible to `comparison.read` holders
  immediately. No publication concept exists for the library (Q-6), and inventing one here would be a
  business rule nobody stated.
- **The comparison page is per heading, and the library is empty in the MVP.** The bounded array (100)
  and `statuteArticleTotal` make truncation visible; a heading with more than 100 classified articles
  reports the real total while returning 100.
- **The `title` column is the article's heading, not a classification.** Two different "headings" in
  one entity is a naming hazard; the response documents carry `title` (mandatory) and `headingId`
  (optional), and §3 records why.

## 9. Implementation record

### Verified by execution

```
dotnet build Convenciones.slnx -> Compilación correcta. 0 advertencias, 0 errores
dotnet test  Convenciones.slnx -> UnitTests         23/23   (13 new StatuteAggregateTests)
                               -> IntegrationTests 104/104  (11 new StatuteTests)
pnpm --dir src/Convenciones.Web run test -> Test Files 8 passed (8), Tests 44 passed (44)
supabase/tests/run-validation.ps1 ->
    constraint-tests  PASS=39 FAIL=0        (4 new statute assertions: E5–E8)
    rls-tests         PASS=35 FAIL=0        (5 new statute assertions: R29–R33)
    reproducibility   two independent builds produced an identical schema
    VALIDATION PASSED
```

Total repository tests at this commit: **127 .NET + 44 web = 171**, 0 failed.

### A pre-existing database-validation failure, found and fixed

The first run of `run-validation.ps1` on this branch reported `RLS and authorisation tests PASS=33
FAIL=2`:

```
FAIL: R7 active negotiator saw 0 agreements, expected 2
FAIL: R15 a writer without agreement.publish published an agreement
```

Neither failure is a product defect, and neither was introduced by T10. T8's migration
`20260104000100_agreement_tenant_rls.sql` made agreements company-scoped, but `rls-tests.sql` created
its `user_profile` rows in the *documents* section near the end of the file. R7 and R15 therefore ran
for users with no company, and the tenant policy filtered the agreements out before the assertions —
or, for R15, before the publication guard trigger could refuse the update. The validation script on
`main` is red for that reason.

The fix is in the fixture, not in the product: the company-A memberships for users 002, 003 and 007 are
created with the fixtures at the top of the transaction, and the writer gets a membership before the
`Read visibility` section. The re-run reports `PASS=35 FAIL=0`, and the two assertions now pass for the
reason they were written for.

## 10. Left undone

- **No statute deletion.** The contract lists none, and cascading a 556-article labour statute away has
  no stated requirement. Residual docs: none.
- **The comparison's statute side is not paged per statute.** The bound is per request; a statute with
  more than 100 classified articles under one heading is truncated and the total reports it.
- **The worklist of unclassified articles (R2-05)** and **AI classification (FUT-02)** stay out.
- **The statute editor UI (T14).** The API exposes `headingId` without `headingName`; the screen will
  join the taxonomy it already fetches for the comparison.
