# T14 — Web: core domain, administration and public portal

**Status:** implemented · **Depends on:** T9, T11, T12, T13 · **Blocks:** — · **Stack:** React + Vite + MUI, **no Tailwind**

## 1. Objective

Complete the interface for the whole MVP: agreements and clauses, comparison, legal library, negotiation,
administration, audit, and the anonymous portal.

## 2. Scope

**In:** agreement and clause editor (numeric ordering, heading picker, publication state); comparison
workspace honouring adjacency and exclusion; statute/article editing; negotiation case screens with the
five stages; user/role administration; audit viewer; public portal pages (published agreements, news);
responsive and keyboard-accessible throughout (F-17).

**Out (unchanged):** clause alignment (R2-01), export (R2-04), worklists (R2-05/R2-06), bulk import
(R2-07). Also out of this increment: PDF upload UI changes (T7 owns them), creating news categories from
the screen (the API exists; the form lists them), and editing agreement annexes (the clause editor is the
T14 deliverable; annexes remain an API-only path until a screen is asked for).

## 3. Decisions

| # | Decision | Where it lives |
|---|---|---|
| 1 | Navigation and controls are filtered by the caller's capabilities, and the API stays the guard | `AppShell`, `useCapabilities`, ADR-028 |
| 2 | The comparison screen groups by heading and says "vecindad, sin alineación" — it never claims equivalence | `ComparisonPage`, ADR-028 |
| 3 | A published agreement is read-only in the editor; editing requires the explicit `Despublicar` action and its confirmation | `AgreementEditorPage` |
| 4 | The portal uses a separate tokenless client (`HttpPublicApi`), not the session client | `HttpPublicApi`, `PortalPage` |
| 5 | Every list pages; every form validates before calling the API | pages + `lib/forms.ts` |
| 6 | The session, never a token in storage, drives every authenticated screen | existing `SessionProvider` (T7) |

## 4. Screens

| Route | Screen | Capability that makes it visible |
|---|---|---|
| `/documents` | PDF workflow (T7) | `document.read` |
| `/agreements` · `/agreements/{id}` | List, create, editor with clauses and publication | `agreement.read` / `.write` / `.publish` |
| `/comparison` | Heading workspace: statute articles beside clauses | `comparison.read` |
| `/legal` · `/legal/{statuteId}` | Statutes and article editor | `statute.read` / `.write` |
| `/negotiation` · `/negotiation/{caseId}` | Cases, the five stages, petitions, meetings, promotion | `negotiation.read` / `.write` / `.promote` |
| `/news` | News administration: create, publish, unpublish | `news.read` / `.write` / `.publish` |
| `/admin/users` · `/admin/roles` | Users (block, roles) and the role × capability matrix | `user.manage` / `role.manage` |
| `/audit` | Audit trail with module and entity filters | `audit.read` |
| `/portal` | Anonymous portal: published agreements and news | none |

## 5. Implementation

```
src/Convenciones.Web/src/features/agreements/    AgreementsPage, AgreementEditorPage (+ tests)
src/Convenciones.Web/src/features/comparison/    ComparisonPage (+ tests)
src/Convenciones.Web/src/features/legal/         LegalLibraryPage, StatutePage
src/Convenciones.Web/src/features/negotiation/   NegotiationPage, NegotiationCasePage (+ tests)
src/Convenciones.Web/src/features/news/          NewsPage
src/Convenciones.Web/src/features/admin/         UsersPage, RolesPage, AuditPage (+ tests)
src/Convenciones.Web/src/features/public/        PortalPage (+ tests)
src/Convenciones.Web/src/features/auth/          useCapabilities
src/Convenciones.Web/src/lib/api.ts              every endpoint the screens use + HttpPublicApi
src/Convenciones.Web/src/lib/forms.ts            validation rules shared by the forms
```

**Capability gating.** `useCapabilities()` reads the `capabilities` array from `/me`, which the API
resolves from the database on every request. The shell hides sections, pages hide buttons, and the API
refuses anyway — the test suite asserts both halves (a hidden control, and a 403 surfaced when the call
is attempted).

**Published agreements.** The editor renders read-only when `state === 'Published'`: no add, edit or
delete clause controls, and an alert explaining why. `Despublicar` opens a confirmation dialog and, only
after it, the controls return. There is no "edit in place" path (AC 3).

**The comparison wording.** The subtitle, the alert and the empty states say *vecindad por materia, sin
alineación*; no element describes clauses as equivalent or aligned. A test asserts the disclaimer is
present and that no text matches `alinead[oa]`.

**The portal.** `/portal` is outside `RequireSession` and reads through `HttpPublicApi`, which has no
`AuthClient` and no token. A draft is not merely hidden client-side: the API returns 404 because `/public/*`
runs on the `anon` database role (T12). The test renders the portal with `token: null` and asserts the
authenticated client was never used.

**Rich text.** Clause and news bodies are rendered as HTML (`dangerouslySetInnerHTML`) because the product
stores formatted legal text. That is safe only because the server sanitises on write (A-33) and sanitises
again at the public boundary; the UI never composes HTML from user input itself.

## 6. Acceptance criteria

| AC | Evidence |
|---|---|
| 1. Every capability the API enforces is reflected in the UI | `AgreementsPage` hides "Nuevo acuerdo" and clause controls without `agreement.write`; `NegotiationPage` hides transitions without `negotiation.write` and promotion without `negotiation.promote`; `UsersPage` surfaces the API 403 when the list is refused |
| 2. The comparison view makes **no claim of alignment** | `ComparisonPage` disclaimer + test asserting no `alinead[oa]` text |
| 3. A published agreement cannot be edited without an explicit, confirmed unpublish | `AgreementEditorPage` + test `does not edit a published agreement in place…` |
| 4. The public portal renders with no session and no token in storage | `PortalPage` test with `FakeAuth({ token: null })` and `FakePublicApi`; no `getMe` call, no token |
| 5. Every list is paged and every form validates before submitting | pages use `Pagination` + `pageSize`; `lib/forms.ts`; the create-form test asserts the API is not called on invalid input |
| 6. Keyboard-only navigation completes author → publish → compare | `AgreementsPage` test `completes author → publish → compare with the keyboard alone` (sign-in and upload are covered by T7's suite) |

## 7. Test matrix

| Suite | Count | What it covers |
|---|---|---|
| `features/agreements/AgreementsPage.test` | 5 | List and states, capability hiding, create validation, the publish gate, the keyboard-only author→publish flow |
| `features/comparison/ComparisonPage.test` | 2 | Adjacency by heading, the no-alignment wording, the unpublished-request warning |
| `features/negotiation/NegotiationPage.test` | 5 | Legal transitions per stage, capability hiding, promotion only on a closed case, user blocking, the API refusal surfaced |
| `features/public/PortalPage.test` | 2 | No session, no token, agreement detail and the news tab |
| Existing suites (T7) | 44 | Login, documents, upload, accessibility, api/auth/validation units — all still green |

## 8. Risks

- **The interface can imply more than the data supports.** Comparison is adjacency by heading with 899
  unclassified clauses; the wording is the control, which is why it is asserted in a test.
- **Rendering stored HTML.** Safe only because of server-side sanitisation; a change that renders
  unsanitised text elsewhere would need its own guarantee.
- **The admin screens cover what the API offers.** Role and capability writes are not exposed because the
  API does not implement them yet (T13 recorded that deliberately).
- **No end-to-end browser test.** The suite renders the real application against fake boundaries; the
  wire format is checked by the API's integration tests, not by this one.

## 9. Implementation record

### Verified by execution

| Check | Result |
|---|---|
| `pnpm typecheck` (`tsc --noEmit`) | clean |
| `pnpm test` (Vitest) | **59 passed** (44 existing + 15 new), 12 files — twice, from the project directory |
| Production build (`tsc --noEmit` + `vite build` + `check-bundle.mjs`) | built; bundle scan passed (no service key, database URL or secret) |
| `dotnet build Convenciones.slnx` | 0 warnings, 0 errors (no server change in T14) |
| `.NET` suites | unchanged from T13: 64 unit + 154 integration |

### Findings

1. **A 1-second query timeout was too tight** for the heavier screens under parallel workers. The setup
   now configures a 5-second `asyncUtilTimeout` and the suite caps itself at four workers; both are
   test-infrastructure fixes for a slow wait, not product defects. With eight workers the shell's company
   lookup occasionally exceeded the timeout while twelve files rendered the full application at once.
2. **The fakes had to grow with the client.** `FakeApi` now implements every endpoint the screens use, so
   a screen that calls the wrong method fails at typecheck rather than in a browser.
3. **MUI selects are keyboard-operable** (focus, `Enter`, arrows, `Enter`), which is what made the
   keyboard-only test possible without special-casing the component.
4. **The first build put everything in one chunk.** The feature screens are now loaded on demand
   (`React.lazy` per route), which is why the build emits one chunk per section. The main chunk is still
   large because MUI itself is — a manual `manualChunks` split is a build-tuning decision, not a T14
   acceptance criterion, and it is recorded here rather than guessed at.

### Deviations

- **Annex editing has no screen yet.** The agreement editor covers clauses and publication, which is what
  the acceptance criteria name; annexes remain API-only and are recorded as future work.
- **News category creation has no screen.** The form lists categories; creating one is still an API call
  until the admin screens grow that control.
- **`/agreements/{id}/audit` is the general audit screen** filtered by entity, not a per-agreement tab.

## 10. Left undone

- **R2 features** (alignment, export, worklists, bulk import) — explicitly out of scope.
- **Annex and category screens**, as above.
- **End-to-end browser automation** against a running API — the repository has no Playwright harness;
  T18's deployment check is the first place a full stack runs together.
