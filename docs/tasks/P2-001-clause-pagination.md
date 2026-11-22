# P2-001 — Clause pagination beyond the first hundred

**Status:** implemented and tested.
**Finding:** F-11 (production-v1 audit) · **PR:** #118

## Objective

The processing dialog always requested page 1 with 100 rows, so a document with more clauses left the
rest invisible and unreviewable while the run reported them as classified.

## What was delivered

| Component | Location |
|---|---|
| Page/total state, pager and caption | `src/Convenciones.Web/src/features/documents/ClauseProcessingDialog.tsx` |
| Tests | `src/Convenciones.Web/src/features/documents/ClauseProcessingDialog.test.tsx` |

## Behaviour

1. The clause list carries `page` and `total`; the dialog requests the current page at the same page size.
2. A pager and a `Mostrando N de M` caption appear whenever the total exceeds one page.
3. Changing page reloads from the API, so a review decision or correction made on a page is still there
   after navigating away and back.
4. A failed page load surfaces through the existing error state instead of showing a stale page.

## Results

PR #118: the new test loads 101 clauses — page 1 shows clause 1 and not 101, the next-page control
reaches clause 101, accepting it persists, and navigating back and forward keeps `Accepted`. Web suite
63 passed; typecheck and production build clean (bundle scan passed); CI green.

## Left undone

None for this scope. The page size stays 100; the API's paging contract is unchanged.
