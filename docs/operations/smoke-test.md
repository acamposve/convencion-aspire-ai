# Release smoke gate — what must work before a deploy is accepted

**Status:** implemented and **executed on staging on 2026-11-22** (run
[45043883108](https://github.com/acamposve/convencion-aspire-ai/actions/runs/45043883108), commit
`a258a59`): all nine flows passed, the teardown left the tenant empty and the worker stayed disabled.
T19's third acceptance criterion is met; the evidence is in §6.

| Companion document | What it adds |
|---|---|
| [Runbook](runbook.md) | what the operator does when a flow fails and it is an incident |
| [Deployment](deployment.md) | how a release reaches staging and how it is rolled back |
| [Monitoring](monitoring.md) | the availability signals that are checked continuously, not per release |
| [Staging exercise](../tasks/task-16-legacy-removal.md) scripts in `scripts/staging/` | the #47 isolation exercise this gate reuses as a pattern |
| [T19](../tasks/task-19-production-readiness.md) | acceptance criterion 3 and its current state |

## 1. What the gate proves

T19 AC3 asks for: **sign in, upload, process, read text, author a clause, publish, compare — signed in —
plus the public portal anonymously, against the deployed environment, not a container.**

`scripts/staging/smoke.sh run` does exactly that against the deployed staging API, using one disposable
synthetic tenant, and then removes what it created.

| # | Flow | Request | Expected |
|---|---|---|---|
| 1 | Sign in | `POST {SUPABASE_URL}/auth/v1/token?grant_type=password` | `200` with an access token |
| 2 | Upload | `POST /documents` (multipart, `tests/fixtures/synthetic-contract.pdf`) | `201` |
| 3 | Extract | `POST /documents/{id}/extract` | `200` |
| 4 | Read text | `GET /documents/{id}/text` | `200`, non-trivial body |
| 5 | Process | `POST /documents/{id}/process`, then `GET /documents/{id}/processing` | `200`/`202`, then `Queued` |
| 6 | Author a clause | `POST /agreements` ×3 (two to compare, one draft only), `POST /agreements/{id}/clauses` ×2, `GET …/clauses` | `201`, `201`, `200` with the new clause |
| 7 | Publish | `POST /agreements/{id}/publish` for both compared agreements | `200` ×2 |
| 8 | Compare | `GET /comparison/?headingId=…&agreementIds=<a>,<b>` | `200` with **both** authored clause bodies (the comparison renders published agreements only) |
| 9 | Public portal (anonymous) | `GET /public/agreements` · `/public/agreements/{published}` · `/public/agreements/{draft}` | `200` · `200` · **`404`** |
| 10 | Heading contract guard | `GET /comparison` and `/comparison/candidates` with `headingId=not-a-guid` and with an unknown GUID | **`400` `validation_failed`** in both cases (#135) |

`process` stops at the queue boundary on purpose: the gate asserts the run reaches `Queued` **and** that
`platform.worker_heartbeat.enabled` is false **and** that no `Classify` row appeared in
`platform.ai_usage_record`. End-to-end processing (`Completed`, AI clauses) stays inside the authorized
worker window of the #47 exercise, because it spends model tokens on every release otherwise.

**Prerequisite: a comparable heading must exist.** Clauses must reference a heading that is not excluded,
and the comparison renders nothing under an excluded one. The reference seed ships a minimal curated
taxonomy ([`supabase/seed.sql`](../../supabase/seed.sql)); without it the gate stops with
`no comparison heading is available`, which is the correct behaviour for an environment where the
taxonomy has not been curated yet.

## 2. Safety posture

| Concern | How it is handled |
|---|---|
| **Real PII** | The only uploaded file is `tests/fixtures/synthetic-contract.pdf`; every record is named `SMOKE …`; nothing is copied out of the environment |
| **Target safety** | The script refuses any `STAGING_API_URL` other than `https://138-197-15-40.sslip.io/api` and exits `2` before touching anything |
| **Model spend** | The worker must stay disabled; the gate fails if it is enabled or if a `Classify` usage row appears |
| **Unavoidable cost** | Exactly **one** Document Intelligence extraction per run (a one-page synthetic PDF). The summary records the observed `Extract` usage rows so the cost is visible, not assumed |
| **Mutations** | Only the dedicated smoke tenant (company + user + role `smoke_tester`, capability-scoped). Nothing else in staging is written |
| **Quota backstop** | The tenant carries a 50-document / 2M-token quota; the teardown removes `processing_usage`, so the gate never approaches it |
| **Public visibility** | The published agreement is synthetic and is unpublished and deleted in the teardown step, which runs even when the flows fail (`if: always()`) |
| **Secrets** | The gate reuses the `staging` environment secrets; no new credential. The ephemeral password is stored only on the droplet (mode 600) and is never printed |
| **Rate** | One document, two agreements, one comparison per run. It is a release gate, not a load test |

## 3. How to run it

Only from `main`, by dispatch, with two confirmations (same convention as the exercise stages):

```bash
gh workflow run staging-smoke.yml --ref main -f mode=flows -f confirm=flows
```

The workflow (`.github/workflows/staging-smoke.yml`, job `flows`) runs, in order:

1. `smoke.sh teardown || true` — removes leftovers from a previous run;
2. `smoke.sh provision` — creates or reuses the smoke tenant and rotates its password;
3. `smoke.sh run` — the flows in §1;
4. `smoke.sh teardown` (`if: always()`) — unpublishes, deletes objects and rows, asserts zero;
5. a final check that `Worker__Enabled=false` is still set on the droplet.

The same steps can be run by hand with the `staging` environment variables exported:

```bash
bash scripts/staging/smoke.sh provision
bash scripts/staging/smoke.sh run
bash scripts/staging/smoke.sh teardown
```

`--dry-run` prints the request plan and exits without reading state or sending a request; it is what the
repository checks locally (no environment required).

`mode=read-only` (the default) keeps the pre-existing read-only verification: HTTPS, redirect, worker
state, configuration names, dotfile protection, provider reachability, recent failures and logs.

## 4. Cleanup, and why the identity survives

`teardown` unpublishes, deletes the tenant's storage objects through the service API, deletes its
documents, agreements, clauses, runs, attempts, extracted clauses, classifications, usage rows and
processing quota usage, and then **asserts that the tenant holds zero rows**.

The tenant, its account and the `smoke_tester` role are **not** deleted. That is not an oversight:

> `platform.audit_entry` is append-only (a `BEFORE UPDATE OR DELETE` trigger raises), and
> `audit_entry.actor_id` references `platform.user_account` with `ON DELETE SET NULL`. Deleting a user that
> has audit rows therefore makes PostgreSQL attempt an `UPDATE`, which the trigger rejects. Verified on
> 2026-11-22 against a migrated PostgreSQL 17 database:
> `ERROR: table platform.audit_entry is append-only and rejects UPDATE`.

So the gate keeps one reusable identity per environment and removes content instead. The role is
staging-only (`platform.role.key = 'smoke_tester'`, `is_system = false`) and is created by
[`smoke-tenants.sql`](smoke-tenants.sql), exactly like the exercise's `ai_staging_tester`; it cannot
administer users, companies, quotas or the platform.

**Related observation (not changed here):** `scripts/staging/teardown.sh` deletes `platform.user_account`
rows for the exercise users. Once those users have audit rows — for example after deleting a document
through the API, which is audited — that statement will fail the same way and abort the teardown block.
That belongs to #47 and is reported, not silently patched in this slice.

## 5. What the gate does not cover

- **Worker end-to-end**: `Completed`, extracted clauses, classifications and cost accounting are exercised
  by the #47 exercise inside an authorized window (P1-006 gate).
- **Negotiation, news, statutes, administration, users and roles**: covered by the automated suites, not by
  this gate.
- **Provider quality**: no evaluation of extraction or classification output (P1-006 owns that).
- **Capacity and latency**: the P2-005 harness owns them; this gate asserts correctness, not performance.
- **Multi-instance, TLS edge cases, DNS**: out of scope for a functional gate.
- **The restore path**: the [restore drill](restore-drill.md) owns it.

## 6. Evidence to attach to a release

Paste the workflow run URL and this table, filled from the run's step summary:

| Field | Value |
|---|---|
| Commit deployed | `<sha>` |
| Run URL | `<actions run>` |
| Flows 1–10 | pass / fail (the summary table has the observed and expected codes) |
| Worker during the run | `enabled=false` (asserted) |
| Classification calls | `0` (asserted) |
| Extraction calls | `1` (observed) |
| Teardown | `0` rows left in the tenant (asserted) |
| Portal afterwards | published agreement no longer served |

A release is accepted when every flow passes, the worker stayed disabled, no classification call was
made, and the teardown left nothing behind. A partial pass is a rejected release.

### 6.1 First recorded run

| Field | Value |
|---|---|
| Date | 2026-11-22 |
| Commit deployed | `a258a59` (deploy run [45043680544](https://github.com/acamposve/convencion-aspire-ai/actions/runs/45043680544)) |
| Gate run | [45043883108](https://github.com/acamposve/convencion-aspire-ai/actions/runs/45043883108), job steps 5–9 `success`, 51 s |
| Flows 1–9 | passed: sign in, upload (201), extract (200), read text, process → `Queued`, headings, author ×2, publish ×2, compare with both bodies, portal `200`/`200`/`404` |
| Worker | `enabled=f` asserted before, during and after |
| Classification calls | asserted `0` |
| Teardown | asserted the tenant holds `0` rows; storage objects deleted |
| Post-deploy edge | `/health/ready` 200, `/health/live` 200, `/health/ops` **404** at the edge |
| Scheduled ops probe | green before and after (runs 45043328469, 45042329640); no open `ops-alert` issue |

## 7. Known failure modes

| Symptom | Cause | Action |
|---|---|---|
| `409`/`400` on `POST /agreements` | Leftover agreement with the same start date from a failed run | Run `teardown` (the workflow does it first); the two agreements use different start dates on purpose |
| `expected Queued … got Completed` | The worker is enabled in staging | Stop the exercise window; the gate must run with the worker off |
| `classification calls` > 0 | Same as above | Same |
| `refusing: STAGING_API_URL is not the staging API` | The target guard fired | Point the gate at staging; never widen the guard |
| `400` on `POST /agreements/{id}/clauses` | The heading id came from `/comparison/headings`, which returns **groups** with nested headings; a group id is not a heading id | The gate picks a heading from a group without an economic-comparison requirement; every failure now prints the API problem document |
| `no smoke state` on `run` | `provision` was not executed | Run `provision` first |
| `teardown` fails on a row count | A foreign key still references the tenant's content | Read the failing count; the SQL block is ordered by dependency and adding a new table to a flow means adding it to the block |

## 8. Open items

| Item | Owner |
|---|---|
| First execution against staging (deployment window + dispatch) | Product Owner |
| Authorize the mutating stage in staging and the `smoke_tester` role | [owner sign-off #9](owner-signoffs.md) |
| Decide whether the gate should also run after every deploy automatically (today it is manual) | Product Owner |
