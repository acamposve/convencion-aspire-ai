# T19 — Operations: keep it running

**Status:** in progress · **Slice A done (2026-11-22):** operator runbook and a timed local restore drill ·
**Depends on:** T18 (deployment) · **Blocks:** trusting the system with real data

## Objective

Deployment gets the MVP to a URL. This task makes it something you can leave running: it can be
restored, observed, and recovered — and the team knows what to do at 2am.

## Scope

**In:** backup and a **rehearsed** restore; monitoring and alerting on the probes and on error rate;
incident and rollback runbooks; a release smoke test that gates every deployment; a secret rotation
schedule (including the T16 credentials); log retention and what must never be logged; a documented
on-call expectation, even if the on-call is one person.

**Out:** deployment itself (T18); the features; capacity planning beyond a first estimate.

## Deliverables

| File | Purpose |
|---|---|
| [`docs/operations/runbook.md`](../operations/runbook.md) | **written (slice A):** first response for each P1-009 failure scenario, rollback, restore, worker kill-switch, escalation paths, proposed credential-rotation schedule |
| [`docs/operations/restore-drill.md`](../operations/restore-drill.md) | **written (slice A):** the rehearsed, timed `create → verify → restore → check` drill and its caveats |
| `docs/operations/smoke-test.md` | The release gate: what must work before a deploy is accepted — **pending** |
| `docs/operations/monitoring.md` | What is measured, what alerts, who is told — **pending** |

## Acceptance criteria

1. **A restore is rehearsed and timed**, with the measured duration recorded. An untested backup is not a
   backup — and the first time anyone tests one should not be during an incident.
2. Every alert has a written first response that someone other than its author could follow.
3. The smoke test covers sign in, upload, process, read text, author a clause, publish, compare — signed
   in — plus the public portal anonymously. It runs against the deployed environment, not a container.
4. `/health/ready` returning 503 raises an alert; `/health/live` returning 200 after a failure is
   distinguished from a working service.
5. No log line contains a token, a password or a `service_role` key — checked by looking at real output.
6. Rotation is scheduled for every credential, with the T16 legacy credentials recorded as decommissioned
   (no rotation applicable).

## Progress against the acceptance criteria (2026-11-22)

| # | Criterion | State |
|---|---|---|
| 1 | A restore is rehearsed and timed | **met locally:** the full `create → verify → restore → check` lifecycle ran in 35.74 s on a production-shaped synthetic set — [restore drill](../operations/restore-drill.md). The measurement against a Supabase target is still open ([owner sign-off #3](../operations/owner-signoffs.md)) |
| 2 | Every alert has a written first response someone else could follow | **partial:** the [runbook](../operations/runbook.md) documents first response for database, worker, queue, provider and deployment failures; the alerts themselves are not wired yet |
| 3 | Smoke test against the deployed environment | **pending** — not part of slice A |
| 4 | `/health/ready` 503 alerts; `/health/live` distinguished | **pending:** the signals exist ([P1-009](../operations/p1-009-observability.md)), but nothing alerts on them, and `/health/ops` is not reachable from a probe as deployed ([runbook §2.1](../operations/runbook.md#21-reaching-healthops)) |
| 5 | No log line contains a token, password or `service_role` key | **pending:** not checked in slice A |
| 6 | Rotation scheduled; T16 legacy credentials decommissioned | **partial:** a schedule is proposed for every credential in [runbook §7](../operations/runbook.md#7-credential-rotation-schedule-proposed), explicitly pending owner sign-off; T16 is recorded as not applicable |

## Risks

- **A restore plan that has never been executed** is the most common way a team discovers, mid-incident,
  that its backups are unusable.
- Health endpoints disclose whether the schema is present (audit M4); in a deployed environment they should
  be reachable only by the platform's health checker.
- Monitoring that nobody watches is decoration. If there is one operator, the alerts must be few, loud and
  actionable.
