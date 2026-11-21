# P1-004 — Durable document object deletion

**Status:** implemented. The soft delete and the object-deletion intent commit together; object removal is
retried until it succeeds. Operator visibility is a SQL query (no new endpoint).

## 1. Why

`DELETE /documents/{id}` committed the soft delete and only afterwards asked storage to remove the bytes.
A storage failure left a hidden document whose private object still existed, with nothing durable to retry:
the request returned 500 (or logged and forgot), and no record said the object was still there.

## 2. Flow

```
DELETE /documents/{id}
  transaction: lock document → soft delete → cancel runs → INSERT deletion intent
  commit
  attempt storage delete now
      success / 404 → intent Completed
      failure       → attempts++, last_error, next_attempt_at = now + backoff
  return 204 in both cases
```

The caller always gets `204`: the document is already invisible, the object is private, and the durable
record makes the removal eventual. The request no longer fails for a state the caller cannot observe.

## 3. Table

`platform.document_object_deletion`

| Column | Meaning |
|---|---|
| `document_id` (PK) | one intent per document |
| `company_id` | owning tenant, used by RLS and for filtering |
| `storage_path` | object key captured at deletion time |
| `state` | `Pending` or `Completed` |
| `attempts` | number of completed attempts, including the successful one |
| `next_attempt_at` | when the sweeper may try again |
| `last_error` | last storage error message (no object contents) |
| `requested_by` | the user who asked for the deletion |
| `created_at`, `completed_at` | timestamps; `completed_at` is set exactly when `state = 'Completed'` |

Backoff on failure: `next_attempt_at = now() + min(2^attempts, 60) minutes` — 1, 2, 4 … capped at 1 hour,
retried indefinitely.

## 4. Sweeper

`DocumentDeletionSweeper` (hosted service in the API, not the worker: privacy must not depend on the
processing worker being enabled).

| Setting | Default | Meaning |
|---|---|---|
| `Documents:DeletionIntervalSeconds` | 60 | sweep interval; `<= 0` disables the sweeper |
| `Documents:DeletionBatchSize` | 10 | rows claimed per pass |
| `Documents:DeletionLeaseSeconds` | 300 | how long a claimed row is leased |

Claiming uses `platform.claim_document_object_deletions(batch, lease)` with `FOR UPDATE SKIP LOCKED`, so
two instances cannot process the same row in the same window. A failure is logged and the loop continues.

## 5. Operator visibility

```sql
-- pending deletions, oldest first
select d.document_id, d.company_id, d.storage_path, d.attempts, d.last_error, d.next_attempt_at
from platform.document_object_deletion d
where d.state = 'Pending'
order by d.next_attempt_at;

-- is a specific document fully removed?
select state, attempts, completed_at, last_error
from platform.document_object_deletion
where document_id = '<document id>';
```

A row that never leaves `Pending` while `attempts` keeps growing means storage is persistently failing for
that key; the object is private and unreachable, and the row is the evidence.

## 6. Why the sweeper does not write `platform.audit_entry`

The audit table's insert policy requires `platform.is_active_user()`, which is derived from the request JWT.
A background task has no user, so writing an audit row from it would either fail or require widening the
policy to accept anonymous writes — a worse trade. The intent row is the audit trail for this operation:
who asked (`requested_by`), when, how many attempts, the last error and the completion time. The API's own
soft-delete audit entry still records the user action.

For the same reason, the sweeper updates through the two `SECURITY DEFINER` functions
(`claim_document_object_deletions`, `record_document_object_deletion`), granted to `app_api`; the table's
RLS policies still govern the user-facing read path.

## 7. Retention interaction

Deletion intents may be purged only when `state = 'Completed'`. A `Pending` row is the only record that an
object may still exist; removing it would re-create the original defect. Document retention (P1-007) does
not delete documents or their intents.

## 8. Limits

- The API performs one immediate attempt; a slow storage backend delays the response by that one call,
  bounded by the storage client timeout.
- The sweeper holds no database lock during the HTTP call (rows are leased), so a crashed instance is
  retried after the lease expires.
- A storage backend that reports success without deleting cannot be detected here; the adapter's contract
  is that a successful `DeleteAsync` (including 404) means absence.
- No HTTP endpoint exposes pending deletions by design; use the queries above.

## 9. Tests

`tests/Convenciones.IntegrationTests/DurableObjectDeletionTests` (7 tests, PostgreSQL 17 + deterministic
fake storage):

1. storage failure → 204, document hidden, intent `Pending`, attempts 1, error recorded;
2. sweeper completes the intent once storage recovers;
3. a missing object is success;
4. a crash between commit and storage call is retried;
5. a failing sweep keeps `Pending`, increments attempts and schedules a future retry without throwing;
6. a second delete is 404 and does not duplicate the intent;
7. another tenant cannot read the intent under `app_api` with its own claims.
