# T7 — Web: authentication and the document workflow

**Status:** planned · **Depends on:** T6 · **Blocks:** T14 (shares the shell) · **Stack:** React + Vite + MUI, **no Tailwind**

## Objective

The first part of the MVP a person can actually use: sign in, upload a contract, watch processing, read
the extracted text.

## Scope

**In:** Vite app in `src/Convenciones.Web`; MUI theme (no Tailwind, ever); Supabase Auth sign-in;
token refresh and an authenticated API client; upload screen with drag-and-drop and progress; status
polling with honest states; text viewer; company indicator; signed-out and unauthorised screens;
keyboard-accessible forms and a responsive layout (F-17).

**Out:** agreement editing, comparison, admin (T14); the public portal (T14).

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Web/` | Vite + React + TypeScript + MUI application |
| `src/Convenciones.Web/src/features/documents/` | Upload, status, text view |
| `src/Convenciones.Web/src/lib/api.ts` | Authenticated client, error mapping |

## Acceptance criteria

1. Signing in with a valid account reaches the documents list; an invalid one shows the API's error code,
   not a raw message.
2. Uploading a PDF shows real progress, then the document appears with `Requested`.
3. Status reflects the API honestly — **a failure is displayed as a failure**, not as an empty result.
4. A `Failed` document shows the recorded error and offers a retry, which is disabled when the retry
   limit is exhausted.
5. An image upload shows "text extraction not supported", never "completed".
6. Every interactive control is reachable and operable by keyboard; the layout works at 360 px.
7. No credential, key or storage path is present in the browser bundle or in browser storage.

## Risks

- The temptation to handle errors optimistically — showing "done" while the API said `Failed`. The UI
  must render the API's status verbatim.
- Bundle leakage of the Supabase service key is easy to do by accident; an anon key is fine, a service
  key is a breach. A build-time check should fail if a service key string appears in the bundle.
