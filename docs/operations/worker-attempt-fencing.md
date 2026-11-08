# Worker attempt fencing (T3.1-B)

The attempt UUID generated at claim time is the worker's fencing token.

## Claim

`ClaimAsync` creates `processing_attempt.id`, inserts the attempt row with `attempt_no`, and returns
`ProcessingJob.AttemptId` together with `RunId`, `DocumentId`, `CompanyId` and `Attempt`.

## Completion and failure

`CompleteAsync` and `FailAsync` update `processing_run` only when all of the following hold in the same
transaction:

- `run.id = job.RunId`;
- `run.status = Running`;
- an attempt exists with `attempt.id = job.AttemptId`;
- `attempt.run_id = job.RunId`;
- `attempt.attempt_no = job.Attempt`;
- `attempt.status = Running`.

`FinishAttemptAsync` applies the same attempt identity/run/number/status predicate before changing the
attempt. A stale worker therefore updates zero rows: no run transition, no attempt transition and no
completion/failure audit.

`CancelAsync` uses the same fencing predicate for worker-originated cancellation. A stale worker cannot
cancel a later attempt.

The attempt UUID is the ownership token. Lease expiry alone does not reject a completion unless recovery
has already closed the attempt: while the run is still Running and the attempt is still current there is
no successor to fence, and completing is safe. Recovery closes the attempt before requeueing, so every
later completion/failure from the old claim fails the predicate.

## Clause and classification writes

`NpgsqlClauseStore.SaveClausesAsync` and `SaveClassificationsAsync` perform the same ownership check
inside their persistence transaction, after the shared document lock:

```sql
select r.id
from platform.processing_run r
join platform.processing_attempt a on a.run_id = r.id
where r.id = @run
  and r.status = 'Running'
  and r.attempts = @attempt
  and a.id = @attempt_id
  and a.attempt_no = @attempt
  and a.status = 'Running'
for update of r, a;
```

The row lock is held until the transaction commits. Recovery updates the run/attempt rows, so it
serializes with the write:

- store obtains the row lock first: its delete/insert/replace commits, then recovery closes the attempt;
- recovery obtains the row lock first: the store waits, sees the closed/replaced attempt, throws
  `StaleProcessingAttemptException`, and rolls back without deleting or inserting anything.

The validation occurs before any delete of clauses/classifications and before any insert. A failed
validation or a later persistence error rolls back the whole operation, including the delete-and-replace
part.

`ProcessingWorker` catches `StaleProcessingAttemptException` separately and does not mark the run
failed or retried. The current attempt belongs to another worker.

## Recovery interaction

Recovery closes the interrupted attempt before requeueing or failing the run. The next claim creates a
new attempt UUID, so a late completion or failure from the previous claim fails the fencing predicate.

## Idempotence

Repeated completion/failure from the same job updates zero rows after the first terminal transition.
Concurrent completions of the same attempt serialize on the run row; exactly one terminal update and
one audit row are written.

No new status and no schema migration are required. The existing `processing_attempt.id` is the token.
