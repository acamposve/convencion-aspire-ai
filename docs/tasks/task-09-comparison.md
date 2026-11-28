# T9 — Comparison API

**Status: implemented and verified — PR #22.** Branch `task/22-comparison`. Executed results are in
[§8](#8-implementation-record).

## 1. Objective

Compare the clauses of a company's published agreements under one heading, respecting the "No Compara"
exclusion and **never implying an alignment the data does not have** (Q-6). This is the product's read
surface.

## 2. Scope

**In:** heading-group listing; candidate agreements for a heading (published, company-scoped, paged);
the comparison projection by heading across selected agreements; numeric clause order; exclusion of
`is_excluded` headings; published-only output with `unpublishedRequested`; the economic comparison note
as free text; paging and hard bounds; `AsNoTracking` and GET-only routes.

**Out:** clause alignment/pairing (R2-01), export (R2-04), full-text search (R2-02), statute articles
(T10 fills the field), the comparison UI (T14).

## 3. Endpoints

| Method | Path | Capability | Purpose |
|---|---|---|---|
| `GET` | `/comparison/heading-groups` | `comparison.read` | Active blocks, ordered by `sortOrder`, with `requiresEconomicComparison` |
| `GET` | `/comparison/headings` | `comparison.read` | T8's taxonomy read; `groupIds`, `includeExcluded` |
| `GET` | `/comparison/candidates` | `comparison.read` | Published agreements of a company with ≥ 1 clause under the heading; `headingId`, `companyId?`, `page`, `pageSize` |
| `GET` | `/comparison` | `comparison.read` | The adjacency projection for one heading and up to 50 selected agreements; `headingId`, `agreementIds`, `page`, `pageSize` |

`headingId` is required and validated by one shared check on both `/comparison` and
`/comparison/candidates`: absent, malformed (not a GUID), unknown or inactive answers **400
`validation_failed`**, the same rule the clause endpoints use. Tenancy keeps **404 `not_found`**.
The value is a heading id: `/comparison/headings` returns groups with nested headings, and the web
client flattens them before sending one (#135).

All routes are **GET-only**. `POST`/`PUT` on `/comparison` return **405**, and the module exposes no
write method (AC 4).

**Changed by T10 (review):** the projection now returns statutory text, so it requires
**`comparison.read` *and* `statute.read`**; the other three routes keep `comparison.read` alone
(ADR-024, `api-contracts.md` §4.4). The change is recorded here because it alters this table, and it is
asserted by a T10 integration test with a `comparison.read`-only role.

### 3.1 Response shapes

```jsonc
// GET /comparison/candidates?headingId=…&companyId=…&page=1&pageSize=10
{
  "items": [
    {
      "agreementId": "…",
      "companyId": "…",
      "companyName": "Empresa",
      "title": "Empresa · 2026–2028",   // composed on read (Q-1)
      "validFrom": "2026-01-01",
      "validTo": "2028-12-31",
      "clauseCount": 3                  // clauses under this heading only
    }
  ],
  "page": 1,
  "pageSize": 10,
  "total": 4
}
```

```jsonc
// GET /comparison?headingId=…&agreementIds=id1,id2&page=1&pageSize=5
{
  "heading": {
    "id": "…", "groupId": "…", "groupName": "GENERALES", "name": "Salarios",
    "isExcluded": false, "requiresEconomicComparison": false, "sortOrder": 10
  },
  "statuteArticles": [],               // T10; the field exists now so the shape is stable
  "agreements": [
    {
      "agreementId": "…", "companyId": "…", "companyName": "Empresa",
      "title": "Empresa · 2026–2028",
      "validFrom": "2026-01-01", "validTo": "2028-12-31",
      "clauses": [
        {
          "clauseId": "…", "number": 2, "title": "…", "summary": "…", "body": "…",
          "economicComparisonNote": "…", "isExcludedFromCompilation": false
        },
        { "clauseId": "…", "number": 10, "…": "…" }
      ]
    }
  ],
  "unpublishedRequested": [],          // ids of requested drafts, never rendered
  "page": 1,
  "pageSize": 5,
  "total": 2                           // published agreements in the requested set
}
```

**Adjacency, not alignment (Q-6).** Each agreement is a column and each clause sits inside its own
agreement. The response never pairs clause A with clause B; clauses within a heading are adjacent, not
aligned. The T14 UI must not render a pairing.

### 3.2 Stable order

| Collection | Order |
|---|---|
| Heading groups | `sortOrder`, then `name` |
| Headings (T8 endpoint) | group order, then heading `sortOrder`, then `name` |
| Candidates | `validFrom` **descending**, then agreement id |
| Comparison agreements | `validFrom` **descending**, then agreement id |
| Comparison clauses | `number` **ascending** (numeric, never lexical) |

### 3.3 Exclusions and publication

- A heading with `is_excluded = true` ("No Compara") produces **no candidates and no comparison rows**.
  The endpoint returns `200` with an empty `agreements` array and `heading.isExcluded = true`, so the
  caller sees why nothing is there. The test creates a published clause under that heading and asserts
  its title and body are absent from the JSON.
- `isExcludedFromCompilation` on a clause is **not** a comparison filter. It controls the compiled
  document, not the comparison workspace; the two flags are deliberately different.
- Only `Published` agreements are rendered. A requested draft is put in **`unpublishedRequested`**
  and contributes no column, even for a caller who can read drafts. Candidates exclude drafts too.
- `unpublishedRequested` is outside the page: it reports why a requested column is missing, not
  agreement data.

### 3.4 Paging and bounds

| Bound | Value |
|---|---|
| Candidates page size | default 10, maximum 50; invalid values fall back to the default |
| Comparison page size | default 5, maximum 10; invalid values fall back to the default |
| Selected agreements per request | maximum 50 distinct ids; more is **400** |
| Missing/blank `agreementIds` or invalid GUID | **400 `validation_failed`** |

Candidates page over agreements. The comparison page over the **published** requested agreements, so a
company with hundreds of clauses cannot make one request return the whole corpus: the caller chooses a
heading and a bounded set of columns. `total` is the number of published requested agreements, not the
number of clauses.

### 3.5 Tenancy

A normal user's candidate query is restricted to their own company; passing another `companyId` is
**404**. In the comparison grid every requested agreement must be visible:
one another-company or nonexistent id makes the whole request **404**, with no partial projection.
A `company.read_all` holder may compare across companies, as everywhere else. The database RLS added
by T8 scopes all agreement/clause rows by company as the second line.

## 4. Implementation

| File | Purpose |
|---|---|
| `src/Convenciones.Comparison/ComparisonContracts.cs` | Read-model records: candidates, heading view, clause/agreement view, response |
| `src/Convenciones.Comparison/ComparisonQueries.cs` | `Candidates` projection over `IQueryable<Agreement>`/`Company`; no `DbContext`, no writes |
| `src/Convenciones.Api/Endpoints/ComparisonEndpoints.cs` | Heading groups, candidates, comparison grid, validation and tenancy |
| `src/Convenciones.Api/Endpoints/ComparisonHeadingEndpoints.cs` | T8 taxonomy read (unchanged) |
| `tests/…/ComparisonTests.cs` | Exclusion, publication, adjacency/order, paging, tenancy, read-only |

`ComparisonQueries` receives queryables rather than the DbContext, so it cannot write and the endpoint
is forced to apply `AsNoTracking`. The Q-1 title is composed **after** materialisation (a computed
property inside the SQL projection could not be translated), using the shared `AgreementTitles` rule.

## 5. Acceptance criteria

1. A clause whose heading is **"No Compara"** is absent from comparison output; candidates are empty
   and the JSON does not contain the clause title or body — the test asserts the absence.
2. An **unpublished** agreement never appears, even for a user who can read drafts; it is reported in
   `unpublishedRequested` and contributes no clause. Publishing it makes it appear.
3. Clauses are grouped by heading in a **stable, documented order**: clauses by integer number, columns
   by `validFrom` descending then id, groups by `sortOrder` then name.
4. The projection is **read-only**: `AsNoTracking` on every query, no write method in the module, and
   `POST`/`PUT /comparison` return **405**.
5. The response is **paged and bounded**: candidate page size ≤ 50, comparison page size ≤ 10, at most
   50 selected ids; invalid or excessive values are rejected or clamped as documented.
6. **Cross-company** data is absent: another company's candidate page or agreement id is **404**, and a
   mixed request returns nothing. RLS hides it as the second line.

## 6. Test matrix

`ComparisonTests`, 7 integration tests against the real PostgreSQL container:

| # | Test | Asserts |
|---|---|---|
| 1 | `An_excluded_heading_never_yields_comparison_rows` | candidates empty; comparison empty with `isExcluded`; clause title/body absent from JSON |
| 2 | `An_unpublished_agreement_is_never_rendered_but_is_reported` | candidates exclude; `unpublishedRequested`; no clause; appears after publish |
| 3 | `Candidates_are_published_company_scoped_and_paged` | total vs SQL; two pages disjoint; order; company A/B; cross-company 404 |
| 4 | `Comparison_groups_clauses_by_heading_in_stable_order_and_returns_the_economic_note` | newest first; `[2, 10]`; other heading absent; note returned; paging |
| 5 | `Another_companys_agreement_is_404_and_mixed_requests_return_nothing` | foreign 404; mixed 404 with no partial data; missing 404; owner 200 |
| 6 | `Comparison_is_read_only_bounded_and_rejects_invalid_requests` | POST/PUT 405; 51 ids 400; missing/invalid ids 400; `pageSize` clamped; unknown heading 404 |
| 7 | `Heading_groups_are_ordered_and_flag_the_economic_comparison_requirement` | groups ordered; GENERALES false; ECONOMICO true |

## 7. Risks

- **Adjacency is not alignment.** The API deliberately returns no pairing. T14 must render columns and
  clauses without a visual equivalence; R2-01 is the feature that would add pairing.
- **The exclusion is the highest-value silent failure.** A wrong `is_excluded` filter makes comparison
  look plausible while including 853 "No Compara" clauses. Test 1 asserts absence from the JSON, not
  only a count.
- **A draft in `unpublishedRequested` is still a fact about it.** It discloses that the id exists to a
  caller who can already read the draft; it is not returned for another company.
- **The comparison is not capped per agreement.** Paging limits the number of agreements, not the
  clauses inside one agreement. A pathological single agreement with hundreds of clauses under one
  heading would still return them; the T14 UI must handle long columns. Adding a cap would hide data,
  so it was not done silently.
- **Statute articles are empty until T10.** The field is present so adding them is not a contract
  change; a caller must not read the empty array as "no statute applies".

## 8. Implementation record

### Verified by execution

```
dotnet build Convenciones.slnx -> Compilación correcta. 0 advertencias, 0 errores
dotnet test  Convenciones.slnx -> UnitTests         10/10
                               -> IntegrationTests  93/93   (7 new ComparisonTests)
```

Total repository tests at this commit: **103 .NET + 44 web = 147**, 0 failed.

### What changed after the first test run

The first candidates query projected a record with a computed `Title` and ordered by a field of that
projection; EF Core could not translate it (`could not be translated`). The fix was to order inside the
query over the entity columns and compose the title after materialisation, using the shared
`AgreementTitles` helper. The API behaviour and response shape are unchanged.

## 9. Left undone

- Clause alignment/pairing (R2-01), export (R2-04), full-text search (R2-02).
- Statute articles — the field was empty at merge; **T10 fills it** (`statuteArticles` plus
  `statuteArticleTotal`, bounded at 100 per heading).- The comparison workspace UI (T14).
- A per-agreement clause cap; paging bounds the number of agreements only (see §7).
