# Pending owner sign-offs and decisions

**Purpose:** the engineering work behind these items is done, or was deliberately stopped at the point
where the next step is a choice, not an implementation. Each needs an explicit Product Owner decision
before the system may be treated as releasable. None can be satisfied by code that guesses the answer.

*Status as of 2026-11-22.*

## 1. Release and acceptance sign-offs

| # | Item | What is needed | Evidence to decide on | Blocks |
|---|---|---|---|---|
| 1 | **P2-005 capacity statement** | Ratify, amend or reject the proposed initial benchmark targets and the measured baseline | [capacity report](p2-005-capacity.md) · [task record](../tasks/P2-005-benchmark.md) · two independent runs on 2026-11-22 | Any capacity or 10,000-documents/month claim in a release note |
| 2 | **P1-006 AI quality gate and provider terms** | Approve a representative, human-annotated corpus; sign off the [36-clause verification worksheet](../quality/review/synthetic-v3-candidate.verification.json); accept or change the proposed thresholds; approve the Azure/Mistral terms and the synthetic-only policy | [P1-006 plan](../tasks/P1-006-ai-quality-validation.md) · [verification report](../quality/review/synthetic-v3-candidate.verification.md) · [evaluation framework](../quality/F06-implementation-report.md) · [AI evaluation](ai-evaluation.md) · [synthetic baseline](../quality/results/baseline-v2.report.md) · [security acceptance](ai-security-acceptance.md) | Enabling the worker for real contracts; the pilot (#52) |
| 3 | **P1-007 backup policy** | Choose backup frequency and retention, managed PITR, whether storage backup is scheduled, and the RTO to rehearse. A measured local baseline now exists ([restore drill](restore-drill.md)): ratify or replace it, and decide whether the production-target drill is required before release | [P1-007 record](../tasks/P1-007-backup-restore.md) · [backup runbook](p1-007-backup-restore.md) · [restore drill](restore-drill.md) | T19 restore drill; a production release |
| 4 | **P1-009 SLO thresholds** | Set the queue-age and worker-heartbeat thresholds, the latency percentile, and who receives the alert | [P1-009 record](../tasks/P1-009-observability.md) · [observability runbook](p1-009-observability.md) | Alerting; any availability claim |
| 5 | **#47 staging drill** | Authorise the two-tenant staging exercise and the kill-switch drill | [staging AI runbook](staging-ai-runbook.md) · [isolation matrix](../security/p1-008-tenant-isolation-matrix.md) · issue #47 | Closing the live evidence for real contracts |

## 2. Policy decisions

| # | Item | What is needed | Evidence |
|---|---|---|---|
| 6 | **P1-005 budget policy** | Who creates `platform.ai_budget` rows and through what surface (API, administration screen, SQL runbook); the monthly limit; the overspend rule for an in-flight run; the alert threshold | [P1-005](../tasks/P1-005-cost-metering.md) · [ADR-029](../architecture/decisions.md#adr-029--ai-costs-are-metered-per-invocation-and-budgets-are-shadow-by-default-opt-in-enforced-p1-005) |
| 7 | **F-08-1 `company.read_all` scope** | Split aggregate oversight from raw-content access (for example an audited `support.access`), or accept the current behaviour for platform administrators | [isolation matrix, findings](../security/p1-008-tenant-isolation-matrix.md) |
| 8 | **Credential rotation schedule** | Accept, shorten or replace the proposed intervals for the SSH deploy key, the Supabase database password, the Supabase secret key and the two Azure keys; name the private place where rotations are recorded. Nothing in the proposal has been executed | [operations runbook §7](runbook.md#7-credential-rotation-schedule-proposed) |
| 9 | **Release smoke gate authorization** | Authorize the mutating `flows` stage on staging: it creates, publishes and then deletes synthetic data in a dedicated tenant with the `smoke_tester` role, leaves a short public window for the published synthetic agreement, and makes one Document Intelligence call per run. Decide whether the gate must run after every deploy or only at release time | [smoke test](smoke-test.md) · [workflow](../../.github/workflows/staging-smoke.yml) | Using the gate as a release condition |

## 3. Already decided (for context)

| Decision | Outcome |
|---|---|
| Staging host and address | DigitalOcean droplet at `https://138-197-15-40.sslip.io` — Caddy terminates TLS, port 80 redirects |
| Worker default | `Worker__Enabled=false`; enabled only in scoped, allowlisted windows with a deadline and a watchdog |
| Supabase | provisioned — Auth, PostgreSQL (session pooler) and a private Storage bucket |
| Legacy credentials | infrastructure decommissioned; rotation not applicable |

## 4. Recording a decision

Write the decision in the item's task record (or the ADR it points to) with the date, the actor and the
chosen option, then update the status here and in [the task index](../tasks/README.md). An item is done
when the decision exists, not when the implementation does.
