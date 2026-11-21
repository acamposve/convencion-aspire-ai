# P2-003 — Deterministic worker-deadline test

**Status:** implemented and tested; a classification race the new test exposed is fixed.

## Defect

The previous `WorkerDeadlineLifecycleTests` set `AttemptTimeoutSeconds = 2`, started the worker with a
one-minute deadline, and then moved `DeadlineUtc` into the past once the stage was blocked. The token
that cancelled the stage was the attempt timeout's, scheduled before the mutation; the
`deadline_reached` that followed was decided by re-reading the mutated option in the catch block. The
property the test claimed to prove — that a deadline fixed before the stage cancels the attempt — was
never exercised, and deleting the deadline clamp in `ProcessingWorker` left the test green.

## Change

### The test

`tests/Convenciones.IntegrationTests/WorkerDeadlineLifecycleTests.cs`:

- **Deadline fixed before the worker starts**, never mutated. `AttemptTimeoutSeconds = 600`, two
  orders of magnitude beyond the deadline lead, so only the deadline can cancel the stage inside the
  barrier window.
- **Barriers instead of sleeps.** The stage publishes the claimed `ProcessingJob` on `Started` and
  signals `Cancelled` when its token fires; the test waits on those events (60 s ceiling) instead of
  polling a "check every 500 ms for 10 s" window.
- **Database evidence.** The run is `Failed` / `deadline_reached` with `attempts = 1` and
  `finished_at`; exactly one attempt exists, its id is the claimed `AttemptId`, and it is `Failed` /
  `deadline_reached` / closed; `processing.failed` is audited once and `processing.retried` never.
- **Repetition.** The scenario runs three times (`[Theory]`, repetitions 1–3). This is repetition,
  not a synthetic load generator.

### The worker fix the test exposed

The first CI run of the rewritten test failed 2 of 3 repetitions (run `44925496380`): the stage was
cancelled, but the run never reached a terminal state and the test timed out after 60 s.

Cause: the attempt timeout is scheduled with `CancellationTokenSource.CancelAfter`, which runs on the
monotonic clock, while `DeadlineUtc` is wall-clock time. While the clock is being slewed the two can
differ by a few milliseconds; the timer then fires just before `DateTimeOffset.UtcNow` reaches the
deadline. The catch block compared the two, took the retryable `timeout` branch, requeued the run, and
the loop then exited on the deadline — so the run stayed `Queued`, with no worker left to retry it.
A deadline turned a permanent failure into a run nobody would retry.

`ProcessingWorker` now records that the timeout was shortened by the deadline
(`deadlineBoundTimeout`) and `WorkerDeadlinePolicy.IsDeadlineFailure` treats that cancellation as the
deadline's, keeping the wall-clock comparison only as a fallback. Four unit tests pin the rule,
including the exact case: a deadline-bound timer that fires 5 ms before the wall clock reaches the
deadline.

No schema, migration or configuration change.

## Mutation checks

Three deliberate mutations were built and run against the final code; all were reverted and
`git diff` contains only the intended change.

| Mutation | Expected failure | Observed |
|---|---|---|
| Remove the `remaining < attemptTimeout` clamp (and the flag) | the stage is never cancelled; the test must fail at the `Cancelled` barrier | **3/3 failed** with `TimeoutException` at the barrier, 3 min |
| Keep the clamp but report `timeout` instead of `deadline_reached` | the run and the attempt carry the wrong code | **3/3 failed** with `Expected "deadline_reached" but was "timeout"` at the run's `error_code`, 15 s |
| Make `IsDeadlineFailure` ignore the deadline-bound flag | the wall-clock comparison must let the 5 ms case through | **1 failed** / 16 unit tests (the deadline-bound case), 64 ms |

## Evidence

| Gate | Result |
|---|---|
| `dotnet build Convenciones.slnx -c Release -warnaserror` | 0 warnings, 0 errors |
| `dotnet test Convenciones.slnx -c Release --no-build` — unit | **248 passed** (244 + 4) |
| same run — integration | **365 passed** (3 new cases, ~5 s each); 2 m 44 s |
| Web | not run — no web change |

## Notes

- The only wall-clock assumption left is the 5 s lead for the worker to start and claim: two database
  round trips on an already-warm connection pool. If that lead were ever exceeded, the test fails at
  `Started` with a timeout and a clear message instead of passing for the wrong reason.
- The `TimeoutException` raised while waiting for a terminal run now reports the last observed status,
  `error_code` and attempt count, which is what identified the CI race in one run.
- No fake clock was introduced: `CancellationTokenSource.CancelAfter` is real-time, so a fake clock
  would not make the cancellation deterministic without changing production semantics. The
  classification is now independent of the clock instead.
