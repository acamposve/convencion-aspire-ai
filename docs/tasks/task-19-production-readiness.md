# T19 — Operations: keep it running

**Status:** in progress · **Slice A done (2026-11-22):** operator runbook and a timed local restore drill ·
**Slice C done (2026-11-22):** monitoring page, loopback-only `/health/ops` and the scheduled ops probe ·
**Slice B done (2026-11-22):** release smoke gate executed green on staging ·
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
| [`docs/operations/smoke-test.md`](../operations/smoke-test.md) | **written (slice B):** the release gate — sign in, upload, extract/text, queue, author, publish, compare, public portal — with its safety posture, cleanup and evidence template |
| [`docs/operations/monitoring.md`](../operations/monitoring.md) | **written (slice C):** the signals, the loopback-only reachability, the scheduled probe and its decision table, the thresholds still to be decided, and what is not measured |

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
| 2 | Every alert has a written first response someone else could follow | **met for the wired alerts:** [monitoring.md](../operations/monitoring.md) maps every probe condition to a [runbook](../operations/runbook.md) scenario. The probe covers database, queue and worker; provider outage and failed deployment are documented but not automated |
| 3 | Smoke test against the deployed environment | **met (2026-11-22):** [gate run 45043883108](https://github.com/acamposve/convencion-aspire-ai/actions/runs/45043883108) against commit `a258a59` passed all nine flows (sign in, upload, extract/text, process → `Queued` with the worker off, author, publish, compare, public portal anonymously), the teardown left the smoke tenant empty and the worker stayed disabled — [evidence](../operations/smoke-test.md#61-first-recorded-run) |
| 4 | `/health/ready` 503 alerts; `/health/live` distinguished | **met:** the scheduled [ops probe](../../.github/workflows/ops-probe.yml) fails P1 when readiness or liveness is not 200, asserts the edge answers `404` for `/health/ops` and that port 8081 is loopback-only ([monitoring.md](../operations/monitoring.md)). It has not run against staging yet (no deployment in slice C) |
| 5 | No log line contains a token, password or `service_role` key | **pending:** not checked in slice A |
| 6 | Rotation scheduled; T16 legacy credentials decommissioned | **partial:** a schedule is proposed for every credential in [runbook §7](../operations/runbook.md#7-credential-rotation-schedule-proposed), explicitly pending owner sign-off; T16 is recorded as not applicable |

## Risks

- **A restore plan that has never been executed** is the most common way a team discovers, mid-incident,
  that its backups are unusable.
- Health endpoints disclose whether the schema is present (audit M4); in a deployed environment they should
  be reachable only by the platform's health checker.
- Monitoring that nobody watches is decoration. If there is one operator, the alerts must be few, loud and
  actionable.
