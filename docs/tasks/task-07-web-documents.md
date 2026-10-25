# T7 — Web: authentication and the document workflow

**Status: implemented and verified — PR [#20](https://github.com/acamposve/convencion-aspire-ai/pull/20).**
Branch `task/20-web-documents`. Executed results are in [§9](#9-implementation-record).

## 1. Objective

The first part of the MVP a person can actually use: sign in, upload a contract, watch its processing
state, and read the extracted text.

## 2. Scope

**In:** Vite app in `src/Convenciones.Web`; MUI theme (no Tailwind); Supabase Auth adapter with an
in-memory session; authenticated API client with error mapping; upload with real progress and the
PDF-only/20 MiB rule stated on the control; honest status display; explicit processing and bounded
retry; extracted-text viewer; company indicator; signed-out and unauthorised screens;
keyboard-accessible controls; a compact layout for a 360 px viewport; a bundle secret scan.

**Out:** agreement editing, comparison, administration and the public portal (T14); the download
button; a generated OpenAPI client; provisioning Supabase (T18); deployment (T18).

## 3. Authentication

The app talks to two boundaries, both replaceable in tests:

| Boundary | Real implementation | Test implementation |
|---|---|---|
| `AuthClient` | `SupabaseAuthClient` (`signInWithPassword`) | `FakeAuth` |
| `ApiClient` | `HttpApiClient` (`fetch` + XHR for upload progress) | `FakeApi` |

**The Supabase project does not exist yet (T18).** The app therefore does not fake a login:

- With `VITE_SUPABASE_URL` and `VITE_SUPABASE_ANON_KEY`, the real Supabase adapter is used.
- Without them, `UnconfiguredAuthClient` fails closed with the code **`auth_not_configured`** and the
  login screen says the project is missing.
- In development only, a token minted elsewhere can be pasted. It is held in memory, still validated
  by the API, and never written to browser storage.

The Supabase client is created with **`persistSession: false`**. The access and refresh tokens live in
memory; a page reload signs the user out. This is the security model's recommended trade-off (A-34):
nothing token-shaped is available to an XSS through `localStorage` or `sessionStorage`.

Signing in is only half the decision. The SPA then calls `GET /me`; the API resolves the account,
company and capabilities. A token for an unprovisioned account is shown the **unauthorised screen**
with the API's code (`no_profile`, `account_inactive`, `forbidden`).

## 4. Document workflow — adapted to the synchronous API

T5 made extraction synchronous and T4/T5 define an upload as `NotRequested`. The original T7 wording
("then the document appears with `Requested`") described the discarded 202 + queue design, so the
screen reflects the API that exists:

1. **Upload.** The drop zone states "PDF únicamente · máximo 20 MiB" on the control itself. The client
   rejects a wrong extension/type, an empty file and a file over 20 MiB before sending; the server
   remains authoritative and its code is displayed (`unsupported_media_type`, `payload_too_large`,
   `validation_failed`).
2. **Status.** The document appears with the status the API returned — after an upload,
   `NotRequested` (shown as "Pendiente de procesar"), never an invented `Requested`.
3. **Process.** The user presses **Procesar**. The request performs the extraction and returns the
   final state; the row is re-read from the API rather than inferred. While the request is in flight
   the button shows a spinner and "Procesando…".
4. **Failure.** A `Failed` document always shows the recorded `extractionError` verbatim and offers
   **Reintentar**. The button is disabled at three attempts with the limit stated.
5. **Text.** **Ver texto** reads `GET /documents/{id}/text`. Content is shown in a scrollable,
   keyboard-focusable region. A 404 `not_found` is displayed as "no text extracted", never as an
   empty panel.

If a document ever reports `Requested` or `InProgress`, the list polls every two seconds, so the same
screen works when extraction becomes asynchronous. Today the terminal state is usually already present.

## 5. Screens and routes

| Route | Screen | Notes |
|---|---|---|
| `/login` | Sign in | Email/password (when configured), `auth_not_configured` warning, development token section |
| `/documents` | Documents | Upload, company indicator, status, process/retry, text dialog |
| `/` | Redirect | → `/documents` |
| `*` | Redirect | → `/` |

The shell is an MUI `AppBar` with the company indicator (name from `GET /companies/{id}`, falling back
to the id), an account menu, a skip link and a footer. The document list is a vertical stack of cards,
not a fixed-width table, so a 360 px viewport has no horizontal scroll.

## 6. Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Web/package.json`, `vite.config.ts`, `tsconfig.json` | Vite + React + TypeScript + MUI project |
| `src/Convenciones.Web/src/lib/auth.ts` | `AuthClient`, Supabase adapter, fail-closed unconfigured adapter |
| `src/Convenciones.Web/src/lib/api.ts` | Typed API client, XHR upload progress, problem+json → `ApiError` |
| `src/Convenciones.Web/src/lib/errors.ts` | Codes and safe details; provider messages are never surfaced |
| `src/Convenciones.Web/src/features/auth/` | Session provider, login, unauthorised screen, route guard |
| `src/Convenciones.Web/src/features/documents/` | Upload, list, status, retry, text dialog |
| `src/Convenciones.Web/src/components/` | Shell, status chip, error notice, loading screen |
| `src/Convenciones.Web/scripts/check-bundle.mjs` | Build-time secret scan |
| `src/Convenciones.Web/src/test/` | Fakes, render helper, matchMedia harness |
| `src/Convenciones.Api/appsettings.Development.json` | Allows the Vite origin `http://localhost:5173` locally |
| `src/Convenciones.Web/README.md` | Setup, configuration, security notes and limitations |

## 7. Acceptance criteria

1. A valid account reaches the documents list; an invalid one shows a **code**
   (`invalid_credentials`, `auth_not_configured`, `no_profile`, …), never a raw provider message.
2. Uploading a PDF shows **real progress**, then the document appears with the API's actual status
   (`NotRequested` after an upload) and can be processed. *(Supersedes "appears with `Requested`",
   which described the discarded asynchronous design.)*
3. Status is rendered honestly: `Failed` is shown as **Fallido** with its reason, never as an empty or
   successful result.
4. A `Failed` document shows the recorded error and offers retry; retry is disabled at the API's
   three-attempt limit, and the limit is stated.
5. **PDF only, up to 20 MiB is stated on the upload control itself**; another format is rejected
   client-side by extension and (where exposed) type; the server's **415** `unsupported_media_type`
   is displayed verbatim.
6. A file over 20 MiB is rejected client-side with the limit stated; the server's **413**
   `payload_too_large` is displayed verbatim if the client check is bypassed.
7. Every interactive control is reachable and operable by keyboard; the skip link reaches the main
   content; the shell switches to its compact layout at 360 px.
8. No credential, key, token or storage path is present in the browser bundle or in browser storage:
   tokens are memory-only, no `localStorage`/`sessionStorage` write occurs, and the build fails if a
   service-role marker reaches `dist`.
9. The text viewer shows the extracted content, or an explicit `not_found` state when there is none.
10. Signing out returns to `/login`; an unauthenticated visit to `/documents` is redirected.

## 8. Test matrix

`pnpm test` runs **44 tests in eight files**. Only `AuthClient` and `ApiClient` are replaced; the
providers, routing, shell and screens are the production components.

| File | Tests | Covers |
|---|---|---|
| `src/features/auth/LoginPage.test.tsx` | 8 | valid sign-in → list; invalid code; `no_profile` → unauthorised; `auth_not_configured`; development token; anonymous redirect; sign-out; keyboard-only sign-in; empty web storage |
| `src/features/documents/DocumentsPage.test.tsx` | 7 | status truthfulness; retry; exhausted limit; process → text; text 404; processing 409 code; polling on `InProgress` |
| `src/features/documents/UploadControl.test.tsx` | 7 | PDF/20 MiB text on control; non-PDF and wrong-type rejection; oversize rejection; progress bar; API 415; API 413 |
| `src/features/documents/Accessibility.test.tsx` | 4 | skip link; keyboard file picker; compact layout at 360 px; wide layout at desktop |
| `src/lib/validation.test.ts` | 6 | extension, declared type, empty, exact 20 MiB and +1 byte |
| `src/lib/auth.test.ts` | 3 | fail-closed when unconfigured; Supabase adapter when configured; single-value misconfiguration |
| `src/lib/api.test.ts` | 4 | bearer header and parse; problem+json → code/status; no token refused; pagination query |
| `scripts/check-bundle.test.ts` | 5 | service-role markers, base64 claim, database URL, secret-key shape, clean public config, library prefix false positive |

## 9. Implementation record

### What was built and what changed

- The app is a new Vite/React/TypeScript project. MUI is the only styling system; there is no
  Tailwind, no CSS framework and no external font request.
- **The list projection gained `extractionError`** (T6 follow-up) so the documents screen can show why
  a document failed without a request per row. The T6 integration test now asserts it.
- The API's **Development** configuration allows `http://localhost:5173`; production CORS remains a
  T18 decision.
- Upload progress is real: `XMLHttpRequest.upload.onprogress`, not a timer. A test drives a deferred
  upload to assert the bar at 37 % before completion.
- The bundle scanner initially failed on the Supabase client's own `startsWith("sb_secret_")`
  validation helper. The marker was narrowed to a real key shape (`sb_secret_` plus 20+ characters),
  with a test proving both the true positive and the library false positive. Without that check the
  guard would either fail every build or be weakened without evidence.

### Verification, executed

```
dotnet build Convenciones.slnx -> Compilación correcta. 0 advertencias, 0 errores
dotnet test  Convenciones.slnx -> UnitTests         10/10
                               -> IntegrationTests  74/74

cd src/Convenciones.Web
pnpm build  -> tsc --noEmit: clean
            -> vite build: 617 modules, dist built
            -> bundle scan: passed (no service key, database URL or secret marker)
pnpm test   -> Test Files 8 passed (8)
            -> Tests     44 passed (44)
```

Total automated tests in the repository at this commit: **84 .NET + 44 web = 128**, 0 failed.

### What was not executed

- **No real Supabase sign-in.** There is no project (T18). The adapter is written against
  `@supabase/supabase-js` but its network behaviour is unproven; the tested path is the fail-closed
  unconfigured state and the fake boundary.
- **No real-browser layout or visual check.** The tests use jsdom; the 360 px behaviour is asserted
  through the shell's `data-layout="compact"` switch and MUI breakpoints, not measured pixels.
- **No generated OpenAPI client.** A-30 recommends generation; T7 hand-writes the types in
  `lib/api.ts`. That is a known drift risk, recorded in §11.
- **No download or delete UI.** T6 exposes both over HTTP; T7's acceptance criteria do not require
  them and the documents screen does not offer them.

## 10. Risks

- **Bundle size:** the production JS is ~758 kB (225 kB gzip), dominated by MUI and the Supabase
  client. Vite warns about a chunk over 500 kB. Code splitting is a follow-up, not done here.
- **Type drift:** the API types are hand-written; a change in a DTO is caught only by tests that
  exercise the field. Generating from OpenAPI closes this, and is the recorded plan (A-30).
- **Auth is the least proven boundary.** Supabase configuration, token refresh against a real
  issuer, blocked/expired accounts and the actual redirect behaviour must be verified in T18.
- **Synchronous extraction means the UI cannot show `Requested`**, which is why the acceptance
  criterion was amended rather than the screen lying about a queue.

## 11. Left undone / for T14 or T18

- Real Supabase project, sign-in against GoTrue, and CORS for the deployed origin (T18).
- Generated TypeScript client from the API's OpenAPI document (A-30).
- Code splitting to reduce the initial bundle.
- Authenticated download and delete UI; documents search/filtering.
- Visual regression or full-browser end-to-end tests.
- The remaining screens: agreements, comparison, negotiation, administration, public portal (T14).
