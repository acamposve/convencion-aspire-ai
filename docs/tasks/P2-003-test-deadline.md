# P2-003 — Deterministic worker-deadline test

**Status:** implemented and tested.

## Defect

`WorkerDeadlineLifecycleTests` set `AttemptTimeoutSeconds = 2`, started the worker with a
one-minute deadline, and then, once the stage was blocked, moved `DeadlineUtc` into the past. The
token that cancelled the stage was the attempt timeout's, scheduled before the mutation; the
`deadline_reached` that followed was decided by re-reading the mutable option in the catch block.
The property the test claimed to prove — that a deadline fixed before the stage cancels the attempt —
was never exercised, and deleting the deadline clamp in `ProcessingWorker` left the test green.

## Change

Test-only change in `tests/Convenciones.IntegrationTests/WorkerDeadlineLifecycleTests.cs`.

- **Deadline fixed before the worker starts**, never mutated. `AttemptTimeoutSeconds = 600`, two
  orders of magnitude beyond the deadline lead, so only the deadline can cancel the stage inside the
  barrier window.
- **Barriers instead of sleeps.** The stage publishes the claimed `ProcessingJob` on `Started` and
  signals `Cancelled` when its token fires. The test waits on those events (60 s ceiling) instead of
  polling a "check every 500 ms for 10 s" window.
- **Database evidence.** The run row is `Failed` with `deadline_reached`, `attempts = 1` and
  `finished_at` set; exactly one attempt exists, its id is the claimed `AttemptId`, and it is
  `Failed` / `deadline_reached` / closed; `processing.failed` is audited once and `processing.retried`
  never.
- **Repetition.** The same scenario runs three times (`[Theory]`, repetitions 1–3) to catch a
  timing-dependent pass. It is repetition, not a synthetic load generator.

No production code, schema, migration or configuration change.

## Mutation checks

Two deliberate mutations were built and run against the new test; both were reverted, and
`git diff -- src/Convenciones.Worker` is empty.

| Mutation | Expected failure | Observed |
|---|---|---|
| Remove the `remaining < attemptTimeout` clamp in `ProcessingWorker` | the stage is never cancelled; the test must fail at the `Cancelled` barrier | **3/3 failed** with `TimeoutException` at the barrier, 3 min |
| Keep the clamp but report `timeout` instead of `deadline_reached` | the run and the attempt carry the wrong code | **3/3 failed** with `Expected "deadline_reached" but was "timeout"` at the run's `error_code`, 15 s |

## Evidence

| Gate | Result |
|---|---|
| `dotnet build Convenciones.slnx -c Release -warnaserror` | 0 warnings, 0 errors |
| `dotnet test Convenciones.slnx -c Release --no-build` — unit | **244 passed** |
| same run — integration | **365 passed** (3 new cases, ~5 s each); 4 m 3 s |
| Web | not run — no web change |

## Notes

- The only wall-clock assumption left is the 5 s lead for the worker to start and claim: two database
  round trips on an already-warm connection pool. If that lead were ever exceeded, the test fails at
  `Started` with a timeout instead of passing for the wrong reason.
- `deadline_reached` is also the code the worker uses for the "expired before the attempt started"
  and "finished after the deadline" paths, so the test pins the in-flight cancellation path by
  asserting that the stage actually started and was cancelled.
- No new abstraction was introduced: a fake clock would not change `CancellationTokenSource.CancelAfter`,
  which is real-time, so it would not make the cancellation deterministic without changing production
  semantics.
