# P2-004 — Attempt close invariant

**Status:** implemented and tested.

## Defect

`ProcessingQueue.FinishAttemptAsync` ignored the affected-row count. A run could move to `Completed`,
`Failed` or `Cancelled` while the expected attempt stayed `Running`; the audit entry and the run state were
committed anyway, leaving an attempt that no terminal run explains.

## Change

`FinishAttemptAsync` returns the number of closed rows. The three transitions now require exactly one:

- `CompleteAsync` and `FailAsync` roll back (run update + audit entry) and throw `InvalidOperationException`
  when the attempt did not close exactly once;
- `CancelInTransactionAsync` throws before returning `true`; the caller's transaction is rolled back.

Stale calls are unchanged: when the run update matches zero rows the method still rolls back and returns
without an error, so a superseded attempt remains a no-op rather than a failure.

## Tests

`AttemptInvariantTests` (PostgreSQL 17, 3 tests):

1. a temporary trigger drops the attempt update while the run transition succeeds → `CompleteAsync` throws,
   the run stays `Running`, the attempt stays `Running`, and the `processing.completed` audit entry is
   rolled back with the transaction;
2. the same for `FailAsync` (run stays `Running`, no `processing.failed` audit entry);
3. completing twice is a stale no-op: the run is `Completed` and exactly one attempt is closed.

## Notes

- The failure surface is `InvalidOperationException`; the worker logs it and the run remains `Running`
  until lease recovery reclaims it, which is the existing recovery path rather than a new one.
- No migration, no API change, no configuration.
