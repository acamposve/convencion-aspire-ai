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
4. The `NotSupported` status is retained in the enum but is **unreachable from the upload path**: T4 accepts
   PDF only and rejects everything else with 415, so no image ever reaches extraction. The status remains
   for documents that arrive through another route (a statute PDF attached later, for example), and the
   test asserts that no upload can produce it.
5. Status transitions are legal only: `Completed` cannot go back to `InProgress` without a new attempt,
   and `extraction_attempts` increments per attempt.
6. A retry after failure is permitted up to the configured limit and then refused with **409**.
7. Nothing claims "processed" for a document that was only stored.

## Risks

- **This is the task where a false success is easiest.** The legacy system's failure mode was silent
  success; the tests must assert the *absence* of text as rigorously as its presence.
- PdfPig is a new dependency. It is managed and has no native requirement, but it must be pinned and
  recorded.
