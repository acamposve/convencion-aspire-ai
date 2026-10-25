# Task records and the path to a complete MVP

One Markdown file per task. Created **before** implementation, updated as it proceeds, committed in the
same pull request as the task.

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
| T2 | [Identity and authorization](task-02-identity-authorization.md) | T1 | planned |
| T3 | [Tenant scoping and persistence concerns](task-03-tenant-scoping.md) | T2 | planned |

### Phase B — The document workflow *(the demonstrable spine)*

| # | Task | Depends on | Status |
|---|---|---|---|
| T4 | [Document upload and private storage](task-04-document-upload.md) | T3 | planned |
| T5 | [Document processing and extraction](task-05-document-processing.md) | T4 | **implemented** (#18) |
| T6 | [Document retrieval API](task-06-document-retrieval.md) | T5 | **implemented** (#19) |
| T7 | [Web: authentication and document workflow](task-07-web-documents.md) | T6 | planned |

### Phase C — The business domain *(F-01 … F-16)*

| # | Task | Depends on | Status |
|---|---|---|---|
| T8 | [Agreements and clauses](task-08-agreements-clauses.md) | T3 | planned |
| T9 | [Comparison](task-09-comparison.md) | T8 | planned |
| T10 | [Legal library](task-10-legal-library.md) | T3 | planned |
| T11 | [Negotiation workflow](task-11-negotiation.md) | T8 | planned |
| T12 | [Publishing and public portal](task-12-publishing-portal.md) | T9, T10 | planned |
| T13 | [Administration and audit](task-13-administration-audit.md) | T2 | planned |
| T14 | [Web: core domain, admin and portal](task-14-web-core.md) | T9, T11, T12, T13 | planned |

### Phase D — Operable and releasable

| # | Task | Depends on | Status |
|---|---|---|---|
| T15 | [CI pipeline](task-15-ci.md) | T2 | planned |
| T16 | [Legacy removal and credential rotation](task-16-legacy-removal.md) | — · **needs approval** | planned |
| T17 | [Aspire investigation](task-17-aspire-investigation.md) | — | planned |
| T18 | [Deployment](task-18-deployment.md) | T3, T7, T15 · **needs decisions + approval** | planned |
| T19 | [Operations: keep it running](task-19-production-readiness.md) | T18 | planned |

---

## Critical path

```
T1 (done) → T2 → T3 → T4 → T5 → T6 → T7    ← first usable product
                 └→ T8 → T9 ──┐
                 └→ T10 ──────┴→ T12 ─┐
                 └→ T11 ──────────────┴→ T14 → T18
```

T15, T16 and T17 are independent and can run in parallel with any of it. **T16 should not wait** — the
credentials are exposed today.

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
8. The repository contains no credential, and the exposed ones have been rotated.

## Open decisions blocking specific tasks

| Decision | Blocks | Question |
|---|---|---|
| **Q-4 interpretation** | T11 | Is `Agreed` the terminal state before promotion, or does promotion happen from `Closed`? The schema currently enforces "promotion implies `Closed`" |
| **Supabase project** | T7 (demonstration), T18 (deployment), production storage in T4 | Credentials and plan tier are listed in [task-18 §2–3](task-18-deployment.md). The local adapter keeps T3–T6 runnable without it |
| **Hosting, environments, domain, plan tier** | T18 | Five decisions in [task-18 §3](task-18-deployment.md) — cost and ownership choices, not technical ones |
| **Legacy removal** | T16 | Remove the tree and dump, or keep them out of the build only? Removing is irreversible for the dump |
| **CI enforcement** | T15 | Branch protection is unavailable on a private repo on the Free plan (HTTP 403). Upgrade, or accept the local hook as the only gate |

## History

| Task | Record | Outcome |
|---|---|---|
| T0 | audit of `92dfa3d` | `NOT RELEASABLE` — 8 blocking, 5 high findings ([report](../operations/release-readiness-audit.md)) |
| T1 | [task-01](task-01-document-isolation.md) | Two migrations, 32 + 30 assertions green, tenant rule corrected to `company.read_all` |
| T5 | [task-05](task-05-document-processing.md) | Synchronous extraction (`200`, not `202`) with 9 acceptance tests; the async design is recorded as deferred, not omitted |
| T6 | [task-06](task-06-document-retrieval.md) | Company-scoped list, metadata, text, streamed range download and `If-Match` soft delete; 13 acceptance tests |
