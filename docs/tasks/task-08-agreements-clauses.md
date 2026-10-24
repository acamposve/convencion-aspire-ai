# T8 — Agreements and clauses API

**Status:** planned · **Depends on:** T3 · **Blocks:** T9, T11, T14

## Objective

The core of the product: author a collective agreement, its clauses and annexes, and publish it.

## Scope

**In:** agreement CRUD with composed title (Q-1) and identity `(company_id, valid_from)`; clause CRUD
with mandatory integer numbering (Q-2) and a mandatory heading (Q-3 is free text, but the heading link is
required); annex metadata; publish/unpublish recording actor and timestamp (Q-7); headings taxonomy read;
per-company scoping.

**Out:** comparison (T9), negotiation (T11), search (R2-02), alignment (R2-01).

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Agreements/` | Aggregate, commands, queries |
| `src/Convenciones.Api/Endpoints/AgreementEndpoints.cs` | `/agreements`, `/agreements/{id}/clauses`, `/publish` |
| `tests/…/AgreementTests.cs` | Domain invariants + API contract |

## Acceptance criteria

1. Two agreements for the same company with the same `valid_from` are refused with **409**, and the
   database constraint is the thing that refused it (test proves both layers agree).
2. Clause numbers are integers; a duplicate within an agreement is **409**; ordering is numeric
   (`2` before `10`).
3. A clause without a heading is refused — the 899 unclassified legacy clauses are the reason.
4. Publishing requires `agreement.publish`; a caller without it gets **403** and the row is unchanged.
5. Publishing records actor and timestamp; unpublishing clears them; the transition is audited once.
6. The composed title contains the company name and the period and is **not** stored.
7. A user of company A cannot read or write company B's agreements (404/unchanged).

## Risks

- The legacy stored `title` and `number` and they drifted from reality; Q-1 removed them. Any code that
  reintroduces a stored title must be rejected in review.
- Clause numbering was a varchar in legacy, which made numeric ordering impossible. The migration fixed
  the column; the query layer must not re-introduce string ordering.
