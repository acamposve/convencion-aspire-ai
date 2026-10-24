# Decisions — Convenciones

**Status: proposed for approval.** Section 2 is the list I need you to sign off; everything in
section 3 is recorded with rationale so it can be challenged rather than rediscovered.

Documents: [mvp-scope.md](mvp-scope.md) · [solution-architecture.md](solution-architecture.md) ·
[domain-model.md](domain-model.md) · [api-contracts.md](api-contracts.md) · [security-model.md](security-model.md)

---

## 1. How to read this

- **Section 2 — decisions requiring approval.** Marked `[A-nn]` throughout the design documents.
  Each has my recommendation and what changes if you reject it.
- **Section 3 — accepted decisions (ADRs).** Choices I judged determined by the approved evidence.
  Recorded so they are reviewable, not so they are hidden.
- **Section 4 — resolutions of the Phase 1 open questions.** Traceability: every question either got
  an answer, became an approval item, or was explicitly deferred.

---

## 2. Decisions requiring approval

### 2.1 Product scope

| ID | Decision | Recommendation | If you reject it |
|---|---|---|---|
| **A-01** | Five personas: legal editor, analyst, negotiator, administrator, public reader | Accept — derived from legacy work roles, not from their names | Roles and screens must be re-derived; blocks A-36 |
| **A-02** | Negotiation workflow stays in MVP as a thin slice after the W1 spine | Accept (matches your Task 1 decision) | Leaner MVP: defer to R2, faster first demo |
| **A-03** | `NegotiationCase` states: `Draft → Open → Closed → Promoted` (+ reopen from Closed) | Accept | Legacy states are ambiguous and unvalidated by data (no case ever closed) |
| **A-04** | **Single-tenant** MVP; company-level scoping for negotiators only | Accept — legacy has no tenancy dimension at all | Multi-tenancy is a schema-wide change; must be decided now, not later |
| **A-41** | Publication records actor + timestamp; **no approval workflow**; published agreements remain editable (audited) | Accept | An approval gate adds a second state dimension and a new screen |

### 2.2 Architecture

| ID | Decision | Recommendation | If you reject it |
|---|---|---|---|
| **A-05** | Six business modules (Catalog, Agreements, Negotiation, LegalLibrary, Comparison, Publishing) + three platform modules | Accept | Folding Comparison+Publishing into Agreements gives 4 modules, fatter Agreements |
| **A-06** | Two projects per business module (Domain+Application, Infrastructure), not three | Accept | Three per module = 18 projects; ceremony without benefit at this size |
| **A-07** | **One `DbContext`, one `public` schema**; boundaries by tests and review, not the compiler | Accept — cross-module atomicity (promotion) requires it | Schema-per-module + 6 migration histories + a shared-transaction workaround |
| **A-08** | Frontend feature folders mirror backend modules one-for-one | Accept | A different convention costs navigation time on every change |
| **A-09** | Two frontend dependencies beyond the mandated set: TanStack Query, React Hook Form + Zod | Accept — every screen is server-state driven | Hand-rolled fetch/effect/validation code with equal behaviour, more review surface |
| **A-10** | Reserve an extraction-state column on `Document` now, with no extraction machinery | Accept — costs one column, avoids a later migration | Add it when FUT-01 is built |
| **A-11** | Deployment target not chosen in this document | Defer — no requirement names a host | Needs a decision before the first deployment, not before the first commit |
| **A-32** | Make RLS real: API connects as a non-bypass role and sets validated JWT claims per request | Accept — otherwise the constitution's RLS mandate is nominal | Rely on application-layer checks only; RLS becomes future work (state this explicitly) |

### 2.3 Domain and data

| ID | Decision | Recommendation | If you reject it |
|---|---|---|---|
| **A-12** | `StatuteArticle.headingId` **nullable**, while `AgreementClause.headingId` is required | Accept — 89.6% of statute articles are unclassified | Requiring classification blocks loading the legal library |
| **A-13** | `NegotiationCaseState` and `PetitionState` vocabularies as proposed | Accept with review — no data can validate them | Must specify the states you actually need |
| **A-14** | `ClauseNumber` = integer + optional 1–2 letter suffix, with a derived order key | Accept | Affects legal numbering across agreements and statutes; must be right |
| **A-15** | Agreement gains `Title` (required) + `Number` (optional) + `UNIQUE(CompanyId, ValidFrom)` | Accept — legacy had no agreement identity at all | Agreements stay indistinguishable in lists; W1 cannot be demonstrated cleanly |
| **A-16** | Clause classification is **mandatory**; "No Compara" becomes `ComparisonHeading.IsExcluded` | Accept — removes the 16.6% unclassified backlog structurally | Keep heading nullable, and the 899-clause backlog returns |
| **A-17** | `IsExcludedFromCompilation` + optional `ExclusionNote` replaces `articulo_cerrado` | Accept — names the behaviour; the *reason* for exclusion is still unknown | Drop the concept and lose the 158-clause behaviour |
| **A-18** | Economic comparison stays **free text** in the MVP; no amount/currency/unit modelling | Accept — modelling it would invent domain semantics | If economic comparison is a real feature, **specify its semantics**; this may be the product's highest-value feature |
| **A-19** | A published agreement's clauses may still be edited; the edit is audited | Accept | Freezing published agreements needs a versioned model — significantly larger |
| **A-20** | An `EmployerOffer` responds to exactly one petition | Accept | Offers may be general; changes the negotiation model |
| **A-21** | Promotion to agreement is **terminal**; an agreement cannot be reopened after promotion | Accept — prevents the legacy duplicate-clause defect | Reopening needs explicit versioning of the promoted agreement |
| **A-22** | Comparison heading groups carry an explicit `SortOrder` (legacy ordered alphabetically) | Accept | Alphabetical ordering, which loses the intended reading order |
| **A-42** | Company keeps the legacy `codigo_empresa` as `LegacyCode` (unique when present), never as a key | Accept if the business cites that code anywhere | If it is meaningless, drop it — but that is a business answer I cannot infer |

### 2.4 API

| ID | Decision | Recommendation | If you reject it |
|---|---|---|---|
| **A-23** | Major version in the URL path; no content negotiation | Accept | Minor cost, no benefit while SPA+API ship together |
| **A-24** | No `Idempotency-Key` machinery; repeats of publish/promote return `409` | Accept | Adds a key store for no current client behaviour |
| **A-25** | No success envelope around single resources; collections use `{items, page}` | Accept | Cosmetic; consistency over convention |
| **A-26** | No validation library; value objects + explicit guards + Zod mirror | Accept | A library duplicates what value objects express |
| **A-27** | `GET /comparison` reports unpublished requested agreements in `unpublishedRequested` rather than failing | Accept — observable and testable | Return `409`, or silently drop them |
| **A-28** | Document upload limit 20 MB | Accept as a starting point | Needs a number from you; the legacy gazettes reached 6 MB |
| **A-29** | Optimistic concurrency (`xmin`) with `If-Match` | Accept — concurrent clause editing is the real risk | Last-write-wins silently loses edits |
| **A-30** | Generate the TypeScript client from the API's OpenAPI document | Accept | Hand-maintained types plus a snapshot contract test |
| **A-31** | Use MUI's `DataGrid` (community) for list screens | Accept | Hand-built tables for S5/S10/S12 |

### 2.5 Security

| ID | Decision | Recommendation | If you reject it |
|---|---|---|---|
| **A-33** | Rich text sanitised server-side with a maintained allow-list sanitiser library + strict CSP | Accept — a hand-rolled XSS filter protecting legal documents is the worse risk | Store plain text only: a product regression |
| **A-34** | Access token in memory only; refresh token in `HttpOnly` cookie | Accept | `sessionStorage` survives reloads but exposes the token to XSS |
| **A-35** | Public sign-up disabled on the Supabase project | Accept — otherwise "no out-of-band accounts" is untrue at the identity layer | Keep sign-up open; provisioned-profile check still returns `403` |
| **A-36** | Four roles: Administrator, Editor, Analyst, Negotiator (+ `system.superadmin`) | Accept with review of the capability matrix | The legacy six roles cannot be translated; they must be redefined |
| **A-37** | Negotiators are scoped to their own company's cases | Accept if that reflects reality — legacy applied it as a list filter in one screen | Scope by case participation instead |
| **A-38** | `expires_on` enforced on every request; no renewal flow, no warnings | Accept provisionally | Expiry semantics are unproven; you may prefer to drop the feature |
| **A-39** | Personal-data retention: kept with the case, deleted with it; published agreements never hard-deleted | Accept provisionally | Needs a stated policy; this is a new requirement with no legacy precedent |
| **A-40** | Audit retained indefinitely in the MVP | Accept — volume is small | Define a retention window |

**That is 42 items.** My recommendation for a first review round is to decide **A-02, A-04, A-05,
A-07, A-12, A-15, A-16, A-18, A-32, A-36** — they are the ones that change the shape of the system.
The rest are reversible at low cost.

---

## 3. Accepted decisions (ADRs)

### ADR-001 — Modular monolith, six business modules, three platform modules
**Context:** mandated no-microservices; the legacy system was a single PHP tree with no boundaries at
all (every module reached into every table).
**Decision:** one API process; Catalog, Agreements, Negotiation, LegalLibrary, Comparison, Publishing;
plus Identity, Documents, Audit as platform modules. Dependency rules enforced by architecture tests.
**Consequences:** boundaries are real but in-process; a future extraction is possible because modules
communicate through interfaces, not tables.

### ADR-002 — Two projects per business module
**Decision:** `<Module>` (Domain + Application) and `<Module>.Infrastructure`.
**Consequences:** the critical Clean Architecture guarantee — Domain references no framework — is
compile-time enforced. Domain/Application separation is by namespace and test, not by project.

### ADR-003 — Single `DbContext` and single `public` schema
**Context:** promotion writes across Negotiation and Agreements atomically; the MVP is small; the team
is small.
**Decision:** one context, one schema, module-prefixed table names, architecture tests forbidding
cross-module entity access.
**Consequences:** boundaries depend on discipline. **Revisit trigger:** two modules querying each
other's tables more than once per release.

### ADR-004 — No CQRS, no mediator, no broker, no generic repository, no domain events
**Context:** Phase 3 instruction; every screen is a straightforward use case.
**Decision:** endpoints call application services directly; `DbContext` is the unit of work; specific
repositories only for the `CollectiveAgreement` aggregate (needed to enforce publish/clause-number
invariants); no dispatcher, no outbox.
**Consequences:** the codebase is smaller and debuggable. Trade-off accepted: no cross-cutting pipeline;
ASP.NET Core filters cover the few cross-cutting needs (correlation, capability, error mapping).

### ADR-005 — `Result<T>` for expected failures, exceptions for bugs
**Decision:** application services return `Result<T>` with a typed error (`NotFound`, `Conflict`,
`InvalidState`, `ValidationFailed`); domain invariant breaches throw `DomainRuleViolated` and are mapped
to `409`/`400`.
**Consequences:** no exception-driven control flow for expected cases; the API maps errors in one place.

### ADR-006 — EF Core migrations are the only schema mechanism
**Decision:** no hand-written DDL, no second migration tool; migrations committed and reviewed; applied
to Supabase only with explicit approval (Art. VI.4).
**Consequences:** CI can rebuild the schema from scratch in Testcontainers; production changes are
reviewable diffs.

### ADR-007 — Optimistic concurrency via `xmin`
**Decision:** `xmin` mapped as a concurrency token, surfaced as `rowVersion`, required on
`PATCH`/`DELETE`.
**Consequences:** no lost updates; clients must send the token (TanStack Query keeps it in cache).

### ADR-008 — Keyset pagination for unbounded lists, offset for catalogues
**Decision:** clauses, audit and public agreements use cursors; companies, statutes, catalogues, news
use offset with totals.
**Consequences:** no deep-offset degradation on the 5,402-clause-scale tables.

### ADR-009 — Comparison is adjacency in the MVP
**Context:** Phase 1 §BR-15 proved the legacy feature renders same-heading clauses side by side with no
alignment, pairing, diff or gap detection. Phase 1 Q-6 asked whether alignment was intended and remains
unanswered.
**Decision:** build adjacency (legacy parity) in the MVP; alignment is R2-01 and is **not** assumed.
**Consequences:** the MVP is demonstrable and honest. **This is the single largest product risk** — if
you expect alignment, the MVP will read as incomplete, so A-18-adjacent confirmation matters.

### ADR-010 — Capability-based authorization, verified by route enumeration
**Decision:** `resource.action` capability keys; every endpoint declares one; a test enumerates all
routes and asserts `401` and `403` behaviour.
**Consequences:** the legacy failure mode (54 of 55 write endpoints unchecked) becomes impossible to
reintroduce silently.

### ADR-011 — Single tenant
**Decision:** one organisation per deployment; no `tenant_id`.
**Consequences:** if multi-tenancy appears later it is a schema-wide migration. Decided in A-04.

### ADR-012 — Bootstrap SuperAdmin through a one-time, self-inerting endpoint
**Decision:** `POST /auth/bootstrap` exists only while no SuperAdmin exists; no seeded users anywhere.
**Consequences:** satisfies Art. VI.6 and removes the class of defect that let `$t_usuario = 1` bypass
everything.

### ADR-013 — Publication is a recorded act, not an approval workflow
**Decision:** `published_at` + `published_by`; unpublish reverses them; both audited; clauses remain
editable after publication with audit.
**Consequences:** drift is visible rather than forbidden (see A-19, A-41).

### ADR-014 — Promotion is atomic, idempotent and terminal
**Decision:** one transaction across both modules; second attempt `409`; a `Promoted` case cannot reopen.
**Consequences:** fixes the legacy defect where reopen+re-close duplicated every clause into a second
agreement (BR-11).

### ADR-015 — `ley_trabajo` and `otras_leyes` fold into one `Statute` with a `Kind`
**Context:** `ley_trabajo` has no primary key, so its 556 articles had no referenceable parent (DS-03).
**Decision:** one entity, `StatuteKind` discriminator, articles as children.
**Consequences:** labour-law articles get the FK they never had; the "exactly one row" fragility
disappears.

### ADR-016 — Company has a single identity; the legacy code becomes optional data
**Context:** Phase 2 proved `id` (116–195) and `codigo_empresa` (2–115) never coincide, agreements join
on the code, and one user row joins on the id.
**Decision:** `Id` is the only key; `LegacyCode` is a unique-when-present attribute (A-42).
**Consequences:** the ambiguity that produces wrong joins today cannot recur.

### ADR-017 — Documents: private bucket, metadata table, signed downloads
**Decision:** `Document` entity, server-generated storage keys, byte-sniffed content types, signed
short-lived URLs, no public bucket.
**Consequences:** closes §S7 fully; requires the API to mediate all file access.

### ADR-018 — No asynchronous processing in the MVP; extraction seam reserved
**Decision:** no queue, no broker, no background worker. `Document.ExtractionStatus` exists, always
`NotRequested`.
**Consequences:** the first async workload (FUT-01) has a designed entry point (202 + polling) without
paying for it now.

### ADR-019 — Audit is written in the same transaction, append-only
**Decision:** audit columns on every business table via interceptor; `audit_entries` for state changes,
written inside the use case's transaction, no update/delete handlers.
**Consequences:** a failed operation leaves no audit row; the log outlives its entities.

### ADR-020 — TypeScript client generated from the API's OpenAPI document
**Decision:** generation in the build; no hand-maintained DTO types in the SPA.
**Consequences:** contract drift becomes a build failure rather than a runtime surprise.

---

## 4. Resolutions of the Phase 1 open questions

| Question | Resolution |
|---|---|
| **Q-1** Agreement identity | → **A-15**: title + optional number + per-company/validity uniqueness |
| **Q-2** Clause numbering | → **A-14**: integer + optional letter suffix + derived order key |
| **Q-3** Economic comparison | → **A-18**: free text in MVP; structuring is an open product decision, flagged as possibly the highest-value feature |
| **Q-4** Negotiation states | → **A-13** / **A-03**: four states proposed; no data validates them |
| **Q-5** Petition outcomes | → **A-13**: four states proposed; legacy could express only two |
| **Q-6** Comparison semantics | → **ADR-009**: adjacency in MVP; alignment deferred to R2-01. **Largest product risk — still needs your confirmation** |
| **Q-7** Publication governance | → **A-41** / ADR-013: recorded act, no approval workflow |
| **Q-8** Permission model | → ADR-010 / **A-36**: capability-based |
| **Q-9** Real roles | → **A-36**: four work-based roles; the legacy six cannot be translated |
| **Q-10** Account expiry | → **A-38**: kept, enforced per request, semantics unproven |
| **Q-11** Reference delete policy | → RESTRICT for business-referenced catalogues, CASCADE only country→state→municipality. Recorded in ADR-016 context |
| **Q-12** Ordering of headings | → **A-22**: explicit `SortOrder` |
| **Q-13** Validation requirements | → §3 of api-contracts: typed value objects; no legacy rules survived, so requirements are **new** and need your input |
| **Q-14** Portal placement | → same SPA, public routes, no SSR. If SEO matters this reverses |
| **Q-15** "No Compara" | → **A-16**: `ComparisonHeading.IsExcluded`. **Answered by Phase 2 data** (853 clauses) |
| **Q-16** Links of interest | → deferred, not in MVP (FUT-05). No requirement exists |
| **Q-17** Promotion semantics | → ADR-014 / **A-21**: atomic, idempotent, terminal |
| **Q-18** Legacy company code | → **A-42**: `LegacyCode` attribute, never a key |
| **Q-19** Clause exclusion | → **A-17**: `IsExcludedFromCompilation` + note. Reason still unknown |

**Q-6 and Q-18 are the two answers I still cannot give.** Q-6 is a product decision with a large scope
consequence. Q-18 is a business fact only you know.

---

## 5. Rejected alternatives

| Alternative | Why rejected |
|---|---|
| Microservices per module | Constitution Art. IV.1; no independent scaling or team need |
| CQRS + MediatR + pipeline behaviours | One handler per use case; the pipeline would carry logging and validation that filters already provide |
| Message broker + outbox now | No asynchronous workload exists (ADR-018) |
| Generic `IRepository<T>` | Hides the projection capability the comparison query needs; `DbContext` is already the unit of work |
| Schema-per-module Database-per-module | Cross-module atomicity required for promotion; 6 migration histories for a boundary still enforced by review |
| Domain events / dispatcher | Only cross-aggregate effect is audit, handled by an interceptor |
| Translating the legacy schema table-for-table | Constitution Art. V.1; Phase 2 documented 34 defects that a translation would reproduce |
| Reproducing the six legacy roles | They mix two axes and contradict their names (BR-19) |
| Keeping `status_articulo` workflow | 5,402 rows, one value, zero writers (DS-18) |
| Storing files by client filename | The legacy upload vulnerability (§S7) |
| Plain-text-only clause bodies | Loses legitimate legal formatting; sanitisation is the correct control (A-33) |
| `localStorage` for access tokens | Exposes tokens to any XSS (A-34) |
| Offset pagination on clauses | Degrades and skips rows on a 5,402-row-plus table |

---

## 6. Change log

| Version | Change |
|---|---|
| 0.1 | Initial architecture proposal from approved Phase 1 and Phase 2 findings. 42 approval items, 20 ADRs, 19 question resolutions. |
