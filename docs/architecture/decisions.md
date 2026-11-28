# Decisions — Convenciones

**Status: partly decided.** Section 1 records the ten decisions the Product Owner has now taken;
section 2 lists what is still outstanding.

Documents: [mvp-scope.md](mvp-scope.md) · [solution-architecture.md](solution-architecture.md) ·
[domain-model.md](domain-model.md) · [api-contracts.md](api-contracts.md) · [security-model.md](security-model.md)

---

## 0. Decided by the Product Owner — Task 6

Ten questions answered. Each answer is binding and is reflected in the constitution where it is a
standing rule.

| Q | Question | Decision | Effect |
|---|---|---|---|
| **Q-1** | Agreement identity | **Title is auto-composed from company + period** | `agreement.title` and `agreement.number` are **removed**; identity is company + validity period, enforced by `UNIQUE (company_id, valid_from)`. Display name is derived on read |
| **Q-2** | Clause numbering | **Integer** | `number_suffix` is **removed** from all five clause-bearing tables; `ClauseNumber` becomes a single positive integer; uniqueness becomes `(parent_id, number_int)` |
| **Q-3** | Economic comparison | **Free text** | Confirms A-18: `economic_comparison_note` stays `text`; no amount, currency or unit is modelled |
| **Q-4** | Negotiation stages | **`Open`, `InProgress`, `Pending`, `Agreed`, `Closed`** | Replaces `Draft/Open/Closed/Promoted`. `Promoted` disappears as a state; promotion is recorded by `promoted_agreement_id` while the case sits in `Closed` |
| **Q-5** | Petition outcomes | **Agreed with the recommendation** | `Open`, `Agreed`, `Rejected`, `Withdrawn` — already implemented |
| **Q-6** | Comparison semantics | **Agreed with the recommendation** | Confirms ADR-009: adjacency in the MVP; alignment deferred to R2-01 and explicitly **not** in scope |
| **Q-7** | Publication governance | **Agreed with the recommendation** | Confirms A-41/ADR-013: `published_at` + `published_by`, no approval workflow, post-publication edits audited |
| **Q-8** | Permission model | **Agreed with the recommendation** | Confirms ADR-010: capability keys (`agreement.publish`), not screen names |
| **Q-9** | Roles | **SuperAdmin, Admin, Editor, Transcriptor, Negotiator** | Five roles, not four. `superadmin` holds `system.superadmin`; `transcriptor` is restored as a first-class role (transcribes clause text, does not publish) |
| **Q-10** | Account expiry | **Agreed with the recommendation** | Confirms A-38: `expires_on` enforced on every request, no renewal flow |

**Standing rules resulting (constitution v1.2.0):** Domain-Driven Design is mandatory where
invariants span aggregates (Art. IV.3); CQRS is mandatory in code (Art. IV.4); `main` protection is
an enforcement obligation (Art. VIII.1).

**Schema deltas required by Q-1, Q-2, Q-4 and Q-9 are listed in §7.** They are *not* applied in this
task.

---

## 1. How to read this

- **Section 2 — decisions still requiring approval.** Marked `[A-nn]`. Items answered above are
  struck from this list.
- **Section 3 — accepted decisions (ADRs).** Choices judged determined by the evidence, recorded so
  they are reviewable.
- **Section 4 — resolutions of the Phase 1 open questions**, now updated with the decisions above.

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
**Consequences:** the intended layout separates Domain/Application from Infrastructure per module.
The current build collapses each module into one project, so that split is a namespace and review
convention, not a compile-time boundary; what the build enforces is the project graph
(`tests/Convenciones.ArchitectureTests`).

### ADR-003 — Single `DbContext` and single `public` schema
**Context:** promotion writes across Negotiation and Agreements atomically; the MVP is small; the team
is small.
**Decision:** one context, one schema, module-prefixed table names; project references are enforced by
architecture tests, while cross-module entity and table access through the shared context is a review
rule.
**Consequences:** boundaries depend on discipline. **Revisit trigger:** two modules querying each
other's tables more than once per release.

### ADR-004 — ~~No CQRS~~; no mediator, no broker, no generic repository, no domain events
**CQRS half superseded by ADR-022 (Task 6).**

**Context:** Phase 3 instruction; every screen is a straightforward use case.
**Original decision:** endpoints call application services directly; `DbContext` is the unit of work; specific
repositories only for the `CollectiveAgreement` aggregate (needed to enforce publish/clause-number
invariants); no dispatcher, no outbox; **no CQRS**.
**Status:** the CQRS half is **withdrawn** — the Product Owner mandated CQRS, now constitution
Art. IV.4. The remaining prohibitions stand and are *not* reopened by that mandate: no mediator, no
command bus, no message broker, no generic repository, no domain events, no separate read store.
Those were rejected on merit and were not requested.
**Consequences:** commands and queries are separate handler types (ADR-022), still invoked directly
by endpoints and still on one `DbContext`. No cross-cutting pipeline; ASP.NET Core filters cover
correlation, capability and error mapping. The codebase grows by the query/command split, which is
the cost the Product Owner accepted.

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

*Implemented in T10, with three refinements:* at most one `LabourLaw` is enforced by the partial unique
index `statute_single_labour_law_key` (the rule spans rows, so the index is the guard and the API's 409
names it); `Kind` is fixed at creation; and an article's own heading (`title`) is mandatory while its
comparison classification (`heading_id`) stays nullable per A-12 — two different "headings" that the
legacy tables conflated.

### ADR-016 — Company has a single identity; the legacy code becomes optional data
**Context:** Phase 2 proved `id` (116–195) and `codigo_empresa` (2–115) never coincide, agreements join
on the code, and one user row joins on the id.
**Decision:** `Id` is the only key; `LegacyCode` is a unique-when-present attribute (A-42).
**Consequences:** the ambiguity that produces wrong joins today cannot recur.

### ADR-017 — Documents: private bucket, metadata table, signed downloads
**Decision:** `Document` entity, server-generated storage keys, byte-sniffed content types, signed
short-lived URLs, no public bucket.
**Consequences:** closes §S7 fully; requires the API to mediate all file access.

### ADR-018 — No asynchronous processing in the MVP; extraction is synchronous
**Decision:** no queue, no broker, no background worker. T5 extracts PDF text inside
`POST /documents/{id}/extract` and answers **`200 OK`** with the final state; `Document.ExtractionStatus`
is written by that request (`InProgress` → `Completed` or `Failed`).
**Amendment (T5, approved):** the original wording reserved `ExtractionStatus` as always `NotRequested`
and described the future entry point as `202 Accepted` + polling. The MVP deviation is synchronous:
returning `202` for work the request has already finished would report a state that no longer exists and
force a pointless poll. The 202 + polling shape remains the design for FUT-01, when extraction becomes
genuinely asynchronous (OCR/AI at volume); the state model and the status endpoint already support it.
See [task-05 §2](../tasks/task-05-document-processing.md#2-architecture-decision-synchronous-extraction-200-ok).
**Consequences:** the first async workload (FUT-01) still has a designed entry point without paying for a
broker now, and the MVP reports the extraction outcome in the response that caused it.

### ADR-019 — Audit is written in the same transaction, append-only
**Decision:** audit columns on every business table via interceptor; `audit_entries` for state changes,
written inside the use case's transaction, no update/delete handlers.
**Consequences:** a failed operation leaves no audit row; the log outlives its entities.

### ADR-020 — TypeScript client generated from the API's OpenAPI document
**Decision:** generation in the build; no hand-maintained DTO types in the SPA.
**Consequences:** contract drift becomes a build failure rather than a runtime surprise.

### ADR-021 — Domain-Driven Design is mandatory where invariants span aggregates *(Task 6)*
**Context:** the Product Owner mandated DDD. The Phase 3 design already used it for the three
aggregates with cross-row invariants, but treated it as a judgement rather than a rule.
**Decision:** constitution Art. IV.3 makes it a rule with a boundary: aggregates are used **where an
invariant spans their parts**, and plain entities where CRUD genuinely suffices. The three
aggregates stand — `CollectiveAgreement` (publish requires ≥ 1 clause; clause numbers unique within
the agreement), `NegotiationCase` (cannot close without an agreement; an agreed petition is locked),
`Statute` (article numbers unique within a statute).
**Explicitly not changed:** reference data (countries, catalogues, comparison headings), news,
companies and roles stay plain entities with application services. Wrapping them in aggregates,
repositories and domain events would add ceremony with no invariant to protect, which Art. IV.7
still forbids.
**Consequences:** invariants live in the model and are also asserted by database constraints. Adding
a fourth aggregate now requires naming the invariant it protects.

### ADR-022 — CQRS is mandatory in code; infrastructure-level CQRS is not *(Task 6)*
**Context:** the Product Owner mandated CQRS, reversing the earlier rejection in ADR-004.
**Decision:**
- **Commands** (`CreateAgreement`, `AddClause`, `PublishAgreement`, `PromoteCaseToAgreement`) change
  state, load the aggregate, enforce its invariants, and write through it.
- **Queries** (`GetAgreement`, `ListAgreements`, `GetComparison`, `ListPublishedAgreements`) never
  load aggregates and never mutate; they project straight into read models with `AsNoTracking()`.
- Separate handler types in separate namespaces (`…Commands`, `…Queries`); a command handler never
  returns a read model and a query handler never touches a repository.
- Endpoints invoke handlers **directly** — no mediator, no command bus, no pipeline (ADR-004's
  remaining prohibitions stand).
**Why not full infrastructure CQRS:** one database, one `DbContext`, no scaling asymmetry between
reads and writes. A separate read store would need synchronisation, eventual consistency, and a
projection mechanism — three problems with no requirement behind them yet. Constitution Art. IV.4
requires an amendment before adding any of it.
**Consequences:** the application layer roughly doubles in type count (request + handler +
response per use case) and stays trivially debuggable. The comparison workspace is the query that
justifies CQRS at all: it is a multi-table projection that would be actively harmed by
aggregate-loading.

*Implemented by T10 as written.* The legal library has one handler **type** per use case on both
sides — `CreateStatuteHandler` … `DeleteStatuteArticleHandler` for writes, `ListStatutesHandler` …
`GetStatuteArticlesForHeadingHandler` for reads — each owning its request and response record and
receiving its `IQueryable` explicitly, so a query has no way to write. The earlier static query helpers
in Agreements and Comparison are the same rule expressed as static methods; they predate this
implementation, are untouched by T10, and are the one remaining shape to normalise when those modules
are next opened (recorded, not silently tolerated).

### ADR-023 — A statute article has two different "headings", and only one is mandatory *(T10)*
**Context:** T10's acceptance criterion 3 read "an article without a heading is refused" while citing
the 89.6% of legacy statute articles that carried no *comparison classification*. Taken as a rule about
classification it would reverse approval item A-12 (row 2.3), whose stated reason is that requiring
classification blocks loading the library at all. The schema has two separate fields that the legacy
tables conflated: `statute_article.title` (the article's own heading, e.g. "Artículo 12 — Vacaciones")
and `statute_article.heading_id` (the optional comparison classification).
**Decision:**
- `title` is **mandatory**: not null, non-blank, enforced by the aggregate and by the CHECK
  `statute_article_title_not_blank`. An article with no heading of its own is not citable and is refused
  with `400 validation_failed`.
- `heading_id` is **optional**, exactly as A-12 approved: `null` is a legitimate, permanent state and
  the database accepts it (`statute_article_heading_idx` is partial on `heading_id is not null`).
- The term "heading" alone is therefore avoided in the legal-library contract; the API says `title` and
  `headingId`, and the task record states which one criterion 3 meant.
**Why not make classification mandatory:** it would reverse A-12 and block the corpus the library
exists to hold; 89.6% of the legacy other-statute articles have no classification, and the worklist to
classify them is R2-05, a separate task.
**Consequences:** A-12 stands unchanged. A future instruction that "law articles must be classified"
is an amendment to A-12 with a data-loading plan, not a change to this endpoint.

### ADR-024 — Reading statutory text through the comparison requires `statute.read` too *(T10)*
**Context:** T9 left `statuteArticles` empty and declared `GET /comparison` as `comparison.read`. T10
fills the array with the full body of every statute article classified under the heading, so the
projection returns legal-library data to a caller who never needed `statute.read`. Every seeded role
that holds `comparison.read` also holds `statute.read`, so nothing broke — but the capability
separation was accidental rather than enforced.
**Decision:** `GET /comparison` requires **both** `comparison.read` and `statute.read`. The other
comparison routes (heading groups, taxonomy, candidates) return no statutory text and keep
`comparison.read` alone. Enforcement stays where capabilities are enforced everywhere: the endpoint
declaration, with the startup gate refusing any route that does not declare one.
**Why not the other way round:** relaxing `statute.read` to mean "everything the legal library shows"
would make the distinction meaningless; dropping the statute array from the comparison would remove the
product's core value. A capability gates the data it names, not the screen the data appears on.
**Consequences:** a caller with `comparison.read` only sees the workspace without the legal column
(**403** naming `statute.read`). Recorded in <code>api-contracts.md §4.4</code> and asserted by an
integration test with a transient role that has exactly that shape.

---

### ADR-025 — The negotiation workflow: explicit transitions, read-only closure, atomic promotion *(T11)*
**Context:** Q-4 settled *which* states exist and that `Promoted` is not one of them, but not how the
intermediate states are entered, whether a closed case can be edited, or what promotion does. The
legacy application answered some of it in the wrong place: `cerrar_discusion.php` set the state to
`10` by hand and inserted a contract with placeholder dates (`*Inicio`, `*Fin`, `*Duración`,
`*Ambito`), copied every clause into it, deleted the originals and made `mkdir(0777)`. The schema's
guards were also incomplete: the promotion trigger fired only on `UPDATE` and only when the previous
link was null, so a case could be inserted already promoted, re-pointed, promoted twice, or closed and
promoted in one statement. Nothing enforced the company boundary on the negotiation tables.

**Decision:**
1. **A case is created in `Open`.** Q-4 removed `Draft`, so there is no pre-open state and no `/open`
   operation; the three middle states are entered by explicit operations (`/in-progress`, `/pending`,
   `/agree`) whose only precondition is the source state. `Agreed` is an explicit act — no invented rule.
2. **`close` requires at least one meeting agreement.** The rule is stated by the API contract ("close
   without agreements") and by INV-N2; the legacy application only displayed the message and never
   refused the write. Recorded here as: the rule is the contract's and the invariant's, not a Q
   resolution.
3. **A closed case is read-only.** Every child write requires `state <> Closed`; reopening is the only
   way back. The legacy handlers checked nothing, and a closed case remained editable and deletable.
4. **A promoted case is terminal.** `Reopen` is refused once `PromotedAgreementId` is set; the link is
   never cleared and the case never leaves `Closed` (A-21, ADR-014). This is also the only reading
   compatible with `negotiation_case_promoted_consistency`, which forbids a link on a non-`Closed` row.
5. **Promotion is separate, authorised, atomic and header-only.** It is `POST …/promote` gated by
   `negotiation.promote`, it creates the agreement through the agreement aggregate's own rules
   (reusing `POST /agreements`), and it writes the agreement and the case link in **one transaction**.
   Meeting agreements are not copied into clauses: the case is the negotiation record, and clause text is
   authored in the agreement workspace. Legacy's placeholder values and clause copying are not carried
   over.
6. **Petition outcomes are explicit.** Recording a meeting agreement does not change a petition's state;
   the outcome is set through `PUT …/petitions/{id}/state`. Q-5 names the outcomes but not the graph, so
   the minimum graph is implemented: `Open → Agreed | Rejected | Withdrawn` and `Agreed → Open` (the
   legacy reopen). `Rejected` and `Withdrawn` are terminal. Documented as provisional until Q-5 grows a
   transition list.
7. **The database enforces what the aggregate enforces.** `app.guard_case_promotion()` now fires on
   `INSERT` and on any `UPDATE`, refuses a non-`Closed` source, a missing capability, a second
   promotion, and any change to a promoted row's state or link — with distinct SQLSTATEs (`42501`,
   `22023`, `23505`) so the API can answer 403/409 without parsing messages. Company-scoped RLS covers
   the six negotiation tables, and the agreement insert policy admits `negotiation.promote` only for a
   `Draft` header, because the seeded negotiator deliberately does not hold `agreement.write`.

**Why not the alternatives:** a `Draft` state (or an `/open` operation with no observable effect) adds a
state with no business meaning and was removed by Q-4; allowing edits to a closed case would make
promotion non-reproducible and contradicts the read-only record the contract describes; copying meeting
agreements into clauses re-creates the legacy duplication defect; auto-setting a petition to `Agreed`
when a meeting agreement is recorded would write a business decision the user never made.

**Consequences:** the negotiation API's contract is <code>api-contracts.md §4.6</code>; the aggregate is
in `src/Convenciones.Negotiation/`, persistence in `NegotiationStore` and the promoted agreement is
created by `PromotedAgreementFactory`. The database half is asserted by
`supabase/tests/constraint-tests.sql` (D5, D8) and `rls-tests.sql` (R36–R40) and, through Npgsql with the
API bypassed, by the integration tests. **INV-N1 (parties before opening) remains unimplemented:** no
state precedes `Open`, so enforcing it at creation would be a new business rule; the decision is open.


---

## 4. Resolutions of the Phase 1 open questions

| Question | Resolution | Decided |
|---|---|---|
| **Q-1** Agreement identity | **Title auto-composed from company + period.** `title` and `number` are dropped as stored columns; identity is `UNIQUE (company_id, valid_from)` | **Task 6** |
| **Q-2** Clause numbering | **Integer.** No suffix. `ClauseNumber` is one positive integer; uniqueness is `(parent_id, number_int)` | **Task 6** |
| **Q-3** Economic comparison | **Free text.** No amount, currency or unit is modelled | **Task 6** |
| **Q-4** Negotiation stages | **`Open`, `InProgress`, `Pending`, `Agreed`, `Closed`.** `Promoted` is no longer a state; promotion is recorded by `promoted_agreement_id` while the case is `Closed` | **Task 6** |
| **Q-5** Petition outcomes | `Open`, `Agreed`, `Rejected`, `Withdrawn` — as recommended | **Task 6** |
| **Q-6** Comparison semantics | Adjacency in the MVP (ADR-009); alignment deferred to R2-01 | **Task 6** |
| **Q-7** Publication governance | Recorded act with actor and timestamp; no approval workflow (ADR-013) | **Task 6** |
| **Q-8** Permission model | Capability-based (ADR-010) | **Task 6** |
| **Q-9** Real roles | **SuperAdmin, Admin, Editor, Transcriptor, Negotiator** — five, not four | **Task 6** |
| **Q-10** Account expiry | Kept, enforced on every request, no renewal flow (A-38) | **Task 6** |
| **Q-11** Reference delete policy | RESTRICT for business-referenced catalogues, CASCADE only country→state→municipality (ADR-016 context) | assumed |
| **Q-12** Ordering of headings | **A-22**: explicit `SortOrder` | assumed |
| **Q-13** Validation requirements | §3 of api-contracts: typed value objects. No legacy rules survived, so requirements are **new** | **open** |
| **Q-14** Portal placement | Same SPA, public routes, no SSR. Reverses if SEO matters | assumed |
| **Q-15** "No Compara" | **A-16**: `ComparisonHeading.IsExcluded`. Answered by Phase 2 data (853 clauses) | assumed |
| **Q-16** Links of interest | Deferred, not in MVP (FUT-05). No requirement exists | assumed |
| **Q-17** Promotion semantics | ADR-014: atomic, idempotent, terminal | assumed |
| **Q-18** Legacy company code | **A-42**: `LegacyCode` attribute, never a key. **Still a business fact only you know** | **open** |
| **Q-19** Clause exclusion | **A-17**: `IsExcludedFromCompilation` + note. Reason still unknown | assumed |

**Q-13 and Q-18 remain open.** Q-13 (which fields are genuinely mandatory) is a requirements
question; Q-18 (whether the legacy company code is a value the business cites) is a business fact.
Everything else is decided or safely assumed and reversible.

---

## 7. Schema deltas required by the Task 6 decisions

**Not applied in Task 6.** These are implementation changes to the schema merged in PR #5 and must
land as a forward migration, not by editing applied migration files (constitution Art. VIII.6).

| Delta | Cause | Work |
|---|---|---|
| Drop `number_suffix` from `agreement_clause`, `statute_article`, `union_petition`, `employer_offer`, `meeting_agreement` | Q-2 integer numbering | Drop 5 columns; rewrite 5 unique indexes to `(parent_id, number_int)`; drop `*_suffix_format` CHECKs; update the clause/statute ordering indexes |
| Replace enum `app.negotiation_case_state` | Q-4 five stages | New type `('Open','InProgress','Pending','Agreed','Closed')`, swap the column, drop the old type. No data to migrate — the table has never held a row |
| Rewrite `negotiation_case_closed_consistency` and `negotiation_case_promoted_consistency` | Q-4 removes `Promoted` | `closed_at` not null iff `state = 'Closed'`; `promoted_agreement_id` not null implies `state = 'Closed'` |
| Retarget `app.guard_case_promotion()` | Q-4 removes `Promoted` | Fire on `UPDATE OF promoted_agreement_id` instead of `UPDATE OF state`, still requiring `negotiation.promote` |
| Drop `agreement.title` and `agreement.number` | Q-1 auto-composed title | Drop 2 columns; keep `UNIQUE (company_id, valid_from)`; compose the display name on read. `agreement_title_not_blank` CHECK goes with the column |
| Replace the four seeded roles with five | Q-9 | `seed.sql`: add `superadmin` and `transcriptor`, keep `administrator`, `editor`, `negotiator`; rebuild the grant matrix (transcriptor: clause transcription without publish) |
| Update the test suites | consequences of the above | Constraint tests B5/B7/B8 (suffix) removed; add cover for integer numbering, the five states, promotion on `promoted_agreement_id`; RLS tests re-run against the new role set |
| Update `target-schema.md` and `legacy-to-target-mapping.md` | documentation drift | Reflect the dropped columns, the new enum and the five roles |

**Not affected:** Q-3, Q-5, Q-6, Q-7, Q-8 and Q-10 confirm what is already built.

---

---

### ADR-026 — The public surface: allow-list projections, the `anon` database role, and news publication as a guarded transition *(T12)*
**Context:** the schema already had `anon` policies and grants for published agreements, their clauses and
news, but the API had no anonymous route. More importantly, an anonymous request ran with the *login's*
privileges: `DatabaseSessionInterceptor` reset the role when no session was resolved, so the first public
endpoint written the obvious way would have served drafts and bypassed every `anon` policy. News
publication was also unguarded at the database: `news.write` is enough to UPDATE the row, so a writer
without `news.publish` could publish with a direct statement. And the security model's AC-S10 had no
implementation at all — no sanitiser existed anywhere in the API, so stored clause bodies were echoed raw.

**Decision:**
1. **Public reads are explicit projections.** Records, not serialised entities, for `/public/agreements`,
   `/public/agreements/{id}`, `/public/news`, `/public/news/{id}` and the category list. The allow-list is
   the type definition, so adding a field to the admin shape cannot leak it.
2. **Anonymous data access runs on the `anon` role**, opted into by `ServesPublicData()`; the session
   middleware sets the flag and the connection interceptor issues `set role anon`. Public handlers filter
   by id or paging only — never by publication state — so the RLS policies are the guard and the leakage
   test is meaningful. `AllowAnonymous()` alone does not switch the role: bootstrap is anonymous and needs
   to write.
3. **News publication is a database-guarded transition.** `app.guard_news_publication()` requires
   `news.publish` on any state change, refuses an `INSERT` of a published article, and **assigns**
   `published_by`/`published_at` on publish and clears them on unpublish — so the actor and the time are
   the database's, not the caller's (T12 AC 3).
4. **Rich text is sanitised on write with the allow-list library (A-33) and again in the public
   projections.** The second pass exists because the portal may serve rows written before the rule. The
   sanitiser is applied to clause bodies, annex bodies and news bodies, closing AC-S10 for the whole
   product, not only for the new module.
5. **News is not company-scoped.** The schema has no `company_id`, published news is public, and any
   caller with `news.read` sees drafts. Categories are global reference data, created through
   `POST /news/categories` because no legacy category rows were migrated.

**Why not the alternatives:** serialising entities makes every future column a disclosure; enforcing the
publication filter in the handler makes the `anon` policies decorative and lets a handler bug publish
drafts; relying on the API capability lets a direct SQL statement publish; sanitising only at render makes
the guarantee depend on every client; storing plain text loses legitimate legal formatting (A-33). The
image columns stay out of the MVP: a public image would need a document download route, which is a
disclosure decision nobody has taken.

**Consequences:** one new dependency, `HtmlSanitizer` (the approach A-33 already accepted); `PatchField`
moved from `Convenciones.Legal` to `Convenciones.Catalog`, so both modules share one partial-update type;
the API login role must be allowed to `SET ROLE anon` (the `postgres`/`authenticator` logins Supabase uses are); agreement clause and annex bodies are now sanitised in T8's module, which is a security fix recorded here
rather than a silent behaviour change. The public projections were verified field by field in
`PublicApiTests`, and the database half in `constraint-tests` N1–N3 and `rls-tests` R41–R44.
---

### ADR-027 — The bootstrap is one database function, and identity administration never touches a password *(T13)*
**Context:** T13 needs three things the API did not have: a one-time first-run provisioning path, user
administration, and a readable audit trail. Each had a trap. The contract's bootstrap body carried
`{ email, displayName, password }` and said the password is "forwarded to Supabase Auth" — but this
service holds no Supabase Admin key, cannot create an auth user, and T13 forbids password handling
outright. The one-time window existed as an INSERT policy on `platform.user_account` keyed on
`not platform.has_any_user()`, which cannot admit the profile, role grant and audit row that must
accompany the account: after the account is inserted, `has_any_user()` is already true inside the same
transaction, so each child would need a *looser* rule — exactly the open registration door the window is
meant to avoid.

**Decision:**
1. **Provisioning takes the subject, not a password.** `POST /auth/bootstrap` and `POST /users` receive the
   Supabase Auth user id; an operator creates the auth user out of band. No endpoint accepts or returns a
   credential, and the database still has no password column.
2. **The first-run path is one `SECURITY DEFINER` function.** `platform.bootstrap_first_admin` takes an
   exclusive lock on `platform.user_account`, re-checks "no account exists" under the lock, refuses with
   `22023`, and writes the account, profile, SuperAdmin grant and audit row in one statement. Two
   concurrent bootstraps serialise; the second is refused. The check is inside the write, not beside it.
3. **Users are managed through the aggregate, with the anti-lockout rules in it.** Blocking or deleting
   yourself is refused, and so is replacing your own roles with a set that grants no administrative
   capability (**409**). Blocking takes effect on the next request because the session is resolved from the
   database every time.
4. **The role matrix is read-only in the MVP.** `GET /roles` and `GET /capabilities` expose what the seeded
   roles grant; writing roles or grants is deferred, not forgotten.
5. **The audit trail is a query, not a screen.** `GET /audit` filters by module, entity, actor and date,
   and is gated by `audit.read` in the API **and** by the RLS policy. The append-only trigger already makes
   history immutable.
6. **`AuditEntry` moved to `Convenciones.Catalog`.** The audit handler needs to filter on its columns
   before shaping the response; filtering over an API-owned projection is untranslatable, and the entity is
   a platform type like `IAuditable`, not API infrastructure.

**Why not the alternatives:** forwarding a password needs the Supabase Admin API and a service key this
service does not hold, and would put credentials in the request path; widening five policies with
`not has_any_user()` either fails after the account insert or loosens the window on every child table; an
API-only bootstrap check would leave the database door open if a handler were ever bypassed; a
database-level self-lockout guard would have to know the caller and would fight the replace-the-set
operation, and the supported path is the endpoint anyway.

**Consequences:** the seeded capability split matters — `user.manage` administers accounts, `role.manage`
reads the matrix, `audit.read` reads history. An audit-only role would see the trail without actor display
names, because reading another account's row requires `user.manage`; no seeded role has that shape today.
The function is the only writer of the first account, and `constraint-tests` G1–G2 plus `rls-tests`
R45–R47 assert it.

---

### ADR-028 — The interface shows what the data supports, and hides what the caller cannot do *(T14)*
**Context:** T14 is the first screen over every module. Three of its acceptance criteria are really
product decisions: which controls a caller sees, what the comparison screen may claim, and whether a
published agreement can be edited. Each has a failure mode that is invisible in a screenshot. A hidden
button that a hand-typed URL still reaches teaches nothing; a column layout that lines clauses up reads
as an equivalence claim the backend never made; an editor that lets someone change a published agreement
in place destroys the record the portal serves.

**Decision:**
1. **Capabilities gate the interface, the API gates the data.** `useCapabilities()` reads `/me` on every
   page load, navigation and controls are filtered by it, and the API remains the guard — the tests assert
   both halves, including a screen that surfaces a 403 rather than pretending the call succeeded.
2. **The comparison screen says adjacency and nothing more.** Clauses of one heading sit side by side
   because they treat the same subject; the subtitle, the alerts and the empty states say *vecindad por
   materia, sin alineación*, and a test forbids text that describes clauses as aligned.
3. **A published agreement is read-only until it is explicitly unpublished.** The editor hides every
   content control while `state === 'Published'`, explains why, and the `Despublicar` action is a
   confirmed dialog. There is no in-place edit path.
4. **The portal is a separate, tokenless client.** `/portal` renders outside `RequireSession` through
   `HttpPublicApi`, which has no `AuthClient` at all. The server-side guarantee is T12's: `/public/*` runs
   on the `anon` role and returns 404 for anything unpublished.
5. **Every list pages and every form validates before it submits.** Validation lives in `lib/forms.ts`
   and returns messages, so a form cannot call the API with an obviously invalid value.
6. **Stored rich text is rendered as HTML only because the server sanitises it** (A-33, and again at the
   public boundary in T12). The UI never composes HTML from its own inputs.

**Why not the alternatives:** showing every control and letting the API refuse produces a screen full of
buttons that fail; a comparison table that pairs clauses implies a judgement nobody made; an editor that
silently publishes on save breaks the publication record; reusing the session client for the portal would
ask an anonymous visitor for a token that does not exist.

**Consequences:** the screen set is the MVP's (documents, agreements, comparison, legal library,
negotiation, news, users/roles, audit, portal). Annex editing and news-category creation remain API-only
until a screen is asked for. The comparison wording is asserted in a test because it is the control for
the product's most over-claimable screen.

---

### ADR-029 — AI costs are metered per invocation, and budgets are shadow by default, opt-in enforced *(P1-005)*
**Context:** F-05 found that usage was aggregated per company and month, written after the stage had
already persisted its results, and that a missing token count was stored as zero — an unknown charge
presented as free. There was no ceiling and no way to attribute a charge to the document, run, attempt,
model or price list that produced it. A ceiling cannot be invented by engineering: the limit, the
overspend tolerance and who may set them are commercial decisions.

**Decision:**
1. **One ledger row per provider invocation**, written immediately after the call returns and before
   parsing, attributed from the run row (never from the caller) and carrying the price-list version.
2. **Money is computed in one place** (`AiPricing`); a missing token count yields no cost, never zero.
3. **Reserve before the call and settle after it**, under one advisory lock per company-period; the
   usage row and the counter movement commit in the same transaction. Unknown outcomes settle at the
   estimate and a late provider figure reconciles only the delta.
4. **A period without a budget row is in shadow mode, not unlimited.** The estimate and the
   would-admit decision are recorded; nothing is blocked. Enforcement starts only through an explicit,
   audited activation that converts shadow rows into itemised liabilities and refuses while unresolved
   unknown usage exists for the period.
5. **The policy stays with the Product Owner.** No limit, overspend tolerance or default activation is
   set by the code; the mechanism records the decision it awaits.

**Why not the alternatives:** a hard-coded ceiling picks a customer's budget by accident; blocking on an
estimate without a reservation would either overcharge or leak capacity; aggregating per month destroys
the evidence needed to reconcile a bill; storing unknown usage as zero is the original defect.

**Consequences:** today the worker records shadow estimates and enforces nothing. A company-period is
enforced only after an operator creates `platform.ai_budget` and activates it; there is no API for that
yet, so operating a budget is a SQL runbook step. The open decisions are listed in
[P1-005](../tasks/P1-005-cost-metering.md) and in
[pending owner sign-offs](../operations/owner-signoffs.md).

### ADR-030 — A user is created in the application by invitation, and one password screen serves invite and reset *(T13 follow-up)*
**Context:** ADR-027 kept the API out of identity creation because it held no Supabase Admin key and T13
forbids handling passwords. Both premises have shifted in practice: the API already carries the project's
`service_role` for Storage (`Program.cs:137-162`), and the workflow that decision left behind — create the
user in the dashboard, copy a UUID, paste it into `/admin/users` — is the friction the Product Owner hit
before the client trial. The same screen exposed a second defect: the dialog never sends `companyId`, so
every user created from the application has no company and sees an empty product.

**Decision:**
1. **The API creates the identity; it never touches the password.** `POST /api/users/invitations` calls the
   Supabase Admin API (`/auth/v1/admin/invite`) and provisions the platform rows with the returned subject.
   No endpoint accepts, generates or returns a password. ADR-027's rule stands: what changes is who calls
   Auth, not who owns the credential.
2. **The user sets their own password in the application.** A public `/auth/set-password` screen consumes the
   invitation token and calls `updateUser({ password })`. The same screen serves `type=recovery`, so password
   reset — which does not exist today — arrives with the same work. The requirement is **length: twelve
   characters minimum, with no composition rules** (NIST SP 800-63B), enforced in the screen and in the
   Supabase Auth project settings.
3. **The temporary session does not outlive the act.** On that route the token is exchanged explicitly
   (`exchangeCodeForSession` for the PKCE code; the implicit fragment for older links), so the global
   `detectSessionInUrl: false` and the in-memory-only token policy stay untouched. On success the screen
   **signs out that session** and redirects to `/login` with a confirmation, leaving no recovered session
   behind.
4. **`companyId` is required, and bounded by the caller's own company.** An account without a company is a
   defect, not a valid state. The API rejects a missing `companyId` with `400` and one that is not the
   authenticated administrator's with **`403`**, for every role: `user.manage` administers accounts, not
   tenancy.
5. **One operation, compensated and retry-safe.** Identity first (it produces the subject), platform rows
   second in one transaction. If the second step fails the identity is deleted; if that compensation also
   fails, the subject is logged under a stable code and reconciliation is manual. An existing identity for
   the same email is reused when it has no platform account; when it is already provisioned the answer is
   `409`, and a pending invitation answers "already sent" rather than issuing a second one. Re-sending is a
   later increment.
6. **The privileged key stays server-side.** The Auth client is a typed `HttpClient` with a fixed base
   address, so the `service_role` never reaches the browser, a log line or an error body.
7. **The invitation email is configured in the provider, in Spanish.** The template and the URL allow-list are
   a deployment step recorded in the runbook, not application code.

**Why not the alternatives:** creating the user with a temporary password makes the application handle a
credential and forces the UI to display it once; keeping the two manual steps is the friction this ADR
removes and leaves the `companyId` defect in place; letting `user.manage` cross companies would turn an
account-administering capability into a tenancy one, which the capability split exists to prevent; sending
our own mail would add an SMTP integration for a volume the built-in Supabase SMTP already covers.

**Consequences:** the API gains an outbound dependency on GoTrue on the request path of user creation, so a
GoTrue outage now fails the invitation (`502`) instead of a dashboard click. The email leaves before the
platform row exists; if the operation is compensated, the issued link leads to `no_profile` — fail-closed,
and accepted. Password reset becomes reachable, which the client trial needs in order to be supportable.
`Documents:SupabaseUrl/SecretKey` is renamed `Supabase:Url/Key` with a compatibility read, because the key
stops being about documents. Deleting a user still leaves the identity in Auth: that remains a separate
decision.

### ADR-031 — The comparison directory is every *published* agreement, and row-level security decides it *(#138b)*
**Context:** the comparison workspace was scoped to the caller's company: `ComparisonQueries.Candidates`
joined agreements on `agreement.CompanyId == companyId`, and the row-level policy required
`can_access_company(company_id)`. That makes the screen useless for the job the client describes. He
compares *his* convention against *another company's* — that is what the original system did, where the
directory listed CEMEX, MAPLOCA, NESTLE and EFE and the filters (sector, type, state) existed to choose
between them. Inside a single company every candidate shares the same sector, type and state, so a filter
by them can only pass everything or nothing. Published agreements were already world-readable through the
anonymous portal; the comparison was the one screen that could not see them.

**Decision:**
1. **Publication is the scope, not tenancy.** The candidate query and the clause projection load
   agreements whose state is `Published`, from any company. The tenant predicate is not removed from the
   business rules; it is replaced, for this read, by the publication predicate.
2. **Row-level security is the guarantee, not the handler.** A new policy
   (`agreement_api_read_published`) grants `app_api` select on rows in state `Published`, and
   `agreement_clause_api_read_published` follows its parent through an `EXISTS`. The existing tenant
   policies stay exactly as they were and are combined with OR, so the effective rule is "mine, or
   published". The database refuses the read even if a future handler forgets the predicate.
3. **Drafts stay invisible, indistinguishably.** A draft of another company is unreadable at the
   database, so a request naming it fails the count check and answers `404 not_found`, exactly like a
   missing id. Nothing in the response confirms that the draft exists.
4. **Nothing else widens.** Insert, update and delete remain tenant-scoped and capability-gated; the
   `authenticated` role is untouched (a direct Supabase client sees no change); and no other module
   (documents, audit, negotiation, news) is relaxed.
5. **Filters are applied on the server, before paging**, so `total` means "matching rows". The optional
   `companyId` remains, but only for a company the caller may administer: it is a convenience for "my
   company", not a way to probe which company ids exist.

**Why not the alternatives:** filtering in the client over the first page would make `total` and the
pagination lie as soon as the directory outgrows one page; widening every module's read policies would
turn a comparison requirement into a general loss of tenant isolation; duplicating published agreements
into a projection table would add a synchronisation problem to solve a permission problem.

**Consequences:** the comparison module is no longer tenant-scoped on read, and that is now its defining
trait — the ADR is what a future reader must check before adding a query there. The legal side of the
workspace (statute articles) was already company-independent, so the screen becomes coherent: agreements
and law are both reference material, and drafts are the only private thing. ADR-028 still holds: the
screen shows adjacency and never alignment. A published agreement becomes comparable by every tenant the
moment it is published, which is a product decision recorded here rather than an implementation detail.

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
| 0.2 | ADR-015 annotated with what T10 implemented: the single labour statute is guarded by the partial unique index, `Kind` is immutable, and the article heading is mandatory while its classification stays optional (A-12). |
| 0.3 | T10 review remediation: **ADR-023** records the two-headings rule so AC 3 and A-12 can both stand; **ADR-024** requires `comparison.read` **and** `statute.read` for the projection that returns statutory text; ADR-022 annotated with T10's query handler types and the remaining static-query exception. |
| 0.4 | T11: **ADR-025** settles the negotiation state machine (create in `Open`, explicit transitions, read-only closure, atomic and terminal promotion, explicit petition outcomes), hardens `app.guard_case_promotion()` (INSERT, source state, capability, no re-pointing or clearing) and adds company scope to the six negotiation tables. INV-N1 stays unimplemented pending a decision. |
| 0.5 | T12: **ADR-026** adds the public surface (allow-list projections, anonymous reads on the `anon` role), makes news publication a database-guarded transition that records its own actor and time, and applies A-33 sanitisation to clause, annex and news bodies. |
| 0.6 | T13: **ADR-027** makes the bootstrap one locking database function with no credential in any endpoint, adds user/role/audit administration with the anti-lockout 409s, and moves `AuditEntry` to the shared kernel so the audit query can filter on columns. |
| 0.7 | T14: **ADR-028** records the interface rules — capability-gated controls with the API as the guard, a comparison screen that claims adjacency and never alignment, a read-only published agreement behind a confirmed unpublish, and a portal that reads through a tokenless client. |
| 0.8 | P1-005: **ADR-029** records per-invocation metering and the shadow-by-default, opt-in-enforced budget model; the limit and overspend policy stay with the Product Owner. |
| 0.9 | T13 follow-up: **ADR-030** moves identity creation into the API by invitation — no credential in any endpoint, one `/auth/set-password` screen for invite and reset with the temporary session closed on success, `companyId` required and bounded to the caller's company (`403`), and the invitation email configured in the provider. |
| 1.0 | #138b: **ADR-031** makes the comparison a directory of published agreements — publication replaces tenancy as the scope of that read, row-level security grants `app_api` the published rows and nothing else, foreign drafts stay indistinguishable from missing ones, and the filters (sector, type, state) are applied on the server before paging. |
