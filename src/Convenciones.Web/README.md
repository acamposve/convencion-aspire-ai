# Convenciones Web

The React single-page application for the document workflow: sign in, upload a PDF, process it, read
the extracted text.

**Stack:** React 19 + Vite 7 + TypeScript + Material UI (MUI) + Supabase Auth client. **No Tailwind,
ever** (constitution Art. III). Server state is fetched through a small typed client; there is no
state-management library.

## Screens

| Route | Screen |
|---|---|
| `/login` | Sign in (Supabase Auth, or a development token) |
| `/documents` | PDF upload, extraction and text viewer (T7) |
| `/agreements` · `/agreements/{id}` | Agreement list, creation and editor; clauses, heading picker, publication |
| `/comparison` | Clauses of one heading beside the statute articles of that heading |
| `/legal` · `/legal/{statuteId}` | Statutes and the article editor |
| `/negotiation` · `/negotiation/{caseId}` | Cases, the five stages, petitions, meetings and promotion |
| `/news` | News administration |
| `/admin/users` · `/admin/roles` | Users and the role × capability matrix |
| `/audit` | Audit trail with filters |
| `/portal` | The anonymous portal — no session, no token |

Navigation and controls are filtered by the caller's capabilities (`/me`), but the API is what enforces
them: a hidden control is a courtesy, never a permission. The published agreement editor is read-only
until it is explicitly unpublished, and the comparison screen says *vecindad, sin alineación* because the
backend does not assert equivalence.
## Run it

Prerequisites: Node.js 22 and pnpm. The API must be running (see the repository README / T18).

```bash
pnpm install
cp .env.example .env.local   # edit the values
pnpm dev                     # http://localhost:5173
```

| Variable | Meaning |
|---|---|
| `VITE_API_BASE_URL` | API origin, e.g. `http://localhost:5000`; empty means same origin |
| `VITE_SUPABASE_URL` | Supabase project URL (public) |
| `VITE_SUPABASE_ANON_KEY` | Supabase publishable/anon key (public by design) |

Only `VITE_`-prefixed values reach the browser. **Never put a `service_role` key here**; the build
fails if it finds one in the output. The API allows `http://localhost:5173` in Development
(`src/Convenciones.Api/appsettings.Development.json`); another origin must be added there or through
`Cors__AllowedOrigins__n`.

The API also needs its own JWT configuration (issuer, audience and either a local signing key or the
Supabase JWKS metadata). That is the API's concern, documented in T2/T18; the SPA never handles a
signing key.

## Authentication

`AuthClient` (`src/lib/auth.ts`) has two implementations:

- **`SupabaseAuthClient`** when `VITE_SUPABASE_URL` and `VITE_SUPABASE_ANON_KEY` are set. It uses
  `signInWithPassword` and `persistSession: false`, so the access and refresh tokens stay in memory.
  A reload signs the user out. That is deliberate: nothing token-shaped is written to
  `localStorage`/`sessionStorage` for an XSS to read.
- **`UnconfiguredAuthClient`** otherwise. It fails closed with the code `auth_not_configured`; the
  login screen says the Supabase project is missing instead of pretending to sign in. This is the
  repository's state until T18 provisions the project.

Signing in with Supabase is not enough: the SPA calls `GET /me`, and the API decides whether the
account is provisioned, active and allowed. An account with no profile is shown the unauthorised
screen with the API's code (`no_profile`, `account_inactive`, `forbidden`).

In development only, the login page also offers a **token field** for a locally minted access token.
It is kept in memory and still validated by the API; it is not a bypass.

## Document workflow

The API's extraction is synchronous (T5). The screen reflects that honestly:

1. Upload a PDF (client checks `.pdf`/`application/pdf` and 20 MiB, server remains authoritative).
2. The document appears with the API's real status, which after an upload is `NotRequested` —
   **not** "Requested", because nothing is queued.
3. Press **Procesar**. The request performs the extraction and returns the final state
   (`Completed` or `Failed`); the list is re-read rather than guessing.
4. **Ver texto** reads `GET /documents/{id}/text`. A 404 is shown as "no text", never as an empty
   panel. A `Failed` document shows its recorded reason and offers **Reintentar** until the API's
   three-attempt limit is reached.

The list polls every two seconds while a document reports `Requested` or `InProgress`, so the screen
will behave correctly if extraction becomes asynchronous. Today it usually sees the terminal state
immediately.

## Scripts

| Command | What it does |
|---|---|
| `pnpm dev` | Vite dev server on port 5173 |
| `pnpm build` | Typecheck, production build, then the bundle secret scan |
| `pnpm typecheck` | TypeScript, no emit |
| `pnpm test` | Vitest + Testing Library |
| `pnpm preview` | Serve the production build locally |

## Tests

44 tests across eight files: login (including the error-code, development-token and
no-browser-storage rules), document status/retry/text, upload validation and progress, keyboard and
360 px shell, file validation, the API client boundary, auth configuration and the bundle scanner
itself.

The tests replace only `AuthClient` and `ApiClient`; routing, `SessionProvider`, the app shell and every
screen are the production components. No Supabase project and no network access are required.

**Not claimed:** a real Supabase sign-in and a pixel-level browser check have not been executed,
because no Supabase project exists yet (T18). The jsdom tests prove behaviour and accessible roles,
not visual layout.

## Security notes

- Access and refresh tokens are memory-only; the app writes nothing to web storage.
- Only error **codes** and safe details reach the screen; provider messages and exception text are
  never rendered.
- `scripts/check-bundle.mjs` scans `dist` for `service_role`, service-role claim base64, database URLs
  and modern secret-key shapes. `pnpm build` fails on a hit.
- Downloads are not linked directly from the SPA: the API streams them after the capability check.

See [T7's implementation record](../../docs/tasks/task-07-web-documents.md) for the acceptance criteria
and the executed results.
