# T4 — Document upload and private storage

**Status:** planned · **Depends on:** T3 · **Blocks:** T5, T6, T7

## Objective

Accept a contract document, prove what it actually is, store it privately with a server-controlled key,
and persist its metadata against the caller's company.

## Scope

**In:** `IDocumentStorage` abstraction; local private-directory adapter for development and tests;
`POST /documents` (multipart); byte-level format sniffing; 20 MiB limit; server-generated storage key;
`company_id` required and taken **from the caller's profile**, never from the request; metadata persisted
with `extraction_status = Requested`.

**Out:** Supabase Storage adapter (needs your approval), extraction (T5), retrieval (T6), UI (T7).

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Documents/` | Storage abstraction, local adapter, format sniffer, upload command |
| `src/Convenciones.Api/Endpoints/DocumentEndpoints.cs` | `POST /documents` |
| `tests/…/DocumentUploadTests.cs` | Format, size, tenancy and failure cases |

## Acceptance criteria

1. A real PDF is accepted; the stored file is byte-identical to what was sent (checksum compared).
2. A text file renamed `.pdf` is rejected with **415** — the check reads the bytes, never the declared
   content type or extension.
3. A JPEG/PNG is accepted and marked `NotSupported` for extraction — never as processed.
4. A 21 MiB upload returns **413** before the bytes are written to storage.
5. The client-supplied filename never becomes the storage path; it is metadata only.
6. `company_id` in the request body, if sent, is **ignored** — the caller's own company is used.
7. A failed upload leaves **no** storage object and **no** database row.

## Risks

- A local-directory adapter can drift from Supabase Storage semantics (immutability, signed URLs). It
  must be labelled a development substitute, not the production path.
- Path traversal via filename metadata if it is ever used in a path. It must not be, and a test should
  prove it.
