# Constitution — Convenciones

**Version:** 1.3.0
**Status:** Ratified
**Ratified:** Task 1 (`task/1-project-constitution`)
**Amended:** 1.1.0 — Task 1, second review. Resolved D-1 (reference catalog seed) and D-2 (root
README); added Art. V.3 and Art. VI.6.
**Amended:** 1.3.0 — T16. The legacy PHP tree and the MySQL dump were **removed from the
repository** (they carried committed credentials and personal data); Art. IX.1 now records that the
committed analysis is the retained evidence and that the removed material is archived outside Git.
See the [amendment log](#amendment-log) and
[operations/legacy-removal.md](operations/legacy-removal.md).
**Supersedes:** the legacy `constitucion.md` referenced by the historical README, which does not exist in this repository and is treated as withdrawn.

This document is the highest authority for engineering decisions in this project. Any
practice, dependency, or design that conflicts with an article here is a defect, not a
preference. Where this document is silent, the decision must be *recorded* (Art. XIII)
rather than assumed.

Article numbering preserves the two references made by the legacy README — **Art. IX**
(legacy codebase) and **Art. XI.2** (provenance and reuse rights) — so historical notes
remain checkable.

---

## Art. I — Purpose

Convenciones is a **contract intelligence platform** for collective bargaining agreements
(*convenciones colectivas*): the authoring and curation of agreement text and its clauses,
their comparison against labour legislation and against other agreements, the negotiation
trail that produced them, and public publication of the results.

The legacy PHP application is a **source of functional knowledge**, never a codebase to port.

## Art. II — Scope of the MVP

In scope, confirmed by the Product Owner:

1. **Collective agreements** — agreement records, clauses with full text and summary, annexes, publication state.
2. **Negotiation workflow** — union petitions, employer offers, meetings, and resulting agreements, with close/reopen lifecycle. *Approved for the MVP.*
3. **Legal library** — labour law and other statutes with article-level records. *In scope as a feature, but starting empty:* no legacy statute content, PDFs, or articles are carried over.
4. **Clause comparison** — the intelligence core: linking equivalent clauses across agreements and to legal articles by a shared comparison heading.
5. **Public portal** — published agreements and news.
6. **Administration** — master data, users, roles, permissions, all managed inside the application.
7. **Authentication & authorization** — via Supabase Auth, with PostgreSQL RLS as the row-level enforcement layer. Exactly one **SuperAdmin** is provisioned at first run; every other account is created in-app (Art. VI.6).

Out of scope for the MVP: any data migration from MySQL, microservices, mobile applications,
offline operation, third-party integration without a named requirement.

## Art. III — Mandated technical baseline

These are constraints, not suggestions. Deviating requires an approved amendment (Art. XIV).

| Concern | Mandate |
|---|---|
| Backend | C# / ASP.NET Core on .NET |
| Orchestration & observability | .NET Aspire |
| Database | PostgreSQL hosted on Supabase |
| Data access | Entity Framework Core with Npgsql |
| Frontend | React + Vite + Material UI (MUI) |
| Styling | **No Tailwind CSS.** MUI's styling system only. |
| Identity | Supabase Auth; `Storage` for binary assets; RLS for row-level rules |
| Testing & CI | Automated tests, GitHub Actions |
| Schema | Version-controlled migrations committed to Git |

No package, framework, or service enters the solution without a requirement that justifies
it (Art. XII.1).

## Art. IV — Architecture rules

1. **Modular monolith.** A single deployable backend. No microservices for the MVP.
2. **Clean Architecture.** Dependencies point inward: Domain ← Application ← Infrastructure/API. The Domain layer references no framework, no EF Core types, and no Supabase SDK.
3. **Domain-Driven Design is mandatory, applied where it earns its place.** Business capabilities are modelled as bounded contexts with aggregates, entities, value objects, invariants, and domain services.
   - An **aggregate** is used wherever an invariant spans several rows. An "aggregate" with no invariant spanning its parts is a defect, not a pattern.
   - Plain entities and application services are used where CRUD genuinely suffices. Wrapping reference data in aggregates, repositories, and domain events is prohibited.
   - Invariants are enforced in the domain model **and** asserted by database constraints. Neither layer may be the only guard.
4. **CQRS is mandatory.** Reads and writes are separated in code, as distinct handler types that never share a model.
   - **Commands** change state, load aggregates, enforce their invariants, and write through the aggregate.
   - **Queries** never load aggregates and never mutate. They project directly into read models.
   - Each use case is exactly one command handler or one query handler, owning its request and response types.
   - CQRS is applied **in code, not at the infrastructure level.** The MVP uses one database and one `DbContext`. A separate read store, materialised read models, or event sourcing requires an amendment (Art. XIV): no requirement justifies it yet, and the cost is real.
   - **No mediator library and no command bus are implied.** An endpoint calls the handler directly. Adding a dispatcher must remove a named, concrete pain — dispatch variability, never aesthetics.
5. **Feature orientation.** Code is organised by business capability, not by technical layer alone. A feature owns its slice end to end.
6. **Explicit boundaries.** Modules communicate through application interfaces, never by reaching into another module's persistence or internal types.
7. **Complexity must be earned.** Any pattern **beyond** those mandated in IV.3 and IV.4 is adopted only when it removes a concrete, named pain. Speculative abstraction is a defect.

## Art. V — Data, schema & migrations

1. **Design, do not translate.** The PostgreSQL schema is designed from requirements. A table-for-table translation of the MySQL schema is prohibited.
2. **No data migration.** Legacy MySQL business records are never migrated. There is no ETL, no backfill, no import job. Reference catalogs are the single, bounded exception defined in Art. V.3.
3. **Reference catalogs may be seeded.** Countries, states, municipalities, comparison headings, and similar reference data ship as a **curated, hand-authored dataset** that is reviewed as source code and committed with the migrations. This is reference data authored for the new system — never a MySQL export, never business records, never personal data. Any seeded catalog must be individually justifiable; seeding is a convenience for an empty database, not a migration in disguise.
4. **Constraints belong in the database.** Referential integrity, uniqueness, nullability, and state validity are declared as real constraints (`FK`, `CHECK`, `UNIQUE`, `NOT NULL`). The legacy reliance on implicit, application-only relationships is explicitly rejected.
5. **Types are honest.** Temporal values use `date`/`timestamptz`, never text. States use typed enumerations, never free strings. Legacy sentinels such as `'0000-00-00'` or numeric state codes stored as `char(3)` must not reappear.
6. **Migrations are code.** Every schema change is a committed EF Core migration. Destructive migrations require explicit review approval.
7. **Auditability is designed in.** Created/updated timestamps and an audit trail where a requirement calls for it. The legacy system had none; auditability is a new requirement, not a preserved behavior.

## Art. VI — Security & identity

1. **Least privilege.** Roles exist only to support a named requirement. The legacy roles (`Master`, `Operador`, `Transcriptor`) are evidence of intent, not a specification to reproduce.
2. **Authorization is server-side.** RLS and API-level checks are both enforced; a UI-only restriction is not a control.
3. **Secrets never enter Git.** Connection strings, keys, and tokens live in user-secrets or environment configuration. The legacy `local/configuracion.php` credentials are compromised-by-history and must never be reused.
4. **No remote mutation without approval.** Nothing is executed against remote Supabase — no migration, no schema change, no data write — without explicit Product Owner approval. Local environments are the default target.
5. **Personal data.** Legacy records contain identifiable individuals (representatives, contract holders). Such data is never committed to this repository and never seeded into development environments.
6. **Bootstrap once, then delegate.** A fresh deployment provisions **exactly one SuperAdmin** at first run; this is the only account created outside the application. Requirements:
   - No default, hard-coded, or committed credential. The bootstrap credential is supplied at setup time (environment/user-secret) or through a one-time claim flow, and is never printed to logs or stored in the repository.
   - The bootstrap path is **idempotent**: re-running it must not create a second SuperAdmin or overwrite an existing one.
   - The bootstrap mechanism is **disabled or inert once a SuperAdmin exists**, so it cannot be replayed as an escalation path.
   - Every other user, administrator, role, and permission is created **inside the application** by an authorised administrator. There are no further out-of-band accounts, and no seeded users of any kind.

## Art. VII — Testing & CI

1. **No claim without execution.** A test may be reported as passing only if it was actually executed in that state of the tree. "Should work", "verified by inspection", and equivalent phrasing are not test results.
2. **Every slice is tested.** A feature slice ships with tests appropriate to its risk: domain logic by unit tests, API contracts by integration tests against a real PostgreSQL instance.
3. **CI is the gate.** GitHub Actions runs build and tests for every pull request. A red pipeline blocks merge.
4. **Fixtures are synthetic.** Test data is generated or hand-written, never copied from the legacy dump. Legacy PDFs and rows may be inspected privately as reference for designing fixtures; they are not committed.

## Art. VIII — Git & collaboration workflow

1. **`main` is protected, and the protection is enforced, not merely intended.**
   - **Required settings on `main`:** a pull request is required before merging; force pushes are refused; branch deletion is refused; required approvals are `0` only because this repository has a single human reviewer, who cannot approve their own PR.
   - **The Product Owner is the only account permitted to override a blocked merge.** No automation, agent, or third party may bypass, force-push, or merge on their behalf.
   - **Where the hosting plan cannot enforce these settings, the equivalent guard is installed locally** as a committed `pre-push` hook, and the gap is recorded in [operations/repository-settings.md](operations/repository-settings.md). A hosting limitation changes *where* the rule is enforced; it never relaxes the rule.
   - **Every change reaches `main` through a pull request**, including documentation, configuration, and one-line fixes. There is no exception for "trivial" work.
2. **One task, one branch.** Every task begins by branching from `main` and ends with a pull request. Suggested prefix: `task/<n>-<slug>`.
3. **One task, one review.** A task is complete only when its PR is open and described. **Merge is performed by the Product Owner and by nobody else**; the next task does not begin until merge. An agent never merges its own pull request, and never merges any pull request.
4. **Nothing is merged, pushed to a protected branch, or deployed without explicit authorization.**
5. **Commits are scoped and legible.** A commit message states what changed and why. Unrelated changes do not share a commit.
6. **History is not rewritten** on shared branches. Force-pushing `main` is blocked, not merely discouraged.

## Art. IX — The legacy codebase (reference only)

1. **The legacy PHP application and the MySQL dump were read-only evidence while they were in the
   repository. They were removed from it in T16 (2026-10-31)**, because they carried committed
   database credentials (Art. VI.3) and personal data (Art. VI.5, Art. VII.4) that must not be
   versioned. The retained evidence is the committed analysis under `docs/legacy/` and
   `docs/database/`; the removed material is archived outside Git and is never a build input. The
   dump is never altered. The material was purged from this repository's history on 2026-10-31 (every
   ref rewritten and force-pushed), but pull-request refs, existing clones and the off-repository
   archive may still contain it, so the credentials must still be rotated (see
   [operations/legacy-removal.md](operations/legacy-removal.md)).
2. **No PHP code is reused as production implementation.** No file is copied, transliterated, or mechanically translated into C#. Behavior is understood, then independently redesigned.
3. Legacy data may be inspected to understand the data model and to design better test fixtures. It is not migrated (Art. V.2).
4. Legacy business behavior that is *useful* is preserved in intent and redesigned in implementation.
5. Behavior that exists in legacy code but is unsupported by a stated requirement is not automatically carried forward; it is raised as a question.

## Art. X — Language & naming

1. **All code, identifiers, schema objects, comments, commit messages, and documentation are in English.**
2. **User-facing copy is Spanish**, matching the domain and its legal vocabulary (*convención colectiva*, *cláusula*, *patrono*, *sindicato*).
3. Domain terms are translated, not transliterated. A Spanish domain concept maps to the closest accurate English term (e.g. `articulos_contratos` → `AgreementClause`), with the Spanish term recorded in documentation where the mapping is not obvious.
4. Database identifiers are `snake_case` (PostgreSQL convention); C# identifiers follow standard .NET conventions.

## Art. XI — Provenance & reuse rights

1. **Independence.** The new implementation is written from requirements and behavioral understanding. No legacy artifact is a build input.
2. **Reuse rights.** *(This resolves Art. XI.2 of the withdrawn legacy constitution.)* No legacy code is reused, so no code-reuse right is required. Legacy PDFs and documents may be read privately for reference and fixture design only; they are **not committed** to this repository. Any broader reuse of legacy content remains unresolved and must be escalated before use.
3. **Provenance is recorded.** When behavior derives from observation of the legacy system, the analysis document says so and identifies the evidence.

## Art. XII — Documentation & complexity

1. **Documentation must pay for itself.** No document is written that does not change a decision or prevent a misunderstanding. Rule-of-thumb: if nobody would consult it twice, it is not written.
2. **Decisions are recorded.** Non-obvious architectural or product decisions are captured as short records with context, decision, and consequences.
3. **Uncertainty is documented, never resolved silently.** Unknowns are logged as open decisions (Art. XIII).
4. **No invented business rules.** If a rule is not stated by the Product Owner and not evidenced in legacy behavior, it is a question, not an implementation.

## Art. XIII — Open decisions

Unresolved items that could affect the product. They block nothing until the phase that needs them.

| ID | Decision | Status |
|---|---|---|
| D-1 | Whether a **curated, hand-reviewed seed** of reference catalogs (countries, states, municipalities, comparison headings) may ship, given Art. V.2 forbids data migration. The app is unusable without them. | **Resolved 1.1.0** — permitted, bounded by Art. V.3 |
| D-2 | Whether the legacy root `README.md` (which describes the legacy tree) is rewritten as the new project README, or left untouched as historical reference. | **Resolved 1.1.0** — `README.md` rewritten as the project README; original content archived at [legacy/README.md](legacy/README.md) with corrections and no content lost |

No open decisions at this time.

## Art. XIV — Amendment

1. Any article may be amended by a pull request that states the article, the change, and the reason.
2. Amendments require Product Owner approval and are recorded with a version bump.
3. Silent deviation is prohibited: if practice and this document disagree, one of them must change in a reviewable commit.

---

## Appendix A — Mandatory rules traceability

| Rule stated at project restart | Where it lives |
|---|---|
| Do not reuse PHP code as production implementation | Art. IX.2, Art. XI.1 |
| Preserve useful legacy behavior while redesigning implementation | Art. IX.4, Art. I |
| Do not migrate data from MySQL; dump is for analysis only | Art. V.2, Art. IX.3 |
| Design a new PostgreSQL schema; no mechanical translation | Art. V.1, Art. V.3 |
| Never modify or delete legacy files or the dump | Art. IX.1 |
| No remote Supabase changes without approval | Art. VI.4 |
| Do not implement features without clear requirements | Art. XII.4 |
| Never invent business rules; document uncertainty | Art. XII.3, Art. XIII |
| No microservices for the MVP | Art. IV.1 |
| Avoid unnecessary complexity, dependencies, documentation | Art. IV.5, Art. XII.1 |
| Work in small verifiable phases, stop for approval | Art. VIII.3 |
| Branch per task, PR per task, never work on `main` | Art. VIII.1, Art. VIII.2 |
| Never claim a test passed unless actually executed | Art. VII.1 |
| Modular monolith, Clean Architecture, feature-oriented | Art. IV |
| **Use Domain-Driven Design** | **Art. IV.3** |
| **Use CQRS** | **Art. IV.4** |
| **`main` is protected; every change arrives through a pull request; the Product Owner is the only account that may override or merge** | **Art. VIII.1, Art. VIII.3** |
| English naming, translated domain terms | Art. X |
| No Tailwind CSS | Art. III |

---

## Amendment log

| Version | Change | Articles touched | Authority |
|---|---|---|---|
| 1.0.0 | Ratified | — | Product Owner |
| 1.1.0 | Resolved D-1: reference catalogs may be seeded from curated data | Art. V.2, **Art. V.3 (new)**, Art. XIII | Product Owner |
| 1.1.0 | Resolved D-2: root README rewritten, legacy description archived | Art. XIII | Product Owner |
| 1.2.0 | **Domain-Driven Design made mandatory** where invariants span aggregates | **Art. IV.3 (new)** | Product Owner |
| 1.2.0 | **CQRS made mandatory** in code (commands and queries as separate handlers); infrastructure-level CQRS still requires an amendment | **Art. IV.4 (new)**, Art. IV.7 | Product Owner |
| 1.2.0 | `main` protection defined as an enforced rule with a local-hook fallback when the hosting plan cannot enforce it; Product Owner is the sole merge/override authority | **Art. VIII.1**, Art. VIII.3, Art. VIII.6 | Product Owner |
| 1.1.0 | Added SuperAdmin first-run bootstrap; all other accounts in-app | Art. II.6–7, **Art. VI.6 (new)** | Product Owner |
| 1.3.0 | Legacy tree and MySQL dump removed from the repository (T16); Art. IX.1 amended so the committed analysis is the retained evidence and the removed material is archived outside Git. Reason: committed credentials (Art. VI.3) and personal data (Art. VI.5, Art. VII.4, Art. XI.2). Deletion does not rotate credentials; rotation is outstanding | **Art. IX.1**, Art. VI.3, Art. VI.5 | Product Owner (T16 approval) |
