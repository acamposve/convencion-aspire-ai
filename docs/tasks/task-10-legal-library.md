# T10 — Legal library API

**Status:** planned · **Depends on:** T3 · **Blocks:** T12, T14

## Objective

The statutes and articles that comparison reads, and the single labour statute the schema enforces.

## Scope

**In:** statute CRUD with kind; article CRUD with integer numbering and mandatory heading; the
**single labour statute** invariant surfaced as a clear error; article text retrieval.

**Out:** the 2,672 unclassified legacy articles' worklist (R2-05); AI classification (FUT-02).

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Legal/` | Commands, queries |
| `src/Convenciones.Api/Endpoints/StatuteEndpoints.cs` | `/statutes`, `/statutes/{id}/articles` |
| `tests/…/StatuteTests.cs` | Invariants and scoping |

## Acceptance criteria

1. Creating a second `LabourLaw` statute is refused with a **409** naming the constraint, and the partial
   unique index is what refused it.
2. Article numbers are integers, unique per statute, ordered numerically.
3. An article without a heading is refused (89.6% of legacy articles were unclassified).
4. Cross-company access is **not** applicable to statutes — they are company-independent reference data;
   the test must prove an authenticated user of any company can read them, which is a deliberate
   difference from agreements.
5. Writes require `statute.write`.

## Risks

- Statutes are global while everything else is tenant-scoped. If a company predicate is applied to them by
  reflex, the library becomes invisible to everyone; if it is omitted everywhere by reflex, tenant data
  leaks. The distinction must be explicit and tested.
