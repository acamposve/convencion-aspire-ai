# P1-004 — Durable object deletion

**Status:** implemented and tested.

## What was delivered

| Component | Location |
|---|---|
| Deletion intent table, RLS, claim/record functions | `supabase/migrations/20261121000500_document_object_deletion.sql` |
| Store (immediate attempt + claim/record) | `src/Convenciones.Api/Infrastructure/DocumentDeletionStore.cs` |
| Background retry | `src/Convenciones.Api/Infrastructure/DocumentDeletionSweeper.cs` |
| Atomic intent insertion | `src/Convenciones.Api/Endpoints/DocumentEndpoints.cs` (`DeleteAsync`) |
| Runbook | `docs/operations/p1-004-object-deletion.md` |
| Tests | `tests/Convenciones.IntegrationTests/DurableObjectDeletionTests.cs` (7) |

## Design decisions

1. **Intent commits with the soft delete.** A crash after the commit leaves a retryable row; the request
   never reports failure for a state the caller cannot observe.
2. **The caller always receives 204.** Object removal is eventual and durable; the object is private.
3. **Retry indefinitely with exponential backoff** (1 → 60 minutes cap), keeping `state='Pending'`,
   `attempts` and `last_error` visible.
4. **No HTTP endpoint for operators**; a documented SQL query is the interface for this PR.
5. **No audit write from the sweeper.** The audit policy needs a user JWT; the intent row is the audit
   trail for the background operation, and the user action is still audited by the API.

## Test evidence

7 integration tests: failure leaves `Pending` and returns 204; recovery completes; 404 is success; crash
after commit is retried; failing sweep backs off without throwing; second delete is 404 with one intent;
cross-tenant read is denied under `app_api`.

Build and full-suite results are recorded in the PR.

## Open decisions

None for this scope. Retention purge of completed intents belongs to a future retention task; the rule is
documented (`Completed` only).
