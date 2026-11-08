# Deleted document during worker processing (T3.1)

A run claimed for a document that is subsequently soft-deleted must end in a terminal, auditable
state. No new run/attempt status is introduced.

## State policy

| Situation | Run | Attempt | Audit |
|---|---|---|---|
| Document deleted before claim | stays `Queued`; never claimed | none created | none |
| Deleted after claim, before load | `Cancelled` | `Cancelled` with `finished_at` | `processing.cancelled`, code `document_deleted` |
| Deleted after load, before persistence | `Cancelled` | `Cancelled` with `finished_at` | `processing.cancelled`, code `document_deleted` |
| Delete detected during completion | `Cancelled` instead of `Completed` | `Cancelled` with `finished_at` | `processing.cancelled`, code `document_deleted` |
| Generic failure after deletion | `Cancelled` instead of retry/`Failed` | `Cancelled` with `finished_at` | `processing.cancelled`, code `document_deleted` |
| Expired lease, document active | normal recovery (`Queued` or `Failed`) | interrupted attempt closed as `Failed` | existing recovery/failure audit |
| Expired lease, document deleted | `Cancelled` | interrupted attempt closed as `Cancelled` | `processing.cancelled`, code `document_deleted` |

`Cancelled` is the existing terminal state that represents processing stopped because the source
document ceased to exist. `Failed` remains reserved for real processing failures.

## Detection

- `NpgsqlClauseStore.LoadDocumentAsync` distinguishes a missing run/document from a soft-deleted
  document and throws `DocumentDeletedException` for the latter.
- `EnsureDocumentActiveAsync` throws the same typed exception when the document disappears after
  loading.
- `ProcessingWorker` catches `DocumentDeletedException` explicitly and calls the cancellation path; it
  is not treated as a transient failure.
- `FailAsync` and `CompleteAsync` re-check the document under the document lock, so a late deletion
  cannot produce `Queued` retry or `Completed`.
- `RecoverAsync` uses one transaction, row-level status guards and the same document/run scope. It
  finalizes the corresponding attempt for requeued, failed and document-deleted runs.
- Repeated recovery sees no `Running` rows and does not create duplicate audit entries.
- Concurrent cancellation/recovery is serialized by the run row update and the terminal guard.

No schema migration is required.
