# T6 — Document retrieval API

**Status:** planned · **Depends on:** T5 · **Blocks:** T7

## Objective

Return document status and extracted text to the caller who is authorised to see them — and to nobody
else.

## Scope

**In:** `GET /documents` (paged, company-scoped), `GET /documents/{id}` (metadata + status),
`GET /documents/{id}/text` (extracted text), `GET /documents/{id}/download` (short-lived signed or
equivalent), `DELETE /documents/{id}` (soft delete).

**Out:** search, versioning, bulk export.

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Api/Endpoints/DocumentEndpoints.cs` | Read and delete endpoints |
| `tests/…/DocumentRetrievalTests.cs` | Authorisation and content cases |

## Acceptance criteria

1. A user of company A requesting company B's document gets **404**, for every one of the endpoints.
2. The list endpoint returns only the caller's company's documents; the count is asserted, not eyeballed.
3. `GET /text` returns **404** (not 500, not empty body) when no text exists, and returns the text when it
   does.
4. Download never exposes the storage path or a permanent URL; the response is a redirect to a
   short-lived link or a streamed byte range.
5. A soft-deleted document is absent from the list and **404** on retrieval, and its audit trail survives.
6. Every response omits `storage_path`, `checksum` and `uploaded_by` internals.

## Risks

- Off-by-one in the "no text" case: an empty `document_text` row cannot exist (`content_not_blank`
  CHECK), so absence must be handled as a missing row, not an empty string.
