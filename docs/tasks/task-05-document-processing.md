# T5 — Document processing and text extraction

**Status:** planned · **Depends on:** T4 · **Blocks:** T6, T7

## Objective

Extract text from the formats the legacy audit confirmed, record the outcome honestly, and make retries
bounded and observable.

## Scope

**In:** PDF text extraction with PdfPig (Apache-2.0); the status machine
`NotRequested → Requested → InProgress → Completed | Failed`; `extraction_attempts`,
`extraction_started_at`, `extraction_completed_at`, `extraction_error`; `POST /documents/{id}/extract`
(202 + polling); bounded retry; `document_text` population with `char_count`.

**Out:** OCR for images; clause parsing or AI classification (FUT-02); keyword search (R2-02).

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Documents/Processing/` | Extractor, status transitions, retry policy |
| `src/Convenciones.Api/Endpoints/DocumentEndpoints.cs` | Extraction trigger and status |
| `tests/…/DocumentProcessingTests.cs` | Real PDFs, a corrupt PDF, a non-PDF, a retry |

## Acceptance criteria

1. A PDF with text yields `Completed` and a `document_text` row whose `char_count = length(content)`.
2. A PDF with **no** extractable text is **not** reported as success — it is `Failed` with a reason, or
   `Completed` with an explicit "no text found" state. It must not claim text that does not exist.
3. A corrupt PDF yields `Failed`, `extraction_error` populated, `document_text` absent.
4. `platform.extraction_status` defines only `NotRequested, Requested, InProgress, Completed, Failed`.
   There is no `NotSupported` member, and none is needed: T4 rejects every non-PDF with 415, so an
   unsupported file has no record at all.
5. Status transitions are legal only: `Completed` cannot go back to `InProgress` without a new attempt,
   and `extraction_attempts` increments per attempt.
6. A retry after failure is permitted up to the configured limit and then refused with **409**.
7. Nothing claims "processed" for a document that was only stored.

## Predecessor note

The `extraction_status` mapping blocker recorded here has been **resolved** before this task starts:
EF Core 9+ requires `MapEnum` on the provider options, and Npgsql's default label translator had to be
replaced with a pass-through one. All five statuses are written and read back by
`ExtractionStatusMappingTests`, so this task can write statuses normally.
## Risks

- **This is the task where a false success is easiest.** The legacy system's failure mode was silent
  success; the tests must assert the *absence* of text as rigorously as its presence.
- PdfPig is a new dependency. It is managed and has no native requirement, but it must be pinned and
  recorded.
