# Constitution — Convenciones

**Version:** 1.0.0
**Status:** Ratified
**Ratified:** Task 1 (`task/1-project-constitution`)
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
6. **Administration** — master data, users, roles, permissions.
7. **Authentication & authorization** — via Supabase Auth, with PostgreSQL RLS as the row-level enforcement layer.

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
3. **Feature orientation.** Code is organised by business capability, not by technical layer alone. A feature owns its slice end to end.
4. **Explicit boundaries.** Modules communicate through application interfaces, never by reaching into another module's persistence or internal types.
5. **Complexity must be earned.** A pattern is adopted only when it removes a concrete, named pain. Speculative abstraction is a defect.

## Art. V — Data, schema & migrations

1. **Design, do not translate.** The PostgreSQL schema is designed from requirements. A table-for-table translation of the MySQL schema is prohibited.
2. **No data migration.** Legacy MySQL rows are never migrated. There is no ETL, no backfill, no import job. *(See Art. XIII, Open Decision D-1, for the one open question on reference catalogs.)*
3. **Constraints belong in the database.** Referential integrity, uniqueness, nullability, and state validity are declared as real constraints (`FK`, `CHECK`, `UNIQUE`, `NOT NULL`). The legacy reliance on implicit, application-only relationships is explicitly rejected.
4. **Types are honest.** Temporal values use `date`/`timestamptz`, never text. States use typed enumerations, never free strings. Legacy sentinels such as `'0000-00-00'` or numeric state codes stored as `char(3)` must not reappear.
5. **Migrations are code.** Every schema change is a committed EF Core migration. Destructive migrations require explicit review approval.
6. **Auditability is designed in.** Created/updated timestamps and an audit trail where a requirement calls for it. The legacy system had none; auditability is a new requirement, not a preserved behavior.

## Art. VI — Security & identity

1. **Least privilege.** Roles exist only to support a named requirement. The legacy roles (`Master`, `Operador`, `Transcriptor`) are evidence of intent, not a specification to reproduce.
2. **Authorization is server-side.** RLS and API-level checks are both enforced; a UI-only restriction is not a control.
3. **Secrets never enter Git.** Connection strings, keys, and tokens live in user-secrets or environment configuration. The legacy `local/configuracion.php` credentials are compromised-by-history and must never be reused.
4. **No remote mutation without approval.** Nothing is executed against remote Supabase — no migration, no schema change, no data write — without explicit Product Owner approval. Local environments are the default target.
5. **Personal data.** Legacy records contain identifiable individuals (representatives, contract holders). Such data is never committed to this repository and never seeded into development environments.

## Art. VII — Testing & CI

1. **No claim without execution.** A test may be reported as passing only if it was actually executed in that state of the tree. "Should work", "verified by inspection", and equivalent phrasing are not test results.
2. **Every slice is tested.** A feature slice ships with tests appropriate to its risk: domain logic by unit tests, API contracts by integration tests against a real PostgreSQL instance.
3. **CI is the gate.** GitHub Actions runs build and tests for every pull request. A red pipeline blocks merge.
4. **Fixtures are synthetic.** Test data is generated or hand-written, never copied from the legacy dump. Legacy PDFs and rows may be inspected privately as reference for designing fixtures; they are not committed.

## Art. VIII — Git & collaboration workflow

1. **`main` is protected by process.** No direct work on `main`.
2. **One task, one branch.** Every task begins by branching from `main` and ends with a pull request. Suggested prefix: `task/<n>-<slug>`.
3. **One task, one review.** A task is complete only when its PR is open and described. Merge is performed by the Product Owner; the next task does not begin until merge.
4. **Nothing is merged, pushed to a protected branch, or deployed without explicit authorization.**
5. **Commits are scoped and legible.** A commit message states what changed and why. Unrelated changes do not share a commit.
6. **History is not rewritten** on shared branches.

## Art. IX — The legacy codebase (reference only)

1. The legacy PHP application and the MySQL dump are **read-only evidence**. They are never modified, moved, or deleted, and the dump is never altered.
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
| D-1 | Whether a **curated, hand-reviewed seed** of reference catalogs (countries, states, municipalities, comparison headings) may ship, given Art. V.2 forbids data migration. The app is unusable without them. | **Open** — awaiting Product Owner |
| D-2 | Whether the legacy root `README.md` (which describes the legacy tree) is rewritten as the new project README, or left untouched as historical reference. | **Open** — awaiting Product Owner |

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
| English naming, translated domain terms | Art. X |
| No Tailwind CSS | Art. III |
