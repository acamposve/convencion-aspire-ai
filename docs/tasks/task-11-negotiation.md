# T11 — Negotiation workflow API

**Status:** planned · **Depends on:** T8 · **Blocks:** T14

## Objective

Run a negotiation from case to promoted agreement, with the five stages from Q-4 and the database guard
on promotion.

## Scope

**In:** case lifecycle `Open → InProgress → Pending → Agreed → Closed` (Q-4); parties; petitions with
outcomes `Open/Agreed/Rejected/Withdrawn` (Q-5); employer offers; meetings and meeting agreements;
close/reopen; **promotion** creating an agreement atomically and recording `promoted_agreement_id` while
the case rests in `Closed`.

**Out:** negotiation history and print (R2-03); quantified economic outcomes (Q-3: free text).

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Negotiation/` | Aggregates and handlers |
| `src/Convenciones.Api/Endpoints/NegotiationEndpoints.cs` | `/cases`, `/petitions`, `/offers`, `/meetings`, `/promote` |
| `tests/…/NegotiationTests.cs` | Stage machine, guard, atomic promotion |

## Acceptance criteria

1. Only legal stage transitions are accepted; an illegal one is **409** with the current stage.
2. Promotion requires `negotiation.promote`; without it **403**, and the database guard fires
   independently (test asserts the exception, not just the API status).
3. Promotion creates the agreement **and** sets `promoted_agreement_id` in one transaction; a forced
   failure leaves neither.
4. A case cannot be promoted from a non-`Closed` state — the database CHECK, not only the handler,
   refuses it.
5. Reopening a closed case clears `closed_at` and is audited.
6. A closed+promoted case cannot be promoted twice.

## Risks

- **Q-4 interpretation is unresolved.** Whether `Agreed` is the pre-promotion terminal state or promotion
  happens from `Closed` needs your confirmation; the schema currently enforces "promotion implies
  Closed". Building the wrong one is a rework of the state machine.
- Legacy evidence is 2 cases and 1 agreement, so this is designed from code reading, not observed
  behaviour.
