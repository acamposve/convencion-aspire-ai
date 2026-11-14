# Retention policy (T4)

## What retention removes

For a soft-deleted document, a full retention sweep removes:

- `platform.clause_classification`;
- `platform.processing_attempt`;
- `platform.extracted_clause`;
- `platform.processing_run`;
- `platform.document_text`.

The `platform.document` row remains as the tenant's soft-deleted record. `platform.audit_entry` is
append-only and is never deleted or updated by retention.

`DELETE /documents/{id}/processing` performs the same full purge for the document's AI output and
extracted text, while refusing to proceed if a human correction exists.

## Scope

- The background sweeper only selects documents with `deleted_at is not null`.
- It applies the worker scope: company, optional document and optional run.
- A run-scoped sweep removes only that run's classifications, attempts, clauses and run row. It does
  **not** remove `document_text`, because the text belongs to the whole document and may still be used
  by another/current run.
- A document-scoped or unscoped sweep removes the document text as well.
- A human correction on an included run/document blocks that run/document from being purged; the
  correction is a legal-hold-like signal and its audit row must not be orphaned.

## Concurrency

Retention takes the exclusive document lock first, then the exclusive run lock. This is the same
`document -> run -> clause` hierarchy used by reviews, extraction and classification writes. The
document is re-read after the lock, and the correction check happens under the locks.

## Scheduling

Retention runs as part of the worker lifecycle. `ProcessingRetentionSweeper` sweeps once at startup and
then every `Retention:IntervalMinutes` (default 360), processing at most `Retention:BatchSize` documents
per pass (default 100) — but **only while the worker is enabled** (`Worker:Enabled=true`, supplied by
the `Worker__Enabled` environment variable).

When `Worker__Enabled=false` the sweeper does **not** execute at all: there is no startup sweep and no
periodic sweep. The hosted service completes immediately instead of entering its loop.

Disabling the worker therefore disables retention as well. It is not a way to keep retention running
without processing, and retention must never be assumed to have run while the worker is disabled:
soft-deleted documents keep their AI output until the worker is enabled again, or until an operator
purges them through `DELETE /documents/{id}/processing`.

No schema migration is required.
