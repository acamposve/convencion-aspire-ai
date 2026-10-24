# T9 — Comparison API

**Status:** planned · **Depends on:** T8 · **Blocks:** T14

## Objective

The product's purpose: compare clauses across a company's agreements, with the "No Compara" exclusion
respected.

## Scope

**In:** comparison candidates for a company; the comparison projection joining clauses across agreements
by heading (**adjacency**, per Q-6 — alignment is R2-01); `is_excluded` honoured so the 853 "No Compara"
clauses do not pollute results; **unpublished agreements excluded**; economic comparison as free text
(Q-3).

**Out:** clause alignment/pairing (R2-01), export (R2-04), full-text search (R2-02).

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Comparison/` | Read-model projection, queries |
| `src/Convenciones.Api/Endpoints/ComparisonEndpoints.cs` | `/comparison` |
| `tests/…/ComparisonTests.cs` | Exclusion, adjacency, publication filtering |

## Acceptance criteria

1. A clause whose heading is "No Compara" is **absent** from comparison output, and the test asserts the
   absence rather than assuming it.
2. An unpublished agreement never appears, even for a user who can read drafts.
3. Clauses are grouped by heading in a stable, documented order.
4. The projection is read-only: `AsNoTracking` and no write path exists (CQRS).
5. Response shape is paged and bounded; a company with hundreds of clauses does not return everything.
6. Cross-company data is absent (404 on another company's comparison).

## Risks

- 15.8% of legacy clauses sit in one heading. If the exclusion is wrong in either direction, comparison
  looks plausible while being wrong — the highest-value silent failure in the product.
- Q-6 adjacency means clauses are *not* aligned; the UI must not imply a pairing that does not exist.
