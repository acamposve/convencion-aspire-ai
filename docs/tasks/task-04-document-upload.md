# T4 — Document upload and private storage

**Status: implemented and verified — PR #16.** Branch `task/16-document-upload`.

## 0. The mandatory rule

> **Convenciones accepts PDF files only.** JPEG, PNG, Word, text, archives and every other format are
> rejected with **415 Unsupported Media Type**.

The format is decided by **parsing the bytes**. The `Content-Type` header, the filename and the extension are
attacker-controlled strings, recorded as metadata at most, never consulted for a decision. A `%PDF-`
signature is **not** sufficient: a file renamed to `.pdf` from JPEG bytes must be refused, and so must a file
that begins with `%PDF-` and then contains nothing parseable.

This supersedes the earlier criterion that accepted JPEG/PNG and marked them `NotSupported` for extraction.

---

## 1. Objective

Accept a contract document, prove it is a well-formed PDF, store it privately under a server-generated key,
attach it to the caller's company, and leave nothing behind when any step fails.

## 2. Scope

**In:** `IDocumentStorage` port with a local private-directory adapter; `POST /documents`; structural PDF
validation; size enforcement during the request; server-side storage keys; company derived from the session;
SHA-256 checksum; metadata persistence; compensating cleanup on failure.

**Out:** text extraction (T5), retrieval and download (T6), the browser screens (T7), **Supabase Storage**
(explicitly out of scope until the Product Owner approves a Supabase project — see T18 §2), OCR, virus
scanning, malware quarantine.

## 3. Validation pipeline

Cheapest check first. Nothing is written to storage, and no database row is created, until **every** check has
passed.

| # | Check | Failure |
|---|---|---|
| 1 | Request body within the transport limit | **413**, before the body is fully read |
| 2 | Exactly one `file` part present | **400** `validation_failed` |
| 3 | Streamed length ≤ 20,971,520 bytes | **413**, before anything is stored |
| 4 | Bytes parse as PDF: header, cross-reference table, at least one page | **415** if it parses as another format; **400** if it is a malformed PDF |
| 5 | Caller has an active company | **409** `invalid_state` |
| 6 | SHA-256 computed over the exact stored bytes | — |
| 7 | Store under a server-generated key | **500**, nothing persisted |
| 8 | Insert metadata row | **500**, stored object deleted |

Steps 3 and 4 stream: the handler copies into a counting stream that aborts at limit + 1 byte, and the
validator reads from the same buffered stream. **The upload is never fully buffered in memory**: ASP.NET's
memory buffer threshold stays small so bodies spill to a temporary file.

### Why parse rather than inspect a prefix

A signature check is a denylist in disguise: it rejects the files someone thought of. A parser accepts only
what the format actually permits. It is also the same parser T5 needs for extraction, so validation and
extraction cannot disagree about what a valid PDF is.

## 4. Size limits — exact numbers

| Layer | Setting | Value | Why |
|---|---|---|---|
| Business rule | maximum document size | **20,971,520 bytes (20 MiB)** | Matches `document_size_within_limit` exactly |
| Multipart | `FormOptions.MultipartBodyLengthLimit` | 22 MiB | 20 MiB plus headroom for part headers and boundaries |
| Transport | Kestrel `MaxRequestBodySize` | 22 MiB | Must be ≥ the multipart limit, or the transport rejects first with an unhelpful error |
| Buffering | `FormOptions.MemoryBufferThreshold` | 64 KiB | Keeps large uploads out of the managed heap |
| — | `MultipartHeadersLengthLimit` | 16 KiB | Framework default, kept explicit |

**Predictable 413.** Exceeding `MaxRequestBodySize` raises a transport-level error that never reaches the
endpoint. The API must translate it into the same problem body every other failure returns — `code`,
`correlationId`, no stack trace — rather than an empty connection reset. Exceeding the multipart limit raises
`InvalidDataException` inside the endpoint and must map to **413**, not 500.

## 5. Storage and consistency

- **Key:** `{companyId}/{yyyy}/{MM}/{documentId}.pdf`. Generated server-side from the session and a new
  document id. The client filename is stored in `file_name` as metadata, sanitised and length-capped, and is
  **never** part of a path.
- **`company_id`:** taken **only** from the caller's profile. A `company_id` in the body or query is ignored
  and must not influence anything; a test sends one and asserts it was ignored.
- **Order:** store the object, then insert the row; if the insert fails, delete the object. An orphaned object
  is invisible and sweepable; a row pointing at a missing object is a broken record that looks valid.
- **All-or-nothing:** a rejected upload leaves no object and no row. Tested by counting both after each failure
  case.
- **Private by construction:** the local adapter writes outside any web-served directory; nothing is served by
  path. Downloads are T6.

## 6. Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Documents/IDocumentStorage.cs` | The port; the local adapter implements it |
| `src/Convenciones.Documents/LocalDocumentStorage.cs` | Private directory, server keys, no public path |
| `src/Convenciones.Documents/PdfValidator.cs` | Structural validation via PdfPig |
| `src/Convenciones.Documents/Document.cs`, `DocumentConfiguration.cs` | Entity mapped to `platform.document` |
| `src/Convenciones.Api/Endpoints/DocumentEndpoints.cs` | `POST /documents` |
| `tests/…/DocumentUploadTests.cs` | The matrix in §8 |
| `tests/…/PdfSamples.cs` | PDF byte builders: valid, padded to an exact size, malformed, and other formats |

## 7. Acceptance criteria

1. A valid PDF is accepted with **201**, and the stored bytes are **byte-identical** to what was sent
   (SHA-256 compared).
2. A text file renamed `.pdf` is rejected with **415**.
3. **JPEG, PNG, DOC, DOCX, TXT and ZIP are each rejected with 415** — no format is special-cased as "stored but
   not processed".
4. A malformed PDF (truncated, broken cross-reference, no pages) is rejected with **400** and
   `code = validation_failed`.
5. An empty file is rejected with **400**.
6. A valid PDF of **exactly 20,971,520 bytes** is accepted.
7. A file of **20,971,521 bytes** is rejected with **413**, and no storage write occurs.
8. A request whose body exceeds the transport limit is rejected with **413** and a problem body, and no storage
   write occurs.
9. The storage key is server-generated and contains no client-supplied text; the stored filename is not used
   as a path.
10. A client-supplied `company_id` is **ignored**; the row belongs to the caller's company.
11. A user of company A cannot upload into company B, and the document they upload is invisible to company B.
12. Every failure case leaves **no storage object and no database row** (counted, not assumed).
13. A database failure after storage leaves **no orphaned object**.
14. The response never contains `storage_path` or a public URL.
15. `POST /documents` requires `document.write`; a caller without it gets **403** and nothing is stored.

## 8. Test matrix

| # | Input | Expected | Asserts |
|---|---|---|---|
| 1 | Valid PDF, ~50 KB | 201 | bytes identical, checksum recorded, row created |
| 2 | Valid PDF padded to exactly 20,971,520 B | 201 | boundary accepted |
| 3 | Same PDF, 20,971,521 B | 413 | no object, no row |
| 4 | Body larger than the transport limit | 413 | problem body, no object |
| 5 | UTF-8 text, filename `contrato.pdf` | 415 | no object, no row |
| 6 | JPEG bytes, filename `contrato.pdf`, `Content-Type: application/pdf` | 415 | header and filename ignored |
| 7 | PNG bytes | 415 | — |
| 8 | OLE (DOC) bytes | 415 | — |
| 9 | ZIP (DOCX) bytes | 415 | — |
| 10 | ZIP (plain archive) bytes | 415 | — |
| 11 | Empty file (0 bytes) | 400 | — |
| 12 | PDF truncated mid-body | 400 | code `validation_failed` |
| 13 | `%PDF-1.7` header followed by garbage, no pages | 400 | signature alone insufficient |
| 14 | Valid PDF, no `document.write` | 403 | nothing stored |
| 15 | Valid PDF, client sends another company's `company_id` | 201 | row uses the caller's company |
| 16 | Valid PDF uploaded by company A, read as company B | 404 | isolation holds |
| 17 | Storage write succeeds, database insert fails (forced) | 500 | object removed |

## 9. Documentation

- [api-contracts.md §4.8](api-contracts.md) — **updated**: PDF only, 415 for other formats, 413 for size, 400
  for a malformed PDF, signature not sufficient.
- `docs/tasks/task-04-document-upload.md` — this file.
- **UI (T7):** the upload control must state *"PDF only, up to 20 MiB"* and reject other extensions before
  sending, while the server remains authoritative. Added to T7's acceptance criteria.

## 10. Decisions needed before implementation

| # | Question | Recommendation |
|---|---|---|
| **D1** | Which PDF parser? | **PdfPig** (Apache-2.0, managed, no native dependency). Needed for structural validation now and reused for extraction in T5, so validation and extraction cannot disagree |
| **D2** | Should the database also refuse non-PDF rows? `document_content_type_allowed` currently permits `application/pdf`, `image/png` and `image/jpeg` | **Narrow it to `application/pdf`** in a forward migration. The API already rejects, but "the database is the last line of defence" is this project's own principle, and today a bug could write a PNG row the database would accept. Requires approval |
| **D3** | Password-protected PDFs are valid PDFs but yield no text without the password | **Reject with 400** and an explicit reason, so the user is told at upload rather than when extraction fails |

## 11. Risks

| Risk | Mitigation |
|---|---|
| **The client is trusted by accident** — one code path reads `file.ContentType` | A test sends JPEG bytes labelled `application/pdf` with a `.pdf` name; it must be 415 |
| **Oversized uploads buffered into memory** | Streaming with a counting stream; small memory threshold; 413 asserted before any write |
| **Orphaned objects accumulating** | Store-then-insert with compensating delete; a sweep job is out of scope and must be recorded as such |
| **A parser accepts malformed input** | PdfPig is configured to require at least one page, and malformed cases are in the matrix rather than assumed |
| **Zero-byte or header-only files slip through as "PDF"** | Explicit cases 11–13 |
| **Local adapter drifts from real storage semantics** | Labelled a development substitute, not the production path; T18 owns the real adapter |

## 12. What this task does not claim

Uploading is not processing. A stored PDF has `extraction_status = 'NotRequested'` until T5 runs, and the API
must not describe it as processed.


---

## 13. Implementation record

### Verified by execution

```
dotnet build Convenciones.slnx -> Compilacion correcta. 0 Advertencias, 0 Errores
dotnet test  Convenciones.slnx -> UnitTests         10/10
                               -> IntegrationTests  50/50   (17 new)
```

Covered: valid PDF accepted and stored byte-for-byte (checksum and file compared); a PDF of exactly
20,971,520 bytes accepted; 20,971,521 bytes rejected with 413 and nothing written; JPEG rejected with 415
both as `.jpg` and as `.pdf` claiming `application/pdf`; PNG, DOC, DOCX, TXT and ZIP each rejected with
415; text renamed to `.pdf` rejected; empty file rejected with 400; truncated PDF rejected with 400
`validation_failed`; a file whose only PDF-like content is the header rejected with 400; upload without
`document.write` rejected with 403 and nothing stored; a client-supplied `company_id` ignored (the row
belongs to the caller's company); two companies each uploading into their own company.

Every rejection case also counts storage objects and database rows before and after, so "rejected" and
"rejected without a side effect" are separate assertions.

### Resolved: the PostgreSQL enum mapping

The blocker is fixed. Two separate causes, found in order:

1. **EF sent the enum as an integer.** EF Core 9+ configures PostgreSQL enums on the provider options,
   not on a `NpgsqlDataSource`. The working form is
   `UseNpgsql(connectionString, npgsql => npgsql.MapEnum<ExtractionStatus>("extraction_status", "platform"))`.
   The data-source route leaves EF's built-in enum-to-integer conversion in place.
2. **Npgsql then translated the labels.** Its default translator converts `NotRequested` to
   `not_requested`, while the database defines PascalCase, producing
   `22P02: invalid input value for enum platform.extraction_status: "not_requested"`. Fixed with a
   pass-through `INpgsqlNameTranslator` (`ExactNameTranslator`): the schema is the authority, so the
   mapping adapts to it rather than the reverse.

Verified by `ExtractionStatusMappingTests`: all five statuses are written and read back, and a new
document starts as `NotRequested`.

### Database constraint (D2)

`20260103000000_document_pdf_only.sql` replaces `document_content_type_allowed` with
`document_content_type_pdf_only check (content_type = 'application/pdf')`.

- **Existing data inspected first:** `select content_type, count(*) from platform.document group by 1`
  returned **0 rows**, so nothing was modified or deleted.
- **No silent data loss:** if a database did hold a non-PDF row, the constraint would fail to apply
  rather than delete it. PostgreSQL validates existing rows, and the migration does not touch them.
- **Reversible:** the exact reverse statements are in the migration header, since the runner is
  forward-only.
- **Proven at the database:** `B9` (PNG rejected), `B11` (JPEG rejected), `B10` (`application/pdf`
  accepted).

### Final verification

```
14 migrations applied to an empty database  -> exit 0
constraint suite                            -> 35 PASS, 0 FAIL  (was 32; +B9, B10, B11)
RLS suite                                   -> 30 PASS, 0 FAIL
dotnet build                                -> 0 warnings, 0 errors
dotnet test                                 -> 10 unit + 52 integration, all passing
```