# T14 — Web: core domain, administration and public portal

**Status:** planned · **Depends on:** T9, T11, T12, T13 · **Blocks:** — · **Stack:** React + Vite + MUI, **no Tailwind**

## Objective

Complete the interface for the whole MVP: agreements and clauses, comparison, legal library, negotiation,
administration, audit, and the anonymous portal.

## Scope

**In:** agreement and clause editor (numeric ordering, heading picker, publication state); comparison
workspace honouring adjacency and exclusion; statute/article editing; negotiation case screens with the
five stages; user/role administration; audit viewer; public portal pages (published agreements, news);
responsive and keyboard-accessible throughout (F-17).

**Out:** clause alignment (R2-01), export (R2-04), worklists (R2-05/R2-06), bulk import (R2-07).

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Web/src/features/agreements/` | Editor and clause list |
| `src/Convenciones.Web/src/features/comparison/` | Comparison workspace |
| `src/Convenciones.Web/src/features/negotiation/` | Case screens |
| `src/Convenciones.Web/src/features/admin/` | Users, roles, audit |
| `src/Convenciones.Web/src/features/public/` | Anonymous portal |

## Acceptance criteria

1. Every capability the API enforces is reflected in the UI — a control the caller cannot use is not
   shown, and the API still refuses it if reached.
2. The comparison view makes **no claim of alignment**: it groups by heading and shows clauses adjacent,
   per Q-6.
3. A published agreement cannot be edited in place without an explicit, confirmed unpublish.
4. The public portal renders with no session and no token in storage.
5. Every list is paged and every form validates before submitting.
6. Keyboard-only navigation completes: sign in → upload → author a clause → publish → compare.

## Risks

- **The interface will imply certainty the data does not have.** 899 clauses are unclassified and
  comparison is adjacency-only; the UI must say so rather than drawing an equivalence the backend does
  not assert.
- Scope creep into R2 features is the main delivery risk for this task.
