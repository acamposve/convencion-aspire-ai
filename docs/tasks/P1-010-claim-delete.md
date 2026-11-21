# P1-010 â€” Claim-vs-delete race: investigation and closure

**Status:** investigated; race **not reproduced**; one explicit terminal guard added
**Finding:** F-10 (audit: "probable, not reproduced") Â· **Scope:** claim SQL + tests, no migration

## 1. The race as reported

`ProcessingQueue.ClaimRowAsync` checks `deleted_at` inside its candidate CTE but does not take the
document advisory lock that `DELETE /documents/{id}` takes. The audit could not reproduce it and asked
for the interleaving to be demonstrated or refuted.

## 2. Entities and transactions

| Side | Transaction |
|---|---|
| Delete | advisory xact lock on the document key â†’ `UPDATE document SET deleted_at` â†’ `CancelActiveRuns` (locks every Queued/Running run `FOR UPDATE`, cancels the run, cancels the current attempt, writes audit) â†’ commit |
| Claim | one statement: CTE selects one candidate `FOR UPDATE SKIP LOCKED` **and** `exists(document active)` â†’ `UPDATE run SET Running, attempts+1` (same statement) â†’ insert attempt â†’ commit |

Entities: `platform.document` (`deleted_at`), `platform.processing_run` (`status`, `lease_expires_at`,
`attempts`), `platform.processing_attempt` (`attempt_no`, `status`).

## 3. Why the invalid state cannot be produced

1. **The candidate row is locked by the same statement that checks the document.** `FOR UPDATE SKIP
   LOCKED` takes the run row lock during candidate selection, not after it.
2. **Delete never commits a soft-delete without cancelling the active runs**, because both happen in
   one transaction. Therefore a *committed* delete implies zero Queued/Running runs for that document.
3. Ordering A (claim wins the row lock): delete's cancel blocks on that row, then cancels the run and
   its current attempt after the claim commits.
4. Ordering B (delete holds/locked the row first): the claim's candidate is skipped (`SKIP LOCKED`) or
   no longer matches `Queued`; no claim happens.
5. A claim cannot observe a deleted document and succeed, because a deleted document is never visible
   without its run cancellations in the same commit.

**Conclusion: the specific defect described in F-10 â€” a claim after a committed delete, or an orphan
attempt after deletion â€” could not be produced.** The run row, not the document check, is what
serialises the two operations.

## 4. The one real weakness, and the change

The outer UPDATE's predicate was only `r.id = c.id`. Correctness therefore rested on the CTE's row lock
plus PostgreSQL's re-check of the qualified row. That is true today, but it is an implicit dependency: if
the CTE ever stops locking (a refactor, a different planner shape), the outer update could move a
Cancelled run back to Running.

Added one predicate:

```sql
where r.id = c.id
  and r.status = 'Queued'::platform.processing_status
```

No new lock, no new abstraction, no deadlock surface. The transition is now explicitly terminal-safe
regardless of how the candidate row was reached.

**Why the claim does not take the document advisory lock:** it would invert the lock order. Delete takes
document (exclusive) â†’ run; claim would take run (`FOR UPDATE`) â†’ document (shared), a classic deadlock
window. The run row is sufficient because delete always locks the run row of an active run inside the
same transaction as the soft delete.

## 5. Constraint check

`processing_run.document_id â†’ platform.document(id) ON DELETE RESTRICT`; `processing_attempt.run_id â†’
processing_run(id) ON DELETE CASCADE`. Hard-deleting a document with runs is refused by the database, and
attempts cannot outlive their run. **No migration is needed and none was added.**

## 6. Can a claimed run still touch a deleted document?

`LoadDocumentAsync` joins the document and throws `DocumentDeletedException` when `deleted_at` is set,
before any model call; the worker cancels the run. Covered by the existing
`Worker_catches_document_deleted_and_cancels_the_run`,
`Deleted_document_detected_after_claim_cancels_run_and_attempt` and
`Stale_worker_cannot_write_clauses_or_classifications_after_recovery`.

## 7. Tests added (`ClaimDeleteRaceTests`)

| Test | What it pins |
|---|---|
| `Claim_after_a_committed_delete_returns_nothing` | ordering B: no claim after the delete commits |
| `Claim_before_delete_leaves_no_active_run_or_running_attempt` | ordering A: run and attempt both Cancelled, document deleted |
| `Concurrent_claim_and_delete_never_leave_an_active_run` | barrier-synchronised, 10 iterations; whichever side wins, no active run and no Running attempt |
| `Delete_still_cancels_a_run_that_was_recovered_after_its_lease_expired` | recovery then delete still cancels |
| `Deleting_company_A_document_does_not_touch_company_B_run` | tenant isolation of the cancellation |
| `No_attempt_is_orphaned_by_the_delete` | every attempt still references an existing run; none left Running |

Repeated deletion is already pinned by `Delete_soft_deletes_removes_the_object_and_keeps_the_audit_trail`
(second delete is a plain 404 with no second mutation); it was not duplicated.

## 8. Results

| Check | Result |
|---|---|
| `build -c Release -warnaserror` | 0 warnings, 0 errors |
| Unit | **239 passed** |
| Integration | **270 passed** (264 before + 6 new) |

## 9. Limits

- The concurrency test asserts the persisted invariant across 10 barrier-synchronised iterations; it does
  not pin one specific microsecond interleaving, because the production code has no hook to pause inside
  the claim statement. The sequential tests pin both orders exactly.
- The document lock remains exclusive for delete and shared for worker persistence; the claim deliberately
  stays outside it (see Â§4).
- No index added; the claim already filters `status`/`available_at`, and no measurement justifies one.
