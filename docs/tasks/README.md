# Task records and the path to a complete MVP

One Markdown file per task. Created **before** implementation, updated as it proceeds, committed in the
same pull request as the task.

**Current state (2026-11-22):** T1–T16 and T18 are implemented; T17 (Aspire) and T19 (operations) are
still open. The production-v1 audit's remediation is merged except the AI quality gate — see
[Production-readiness remediation](#production-readiness-remediation-production-v1-audit) below. The
decisions that need the Product Owner are collected in
[pending owner sign-offs](../operations/owner-signoffs.md).

## The rule

**No result is written before it was executed.** "Expected to pass" is not a result. If a check was not
run, the file says so.

## Convention

| Rule | Detail |
|---|---|
| File name | `task-NN-<slug>.md`, matching the task number below |
| Created | Before code, with objective, scope, planned files and risks |
| Updated | Every check actually run, with its real output — including the failures |
| Committed | In the same pull request as the task |
| Sections | Objective · Scope · Deliverables · Acceptance criteria · Risks · Status · Left undone |

---

## The plan

19 tasks. The MVP is complete when T1–T14 are done and T15–T19 have made it operable, safe and
deployable.

### Phase A — Security foundation *(nothing ships before this)*

| # | Task | Depends on | Status |
|---|---|---|---|
| T1 | [Document tenant isolation and Task 6 decisions](task-01-document-isolation.md) | — | **merged** (#9) |
| T2 | [Identity and authorization](task-02-identity-authorization.md) | T1 | **implemented** (#12) |
| T3 | [Tenant scoping and persistence concerns](task-03-tenant-scoping.md) | T2 | **implemented** (#14) |

### Phase B — The document workflow *(the demonstrable spine)*

| # | Task | Depends on | Status |
|---|---|---|---|
| T4 | [Document upload and private storage](task-04-document-upload.md) | T3 | **implemented** (#16) |
| T5 | [Document processing and extraction](task-05-document-processing.md) | T4 | **implemented** (#18) |
| T6 | [Document retrieval API](task-06-document-retrieval.md) | T5 | **implemented** (#19) |
| T7 | [Web: authentication and document workflow](task-07-web-documents.md) | T6 | **implemented** (#20) |

### Phase C — The business domain *(F-01 … F-16)*

| # | Task | Depends on | Status |
|---|---|---|---|
| T8 | [Agreements and clauses](task-08-agreements-clauses.md) | T3 | **implemented** (#21) |
| T9 | [Comparison](task-09-comparison.md) | T8 | **implemented** (#22) |
| T10 | [Legal library](task-10-legal-library.md) | T3 | **implemented** (#23) |
| T11 | [Negotiation workflow](task-11-negotiation.md) | T8 | **implemented** |
| T12 | [Publishing and public portal](task-12-publishing-portal.md) | T9, T10 | **implemented** |
| T13 | [Administration and audit](task-13-administration-audit.md) | T2 | **implemented** |
| T14 | [Web: core domain, admin and portal](task-14-web-core.md) | T9, T11, T12, T13 | **implemented** |

### Phase D — Operable and releasable

| # | Task | Depends on | Status |
|---|---|---|---|
| T15 | [CI pipeline](task-15-ci.md) | T2 | **implemented** (#30) |
| T16 | [Legacy removal and credential disposition](task-16-legacy-removal.md) | — | **implemented** (infrastructure decommissioned; rotation N/A) |
| T17 | [Aspire investigation](task-17-aspire-investigation.md) | — | planned |
| T18 | [Deployment](task-18-deployment.md) | T3, T7, T15 | **implemented** — live at `https://138-197-15-40.sslip.io` (worker disabled by default; last deploy 2026-11-14 at `97a2cac`) |
| T19 | [Operations: keep it running](task-19-production-readiness.md) | T18 | planned |

### Production-readiness remediation *(production-v1 audit)*

The 2026-11-08 audit ([executive summary](../audits/production-v1/00-executive-summary.md),
[roadmap](../audits/production-v1/10-remediation-roadmap.md)) produced findings F-01–F-15. Their
remediation lives in this directory. Status at 2026-11-22:

| Record | Finding | Status |
|---|---|---|
| [P1-001 atomic reservation](P1-001-atomic-reservation.md) | F-01 | implemented |
| [P1-002/003 recovery and fencing](P1-002-003-recovery-fencing.md) | F-02, F-03 | implemented |
| [P1-004 object deletion](P1-004-object-deletion.md) | F-04 | implemented |
| [P1-005 cost metering](P1-005-cost-metering.md) | F-05 | metering, reservations and shadow/enforced budgets implemented; **budget policy open** |
| P1-006 AI validation ([audit record](../audits/production-v1/tasks/P1-006-validacion-ia.md)) | F-06 | **partial** — evaluation framework and synthetic baseline; representative corpus and provider terms pending ([quality reports](../quality/F06-implementation-report.md)) |
| [P1-007 backup and restore](P1-007-backup-restore.md) | F-07 | implemented for the database; frequency, retention and RTO open |
| [P1-008 tenant isolation](P1-008-tenant-isolation-verification.md) | F-08 | implemented; policy finding F-08-1 open |
| [P1-009 observability](P1-009-observability.md) | F-09 | implemented; SLO values open |
| [P1-010 claim/delete](P1-010-claim-delete.md) | F-10 | investigated — race not reproduced; terminal guard added |
| [P2-001 clause pagination](P2-001-clause-pagination.md) | F-11 | implemented |
| [P2-002 UI start/error handling](P2-002-ui-error-start.md) | F-12 | implemented |
| [P2-003 deadline test](P2-003-test-deadline.md) | F-13 | implemented |
| [P2-004 attempt invariant](P2-004-attempt-invariant.md) | F-14 | implemented |
| [P2-005 capacity benchmark](P2-005-benchmark.md) | F-15 | measured; **capacity statement unsigned** |
| [A-02 architecture boundaries](A-02-architecture-boundaries.md) | — | implemented |

The decisions that need the Product Owner are listed in
[pending owner sign-offs](../operations/owner-signoffs.md).

---

## Critical path

```
T1 (done) → T2 → T3 → T4 → T5 → T6 → T7    ← first usable product
                 └→ T8 → T9 ──┐
                 └→ T10 ──────┴→ T12 ─┐
                 └→ T11 ──────────────┴→ T14 → T18
```

T15, T16 and T17 are independent and can run in parallel with any of it. **T16 is done** (legacy
tree and dump removed; the MySQL infrastructure was decommissioned, so rotation is not applicable — see
[operations/legacy-removal.md](../operations/legacy-removal.md) §4).

**T18 is the answer to "when do you need Supabase?"** Sign-in (T7) needs a real project to be
*demonstrable*; deployment needs it outright. The complete list of what is required, and how sensitive
each item is, is in [task-18 §2](task-18-deployment.md). T3–T6 keep running on the disposable container
and the local storage adapter, so nothing before T7 is blocked on it.

## Definition of done for the MVP

1. A new developer clones, runs one documented command, and gets a working stack.
2. A user signs in, uploads a contract, sees honest processing status, and reads the extracted text.
3. A user authors an agreement with numbered clauses, classifies them, publishes it, and compares it
   against another.
4. A negotiation case runs from `Open` to a promoted agreement.
5. An anonymous visitor reads published agreements and news, and nothing else.
6. A user of one company cannot read or write another company's data — proven over HTTP, not only in SQL.
7. CI runs build and tests on every pull request.
8. The repository contains no credential; the legacy credentials belonged to decommissioned infrastructure and need no rotation.

## Open decisions blocking specific tasks

| Decision | Blocks | Question |
|---|---|---|
| **INV-N1** (parties before opening) | negotiation UI | No state precedes `Open`; implementing it needs a decision, not a code change |
| **AI quality gate** (P1-006 / #52) | enabling the worker for real contracts | Representative corpus, acceptance thresholds and provider terms — see [owner sign-offs](../operations/owner-signoffs.md) |
| **Budget policy** (P1-005) | enforcing a spend ceiling | Who creates `ai_budget` rows, the default limit and the overspend rule — see [ADR-029](../architecture/decisions.md#adr-029--ai-costs-are-metered-per-invocation-and-budgets-are-shadow-by-default-opt-in-enforced-p1-005) |
| **`company.read_all` scope** (F-08-1) | tenant isolation model | Split aggregate oversight from raw-content access, or accept the current behaviour for platform administrators |
| **CI enforcement** | T15 | Branch protection is unavailable on a private repo on the Free plan (HTTP 403). Upgrade, or accept the local hook as the only gate |
| ~~**Supabase project**~~ | ~~T7, T18~~ | **Resolved** — provisioned; sign-in and private storage verified on staging |
| ~~**Hosting and domain**~~ | ~~T18~~ | **Resolved for staging** — DigitalOcean droplet behind Caddy with a `sslip.io` certificate. A production environment is not decided |
| ~~**Legacy removal**~~ | ~~T16~~ | **Resolved 2026-10-31** — approved: the tree and dump were removed (archive deleted); the Product Owner confirmed the MySQL infrastructure decommissioned, so rotation is not applicable |
| ~~**Q-4 interpretation**~~ | ~~T11~~ | **Resolved by ADR-025:** promotion is a separate operation for a `Closed` case and is terminal; a promoted case cannot be reopened |

## History

Figures below are the snapshot recorded by each task at the time; they are not current totals.

| Task | Record | Outcome |
|---|---|---|
| T0 | audit of `92dfa3d` | `NOT RELEASABLE` — 8 blocking, 5 high findings ([report](../operations/release-readiness-audit.md)) |
| T1 | [task-01](task-01-document-isolation.md) | Two migrations, 32 + 30 assertions green, tenant rule corrected to `company.read_all` |
| T5 | [task-05](task-05-document-processing.md) | Synchronous extraction (`200`, not `202`) with 9 acceptance tests; the async design is recorded as deferred, not omitted |
| T6 | [task-06](task-06-document-retrieval.md) | Company-scoped list, metadata, text, streamed range download and `If-Match` soft delete; 13 acceptance tests |
| T7 | [task-07](task-07-web-documents.md) | Vite/React/MUI document workflow: memory-only Supabase session, honest synchronous processing, retry and text viewer; 44 web tests |
| T8 | [task-08](task-08-agreements-clauses.md) | Agreement aggregate, integer clause numbering, publication with audit, composed title and tenant RLS; 12 acceptance tests |
| T9 | [task-09](task-09-comparison.md) | Read-only adjacency comparison: heading exclusion, published-only, economic note, bounded paging and cross-company 404; 7 acceptance tests |
| T10 | [task-10](task-10-legal-library.md) | Legal library: one labour statute guarded by the partial unique index, integer article numbering, mandatory article heading with an optional classification (ADR-023), company-independent reference data and statute articles in the comparison behind `statute.read` (ADR-024). Review remediation: partial `PATCH` semantics, `malformed_request` for unreadable bodies, agreement tenancy assertions R34–R35; 20 + 13 + 5 tests |
| T11 | [task-11](task-11-negotiation.md) | Negotiation workflow: create in `Open`, explicit transitions, read-only closure, atomic and terminal promotion behind `negotiation.promote` (ADR-025), a hardened database guard and company-scoped RLS; 21 + 19 tests, D5/D8 + R36–R40 |
| T12 | [task-12](task-12-publishing-portal.md) | Public portal: allow-list projections over `anon` RLS, news publication guarded in the database with a DB-recorded actor and time (ADR-026), A-33 sanitisation for clause/annex/news bodies; 10 + 10 tests, N1–N3 + R41–R44 |
| T13 | [task-13](task-13-administration-audit.md) | Administration and audit: one locking bootstrap function with no credential in any endpoint (ADR-027), users with live blocking and the anti-lockout 409s, the role matrix read and the audit trail behind `audit.read`; 5 + 12 tests, G1–G2 + R45–R47 |
| T14 | [task-14](task-14-web-core.md) | Web MVP: capability-gated navigation with the API as the guard (ADR-028), agreement and clause editor with the confirmed-unpublish gate, adjacency comparison that claims no alignment, legal library, negotiation stages, news, users/roles/audit, and a tokenless public portal; 44 + 15 web tests |
| T15 | [task-15](task-15-ci.md) | CI gate: four jobs (Release build with warnings as errors + 64 unit/154 integration tests, 19 migrations applied to PostgreSQL 17 and verified by query, web typecheck/59 tests/production build with bundle scan, secret scan); ~5.1 min; vitest per-test timeout raised to 20 s after CI exposed the flake |
| T16 | [task-16](task-16-legacy-removal.md) | Legacy removal: 1,869 tracked files (PHP tree + 48.9 MB dump) removed from the tree because they carried 4 credential sets and personal data; analysis retained in `docs/legacy`/`docs/database`; `tools/scan-secrets.ps1` returns CLEAN; the legacy material was then purged from every ref (filter-repo, force-push); constitution Art. IX.1 amended (v1.3.1); the MySQL infrastructure was decommissioned, so rotation is not applicable |
| T18 | [task-18](task-18-deployment.md) | Staging on a DigitalOcean droplet + Supabase (session pooler, Auth, private Storage bucket): Docker images, compose, nginx, deploy workflow with migration and rollback; verified sign-in, upload + extraction (27 KB PDF, 470 chars), cross-tenant 404, firewall 22/80 only, reboot recovery, no secret in the bundle. $12/month. HTTP only (no IP certificates) |
