# T6 — Document retrieval API

**Status: implemented and verified — PR #19.** Branch `task/19-document-retrieval`. Executed results
are in [§9](#9-implementation-record).

## 1. Objective

Return a document's metadata, extraction state, extracted text and bytes to the caller who is
authorised to see them — and to nobody else — and remove a document from every read path without
destroying its audit history.

## 2. Scope

**In:** `GET /documents` (paged, company-scoped), `GET /documents/{id}` (metadata + extraction state +
`rowVersion`), `GET /documents/{id}/text` (extracted text), `GET /documents/{id}/download` (streamed
bytes with range support), `DELETE /documents/{id}` (soft delete + storage removal, `If-Match`
required).

**Out:** search and filtering, versioning, bulk export, signed storage URLs (Supabase Storage is T18;
the local adapter is the MVP path), the web screens (T7).

## 3. Endpoints

| Method | Path | Capability | Success | Failure |
|---|---|---|---|---|
| `GET` | `/documents` | `document.read` | **200** `{ items, page, pageSize, total }` | 403 without the capability |
| `GET` | `/documents/{id}` | `document.read` | **200** metadata + extraction state + `rowVersion` | **404** not visible |
| `GET` | `/documents/{id}/text` | `document.read` | **200** `{ documentId, content, charCount, extractedAt }` | **404** not visible, or no text row |
| `GET` | `/documents/{id}/download` | `document.read` | **200** (or **206** for a range) streamed bytes | **404** not visible, or bytes missing |
| `DELETE` | `/documents/{id}` | `document.write` | **204** | **400** no `If-Match`, **409** stale `rowVersion`, **403** without the capability, **404** not visible |

### 3.1 Listing

Newest first (`uploaded_at DESC, id DESC`), offset paging (`?page=1&pageSize=25`); `pageSize` is capped
at 100 and invalid values fall back to the default. The response follows the shape `/companies` already
uses — `items`, `page`, `pageSize`, `total` — rather than introducing a second envelope for the same
concept.

The company filter is part of the SQL query, so `total` is the number of rows the caller may see, not
the number of rows that exist. Two edge cases are deliberate:

- A caller with no company and without `company.read_all` gets an **empty page**, never an unfiltered
  one. "No company" must not mean "all companies".
- `company.read_all` widens the list to every **attributed** document. A document with no company is
  visible to nobody through the API, including `read_all` (T1's rule), so those rows are excluded.

Soft-deleted rows are excluded by a global query filter on `Document`, so no read path — list,
metadata, text, download, extraction — can forget them.

### 3.2 Metadata, text and download

`GET /documents/{id}` returns `id`, `fileName`, `contentType`, `sizeBytes`, extraction status,
attempts, timestamps, error, `uploadedAt` and `rowVersion`. It deliberately omits `storage_path`,
`checksum` and `uploaded_by`: the storage key is internal, the checksum is not part of the retrieval
contract, and the uploader is not needed by the documents screen.

`GET /documents/{id}/text` returns the stored text with its `charCount` and `extractedAt`. **No text is
404 `not_found`**, not an empty body and not a 500: absence is a missing `document_text` row, which the
database guarantees cannot be blank.

`GET /documents/{id}/download` streams the bytes after the tenant check, with
`Content-Disposition: attachment`, `Cache-Control: private, no-store` and range processing enabled
(`206` + `Content-Range` for a `Range` request). The response never contains the storage key, a
permanent URL, or a `Location` header: the private directory is not reachable by path, and no signed URL
exists while the local adapter is in use.

### 3.3 Soft delete

`DELETE` requires `If-Match` with the current `rowVersion`, like every other DELETE in
[api-contracts §5](../architecture/api-contracts.md#5-concurrency). The document is located **before**
the header is read, so another company's document is 404 even when `If-Match` is missing — a 400 there
would confirm the document exists.

Order matters: the row is marked `deleted_at` and saved first, then the object is removed. A failed
object removal therefore leaves an invisible, private orphan (sweepable), never a visible row pointing
at missing bytes. The row, its extraction state and its audit trail remain; every API read returns 404
afterwards. The extracted text row is retained with the document for history but is unreachable through
the API.

## 4. Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Api/Endpoints/DocumentEndpoints.cs` | List, metadata, text, download and delete endpoints |
| `src/Convenciones.Api/Endpoints/DocumentAccess.cs` | The single tenant check every document endpoint uses |
| `src/Convenciones.Documents/Document.cs` | `UploadedAt`, `DeletedAt`, `RowVersion`, `SoftDelete` |
| `src/Convenciones.Documents/DocumentConfiguration.cs` | Column maps, `xmin` concurrency token, soft-delete query filter |
| `tests/Convenciones.IntegrationTests/DocumentRetrievalTests.cs` | The acceptance criteria below, over HTTP and against persisted state |
| `tests/Convenciones.IntegrationTests/DocumentsFixture.cs` | Adds a read-only user with no company, to prove the list fails closed |

## 5. Acceptance criteria

1. A user of company A requesting company B's document gets **404**, for **every** endpoint: metadata,
   text, download and delete (and the document is absent from the list).
2. The list endpoint returns only the caller's company's documents, and `total` equals the number of
   active rows that company owns — asserted against SQL, not eyeballed. A caller holding
   `company.read_all` sees every **attributed** document; a caller with no company sees none.
3. `GET /text` returns **404** `not_found` when no text exists (not extracted, or extraction failed)
   and returns the exact stored content when it does.
4. Download returns the stored bytes verbatim, supports a byte range (`206`), and never exposes the
   storage path or a permanent URL; the response is streamed, not redirected.
5. A soft-deleted document is absent from the list and **404** on metadata, text and download; its row
   records `deleted_at`, its stored object is removed, and its audit trail survives — including an
   audit row for the soft delete itself.
6. Retrieval responses omit `storage_path`, `checksum` and `uploaded_by`.
7. `DELETE` requires `If-Match`: missing → **400** `validation_failed`, stale → **409**
   `concurrent_modification`, current → **204**, with nothing changed on either refusal.
8. A caller without `document.write` gets **403** on delete and the document is untouched; a caller
   without `document.read` is refused by the capability middleware.
9. A caller with `document.read` but no company sees **zero** documents and gets 404 for a specific id.
10. Deleting an already deleted document returns **404**, not a second mutation and not a 500.
11. Extraction cannot be triggered on a soft-deleted document: it is invisible before the attempt
    check, so it returns 404.

## 6. Test matrix

`tests/Convenciones.IntegrationTests/DocumentRetrievalTests.cs`, 13 tests:

| # | Test | Asserts |
|---|---|---|
| 1 | `List_returns_only_the_callers_company_and_the_real_total` | `total = count(SQL)` for the caller's company; every item belongs to it; another company's document absent; metadata fields populated; no internal fields in the JSON |
| 2 | `List_paginates_without_duplicating_or_skipping_the_new_documents` | page size and page number honoured; pages disjoint; the three new documents appear within the first two pages |
| 3 | `Metadata_returns_the_document_and_omits_internal_fields` | fields and `rowVersion` correct; `uploadedAt` matches the row; no `storage_path`, checksum or uploader |
| 4 | `Text_returns_the_extracted_content_when_it_exists` | content equals the database row; `charCount` equals the content length; real timestamp; no internals |
| 5 | `Text_returns_404_when_no_text_exists` | not-extracted and failed documents both 404 `not_found` |
| 6 | `Download_returns_the_stored_bytes_and_never_a_path` | byte-identical body; attachment disposition; private/no-store; no `Location`; `206` + correct prefix for a range; no path or checksum in headers |
| 7 | `Every_endpoint_reports_another_companys_document_as_not_found` | 404 `not_found` on metadata, text, download and delete (no `If-Match`), absent from B's list, still readable by the owner |
| 8 | `Delete_soft_deletes_removes_the_object_and_keeps_the_audit_trail` | 204; `deleted_at` set; object gone; all reads, extraction and the list 404/absent; audit rows grow with a `DeletedAt` change and the actor; second delete 404 without another audit row |
| 9 | `Delete_requires_the_current_row_version` | missing header 400, stale version 409, correct version 204; nothing changed before success |
| 10 | `Delete_without_document_write_is_forbidden_and_changes_nothing` | 403; readable by the same user; row and object untouched |
| 11 | `A_caller_without_a_company_sees_no_documents` | empty list and `total = 0`; specific id 404 |
| 12 | `Retrieval_without_document_read_is_forbidden` | list, metadata, text and download all 403 `forbidden` |
| 13 | `Cross_company_capability_lists_every_attributed_document` | an administrator sees documents from both companies; `total` equals the SQL count of attributed, non-deleted rows |

## 7. Risks

- **Offset pagination** degrades on large tables and can skip rows when new documents arrive between
  pages. Documents grow per company but not to the scale that earned keyset paging for clauses; the
  contract's keyset strategy remains available if that changes.
- **The retention choice is explicit:** soft delete keeps the row and extracted text. The bytes are
  removed and every API read is 404, but a database operator can still read the text. If deletion must
  destroy the text too, that is a policy decision and a small follow-up, not something this task
  silently assumed.
- **Object removal is best effort after the row is hidden.** A failure is logged, the document stays
  invisible, and an orphan remains for a sweep job that does not exist yet.
- **`If-Match` uses `xmin`**, which is coarse: any concurrent update to the row invalidates a delete,
  even one that changed an unrelated column. That is the same trade-off T3 accepted and the contract
  treats `rowVersion` as opaque.

## 8. Documentation

- [api-contracts.md §4.8](../architecture/api-contracts.md#48-documents-and-audit) — updated with the
  retrieval contract, the streamed download, the list shape and the soft-delete semantics.
- This file records what was executed, including the tests' scope.

## 9. Implementation record

### Changes needed to make the criteria executable

- `Document` did not map `uploaded_at`, `deleted_at` or `xmin`. All three are now mapped; the upload
  path sets `uploaded_at` (and `created_at`, which previously stored the CLR default) from one
  timestamp.
- Soft deletes are hidden by a global query filter, so the T5 extraction endpoints and the new read
  paths share one rule instead of four independent `deleted_at IS NULL` clauses.
- The tenant lookup was a private copy inside the extraction endpoints. It moved to
  `DocumentAccess.FindAccessibleAsync` and every document endpoint now uses it.
- `GET /documents` does not use `TenantScope.VisibleCompanyId`: that helper returns `null` both for
  `company.read_all` and for "no company", which would have turned a company-less account into an
  unfiltered list. The endpoint handles the two cases separately and fails closed.

### Verified by execution

```
dotnet build Convenciones.slnx -> Compilación correcta. 0 Advertencia(s), 0 Errores
dotnet test  Convenciones.slnx -> UnitTests         10/10
                               -> IntegrationTests  74/74   (13 new DocumentRetrievalTests)
```

The retrieval run:

```
dotnet test tests/Convenciones.IntegrationTests --filter FullyQualifiedName~DocumentRetrievalTests
  -> Correctas! … Superado: 13, Total: 13
```

## 10. Left undone

- Search, filtering, sorting options and versioning are out of scope.
- `GET /audit` (T13) is not part of this task; the rows written here are readable by that endpoint.
- Signed, short-lived download URLs belong to the Supabase Storage adapter (T18). Until then the API
  streams the bytes itself.
- No sweeper removes orphaned objects left by a failed storage delete.
- The web documents screen (T7) is the first consumer and is not part of this task.
