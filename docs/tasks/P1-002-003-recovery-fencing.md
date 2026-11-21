# P1-002 + P1-003 — Periodic recovery and attempt fencing

**Status:** implemented · **Findings:** F-02 (recovery only at startup), F-03 (progress not fenced)
**Scope:** worker only, no schema change, no new dependency

## P1-002 — recovery during the process lifetime

`ProcessingWorker` called `ProcessingQueue.RecoverAsync` once, before its loop. A run whose worker died
was only reclaimed if the process restarted after the lease expired; otherwise it stayed `Running`
indefinitely.

Recovery now also runs from the worker loop, at most once per `Worker:RecoveryIntervalSeconds`
(default 60, floor 1). It still runs unconditionally at startup, preserving the previous behaviour.

**Why periodic recovery is gated on `Worker:Enabled`:** a disabled worker is not consuming the queue, so
sweeping would only churn state that an operator deliberately parked. This matches the retention
sweeper, which was gated on the same flag in PR #92. Startup recovery keeps its historical behaviour.

**Concurrency across instances:** `RecoverAsync` is a single set-based `UPDATE … FROM (CTE)` over
`status = 'Running' AND lease_expires_at < now()`. PostgreSQL row locks serialise concurrent sweeps, so
the second one matches zero rows. A test proves the sum of two concurrent sweeps is one.

**Retry policy:** unchanged. Recovery requeues only while `attempts < @max`; otherwise it closes the run
and the attempt as `Failed`. It does not consume an attempt itself — the next claim does.

## P1-003 — fenced progress writes

`IClauseStore.UpdateProgressAsync(Guid runId, …)` updated `stage` and `clause_count` by run id alone.
A worker that lost its lease could overwrite its successor's progress.

The contract is now `UpdateProgressAsync(ProcessingJob job, …)`, implemented as one conditional UPDATE:

```sql
update platform.processing_run r
set stage = @stage, clause_count = @count, updated_at = now()
where r.id = @run
  and r.status = 'Running'::platform.processing_status
  and r.attempts = @attempt
  and exists (
      select 1 from platform.processing_attempt a
      where a.id = @attempt_id and a.run_id = r.id
        and a.attempt_no = @attempt
        and a.status = 'Running'::platform.processing_status)
returning r.id;
```

Zero rows means the worker no longer owns the attempt: it throws
`StaleProcessingAttemptException`, the same signal the worker already handles for clause and
classification writes. No read-then-write, no new exception type, no separate ownership model.

An empty `AttemptId` is rejected before touching the database.

## Files

| File | Change |
|---|---|
| `src/Convenciones.Worker/WorkerOptions.cs` | `RecoveryIntervalSeconds` (60) |
| `src/Convenciones.Worker/ProcessingWorker.cs` | periodic sweep in the enabled loop; `TryRecoverAsync` shared by startup and periodic paths, logging and swallowing non-cancellation failures |
| `src/Convenciones.Worker/Extraction/IClauseStore.cs` | progress contract takes the job |
| `src/Convenciones.Worker/Extraction/NpgsqlClauseStore.cs` | fenced conditional UPDATE |
| `src/Convenciones.Worker/Extraction/ClauseExtractionStage.cs` | passes the job |
| `tests/.../WorkerRecoveryFencingTests.cs` | 11 new PostgreSQL tests |
| `tests/.../ClassificationPipelineTests.cs`, `PromptInjectionTests.cs`, `ClausePipelineTests.cs` | fake stores updated to the new signature |

## Tests

Recovery (P1-002):

1. `Recovery_reclaims_an_expired_lease_while_the_worker_is_running` — starts a real worker, expires the
   lease after startup recovery has run, waits for `processing.recovered`; proves no restart is needed.
2. `Recovery_does_not_touch_a_live_lease` — three sweeps against a 60 s lease change nothing.
3. `Concurrent_recovery_sweeps_reclaim_a_run_once` — two queues sweep simultaneously; sum is 1, attempts stay 1.
4. `Recovery_ignores_completed_runs`.
5. `A_cancelled_recovery_sweep_changes_nothing` — cancellation leaves the run `Running`.

Fencing (P1-003):

6. `The_current_attempt_updates_progress`.
7. `A_previous_attempt_cannot_update_progress_after_the_next_attempt_takes_over` — the stale write throws and leaves the successor's stage/count.
8. `Progress_is_rejected_once_the_run_is_completed`.
9. `Progress_is_rejected_once_the_run_failed`.
10. `Concurrent_stale_and_current_updates_keep_the_current_progress`.
11. `An_empty_attempt_id_is_rejected_before_touching_the_database`.

Existing recovery coverage continues to pass: `Expired_lease_is_recovered`,
`Recovery_exhausted_attempts_closes_run_and_attempt`,
`Recovery_cancels_an_expired_run_for_a_deleted_document`,
`Concurrent_cancel_and_recovery_are_serialized_by_fencing`.

## Results

| Check | Result |
|---|---|
| `dotnet build Convenciones.slnx -c Release -warnaserror` | 0 warnings, 0 errors |
| Unit | **239 passed**, 0 failed |
| Integration | **264 passed**, 0 failed (253 before + 11 new) |

## Limits

- The recovery interval is a fixed delay, not jittered. Several instances starting together sweep at
  roughly the same time; the statement is idempotent, so this costs one wasted query, not correctness.
- Progress writes are fenced by run, attempt number and attempt id. They are not fenced by lease time:
  a worker whose lease expired but was not yet reclaimed can still write. That matches the existing
  clause/classification fencing and is corrected as soon as recovery requeues the run.
- No index was added. The recovery predicate reads `status` and `lease_expires_at` on
  `platform.processing_run`, which the lifecycle already filters by; adding an index is a separate
  decision that needs measurement (P2-005).
