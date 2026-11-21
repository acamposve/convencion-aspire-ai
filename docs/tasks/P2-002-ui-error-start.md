# P2-002 — Start processing only on a confirmed absence

**Status:** implemented and tested.

## Defect

`ClauseProcessingDialog` started a processing run for **any** failure of
`GET /documents/{id}/processing` (`catch { startProcessing(...) }`). A 401/403, a 500, a network error or an
unknown failure therefore produced an unexpected write, not just a message.

The ambiguity was in the contract: both "no run yet" and "this document is not visible to you" returned
`404 not_found`, so the client could not tell them apart.

## Change

**Contract:** when the document is visible but has no run, the endpoint now returns
`404 run_not_found` (`ErrorCodes.RunNotFound`). A document that is not visible keeps `404 not_found`.

**Client:** the dialog starts work only for `status === 404 && code === 'run_not_found'`. Every other
failure is shown through the existing error state, with a **Reintentar** action that re-runs the query
without starting anything.

## Tests

Web (`ClauseProcessingDialog.test.tsx`, 4 new):

- `500 internal_error` → error shown, `startProcessing` never called, retry offered;
- `403 forbidden` → error shown, no start;
- absent run (`run_not_found`) → start is called;
- after a transient failure, retry clears the error without starting work.

API (`ProcessingStartTests`, 1 new): a visible document with no run returns `404` + `run_not_found`; an
invisible document returns `404` + `not_found`.

## Evidence

| Gate | Result |
|---|---|
| Web tests (`vitest`) | **67 passed** (63 + 4) |
| `pnpm run typecheck` | clean |
| Unit | **244 passed** |
| Integration | **363 passed** (362 + 1) |
| `dotnet build -c Release -warnaserror` | 0 warnings, 0 errors |

## Notes

- The error code is additive: existing clients that treated any 404 as "no run" keep working, but the
  dialog now requires the explicit code.
- No migration, configuration or worker change.
