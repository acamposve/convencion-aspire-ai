# T19 — Operations: keep it running

**Status:** planned · **Depends on:** T18 (deployment) · **Blocks:** trusting the system with real data

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
| `docs/operations/runbook.md` | Restore, roll back, respond to each alert |
| `docs/operations/smoke-test.md` | The release gate: what must work before a deploy is accepted |
| `docs/operations/monitoring.md` | What is measured, what alerts, who is told |

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

## Risks

- **A restore plan that has never been executed** is the most common way a team discovers, mid-incident,
  that its backups are unusable.
- Health endpoints disclose whether the schema is present (audit M4); in a deployed environment they should
  be reachable only by the platform's health checker.
- Monitoring that nobody watches is decoration. If there is one operator, the alerts must be few, loud and
  actionable.
