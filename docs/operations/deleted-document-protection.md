# Deleted document protection

Soft-deleted documents are not current content. The following paths treat them as not found and do not
modify classification state:

- document list/detail/text/download/delete continue to use the global `deleted_at is null` query filter;
- `ListClausesAsync` returns 404 through `DocumentAccess`;
- `GetClauseAsync` verifies the document is still active after taking the shared document lock;
- `ReviewAsync` verifies the document is still active inside the transaction, after the document/run/
  clause locks and before any mutation;
- `GetRunAsync` rejects a run whose document is deleted;
- `ProcessingQueue.ClaimAsync` does not claim runs whose document is deleted;
- `RecoverAsync` does not requeue or fail runs whose document is deleted;
- `NpgsqlClauseStore.LoadDocumentAsync` rejects deleted documents;
- `SaveClausesAsync` and `SaveClassificationsAsync` re-check the document after the shared document
  lock and abort if it was deleted;
- document soft delete takes the exclusive document advisory lock, reloads the row under that lock,
  applies the `If-Match` check to the reloaded row and commits before removing object bytes.

The lock hierarchy remains `document -> run -> clause`. No schema migration is needed.
