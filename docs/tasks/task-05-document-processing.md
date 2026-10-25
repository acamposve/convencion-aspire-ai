# T5 — Document processing and text extraction

**Status: implemented and verified.** Branch `task/18-document-processing`. The executed results are in
[§9](#9-implementation-record).

## 1. Objective

Read the text of a stored PDF, record the outcome honestly — including "the PDF has no text" and "the
bytes are damaged" — persist the text with a character count the database can verify, and bound retries
so a broken document cannot be retried forever.

## 2. Architecture decision: synchronous extraction, `200 OK`

The original design ([solution-architecture.md §7](../architecture/solution-architecture.md#7-asynchronous-processing),
ADR-018) specified `POST /documents/{id}/extract` → **`202 Accepted`** with an extraction id, followed by
the SPA polling `GET /documents/{id}/extraction`, because extraction was expected to be the first
genuinely asynchronous workload.

**The MVP implements extraction synchronously instead, and this is the approved deviation.** Extraction
runs in-process with PdfPig inside the request; by the time the handler returns, the document is either
`Completed` or `Failed`. Returning `202 Accepted` for work that has already finished would report a
pending state that no longer exists and force the caller to poll for a result the server already holds.

The extract endpoint therefore answers:

| Situation | Response |
|---|---|
| The attempt ran, whether it succeeded or failed | **`200 OK`** with the final state: `status`, `attempts`, `startedAt`, `completedAt`, `error`, `charCount` |
| Document already `Completed` | **`409 Conflict`** `invalid_state` |
| Three attempts already made | **`409 Conflict`** `invalid_state` |
| Caller lacks `document.write` | **`403 Forbidden`** `forbidden` |
| Document belongs to another company, or does not exist | **`404 Not Found`** `not_found` |

No queue, message broker or hosted worker is introduced. The `Requested` status label remains defined
for a future queue; a synchronous attempt moves directly from `NotRequested`/`Failed` to `InProgress`
and then to its terminal state. The 202 + polling design remains the recorded path for FUT-01, when
extraction becomes genuinely asynchronous (for example OCR at volume). T5 does not implement it.

## 3. Scope

**In:** PDF text extraction with PdfPig (Apache-2.0); bounded retries (`extraction_attempts`, maximum
three); `extraction_started_at`, `extraction_completed_at`, `extraction_error`; `document_text` population
with `char_count`; `POST /documents/{id}/extract` (synchronous, `200` with the final state);
`GET /documents/{id}/extraction` (state for the caller who may see the document).

**Out:** OCR for images; clause parsing or AI classification (FUT-01); keyword search (R2-02); an
asynchronous queue or hosted worker (§2); the extracted-text read endpoint and downloads (T6); the
browser screens (T7).

## 4. Extraction semantics

| Stored object | Result | Persisted state |
|---|---|---|
| PDF containing text | `Completed` | `document_text` row with the text; `char_count = length(content)`; timestamps and attempt count recorded |
| Valid PDF with no extractable text (e.g. a scan) | `Failed` with "contains no extractable text" | **No** `document_text` row; `extraction_error` populated |
| Corrupt, truncated or unreadable bytes | `Failed` with a readable reason | **No** `document_text` row; `extraction_error` populated |
| Storage object missing | `Failed` with "could not be found" | **No** `document_text` row; `extraction_error` populated |

**A failure never leaves text behind.** Every failed attempt deletes any `document_text` row from an
earlier attempt, including the missing-object path, so obsolete content cannot be returned by the status
endpoint or by T6's text read. A successful attempt replaces any existing row, so one document never has
two text rows.

**`char_count` is verified by the database.** The `document_text_char_count_matches` CHECK requires
`char_count = length(content)`, and PostgreSQL's `length(text)` counts code points. The application counts
runes for the same reason, so text outside the BMP cannot make a genuine success fail the constraint.

**Retries are bounded at three.** Each accepted request increments `extraction_attempts`; a request after
the third is refused with `409` without consuming an attempt. A refused re-extraction of a `Completed`
document does not consume one either.

## 5. Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Documents/PdfTextExtractor.cs` | Text extraction over the same parser T4 validates with; "no text" is a failure |
| `src/Convenciones.Documents/Document.cs` | Status transitions, attempts, timestamps and failure reason |
| `src/Convenciones.Documents/DocumentText.cs` | `platform.document_text` entity; `char_count` equal to the stored content length |
| `src/Convenciones.Api/Endpoints/DocumentExtractionEndpoints.cs` | `POST /documents/{id}/extract`, `GET /documents/{id}/extraction` |
| `tests/Convenciones.IntegrationTests/DocumentProcessingTests.cs` | The acceptance criteria below, over HTTP and against persisted state |
| `tests/Convenciones.IntegrationTests/PdfSamples.cs` | Deterministic text and blank PDFs, built in code |

## 6. Acceptance criteria

1. Extracting a PDF **with text** returns **`200 OK`**, persists the actual extracted text, records
   `Completed`, and the stored `char_count` equals both the content length and PostgreSQL's
   `length(content)`.
2. A **valid PDF with no extractable text** becomes **`Failed`**, records a meaningful
   `extraction_error`, and creates **no** `document_text` row.
3. **Corrupt stored PDF bytes** become **`Failed`**, record the failure reason, and create **no**
   `document_text` row.
4. **Re-extracting a `Completed` document** returns **`409`** `invalid_state`, leaves the row `Completed`
   and does not increment `extraction_attempts`.
5. **Attempts beyond the maximum of three** are rejected with **`409`** `invalid_state`;
   `extraction_attempts` stops at three.
6. A caller **without `document.write`** receives **`403`** `forbidden`, and the refused request does not
   consume an attempt or write text.
7. **A document belonging to another company** returns **`404`** `not_found` for both the extract and
   status endpoints. (`company.read_all` holders may cross the boundary, as everywhere else.)
8. **A failed retry removes stale extracted text**: after a successful extraction followed by a failed
   attempt, the `document_text` row is gone and the status endpoint reports `charCount = null`.
9. `platform.extraction_status` defines only `NotRequested, Requested, InProgress, Completed, Failed`.
   There is no `NotSupported` member: T4 rejects every non-PDF with 415, so an unsupported file has no
   record at all.
10. Status transitions are legal only: `Completed` cannot go back to `InProgress` without a new attempt,
    and `extraction_attempts` increments once per accepted attempt.
11. Nothing claims "processed" for a document that was only stored: upload leaves
    `NotRequested`, `extraction_attempts = 0` and no text row.
12. The endpoint responds **`200 OK`** with the final state, not `202`: §2 is the approved deviation and
    this criterion supersedes the original "202 + polling" wording.

## 7. Test matrix

| # | Test | Asserts |
|---|---|---|
| 1 | `Successful_extraction_persists_the_pdf_text_and_matching_char_count` | 200; `Completed`; text in the database contains the sentence drawn in the fixture; `char_count = content.Length = length(content)`; timestamps real; status endpoint agrees |
| 2 | `A_blank_pdf_is_failed_with_a_reason_and_no_text_row` | 200; `Failed`; reason contains "text"; no `document_text` row; status endpoint `charCount = null` |
| 3 | `Corrupt_stored_bytes_are_failed_and_the_reason_is_recorded` | 200; `Failed`; error stored; no `document_text` row |
| 4 | `Re_extracting_a_completed_document_returns_409` | 409 `invalid_state`; attempts still 1; exactly one text row |
| 5 | `Attempts_beyond_the_limit_are_rejected_with_409` | three attempts each 200/`Failed` with attempts 1–3; fourth 409; attempts still 3 |
| 6 | `Extraction_without_document_write_is_forbidden` | 403 `forbidden`; status `NotRequested`; attempts 0; no text row |
| 7 | `Another_companys_document_returns_404` | 404 `not_found` on extract and status; row untouched |
| 8 | `A_failed_retry_removes_stale_extracted_text` | after success then corrupt retry: 200/`Failed`; text row deleted; status `charCount = null` |
| 9 | `A_missing_stored_file_is_failed_and_removes_stale_text` | missing object after a success: 200/`Failed`; stale text deleted |
| 10 | `ExtractionStatusMappingTests` (T4, still green) | all five enum labels round-trip |

**Fixtures.** `PdfSamples.TextPdf()` and `PdfSamples.BlankPdf()` are built byte by byte in the test
assembly: object offsets, the cross-reference table and the content-stream length are computed from the
emitted bytes. No external file, download or network access is involved, so the tests are deterministic.
The text fixture draws one exact sentence (`PdfSamples.ExtractedText`) and the blank fixture fills a
rectangle with no text operators.

## 8. Risks

- **The false success is the failure this task exists to prevent.** Every failure case asserts the
  *absence* of a `document_text` row, not only the status string.
- Bounded retries are enforced in the application, not by a database constraint. Two concurrent extract
  requests for the same document can both pass the limit check; the MVP has no lock and no queue. The
  final text row is replaced, so the data stays consistent, but the attempt count can advance by two.
- A process crash between `InProgress` and the terminal state leaves the status at `InProgress`. There is
  no reaper in the MVP; an operator can requeue the document, which is what the tests simulate.
- PdfPig is pinned in `Directory.Packages.props`; it is managed, Apache-2.0 and has no native dependency.

## 9. Implementation record

### What the extraction path does

`POST /documents/{id}/extract` loads a document the caller may touch (tenant scope), refuses a
`Completed` document and an exhausted attempt limit with 409, records the attempt, reads the stored
object, extracts the text and writes the terminal state in one save. `GET
/documents/{id}/extraction` requires `document.read` and returns the same shape plus the stored
`char_count`.

Three defects in the pre-test WIP were found and fixed while making the criteria executable:

1. **`extracted_at` was never set.** The CLR default would have been written, storing year 1 instead of
   the extraction time. It is now set explicitly to the completion time and asserted.
2. **`char_count` counted UTF-16 units.** PostgreSQL's `length(text)` counts code points, so text outside
   the BMP would have failed the CHECK and turned a successful extraction into a 500. The count now uses
   runes.
3. **A missing storage object did not remove stale text.** The corrupt-pdf failure path deleted previous
   text, but the "object not found" path did not. Both now go through one deletion helper; the
   `A_missing_stored_file_…` test fails if that call is removed (checked by temporarily removing it:
   `1 failed, 0 passed`, then restored).

A successful attempt also deletes any existing text row before inserting, so one document cannot end up
with two rows when an operator requeues a completed document.

### Verified by execution

```
dotnet build Convenciones.slnx -> Compilación correcta. 0 Advertencia(s), 0 Errores
dotnet test  Convenciones.slnx -> UnitTests         10/10
                               -> IntegrationTests  61/61   (9 new DocumentProcessingTests)
```

The processing run:

```
dotnet test tests/Convenciones.IntegrationTests --filter FullyQualifiedName~DocumentProcessingTests
  -> Correctas! … Superado: 9, Total: 9
```

## 10. Left undone

- The asynchronous design (`202` + polling) is **not** implemented; this is the approved deviation in §2,
  not an omission.
- OCR, clause parsing and classification (FUT-01) are out of scope.
- The extracted text is not exposed to clients yet; `GET /documents/{id}/text` is T6.
- No lock serialises concurrent extract requests, and no process reaps a document stuck at `InProgress`
  after a crash (§8).
