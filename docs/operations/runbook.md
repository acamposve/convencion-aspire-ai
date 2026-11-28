# Operations runbook — first response, rollback, restore and rotation

**Status:** T19, slice A. This page records what an operator does when something breaks, and what was
measured before it was written. Every scenario below is derived from an implemented signal, script or
procedure in this repository; nothing here is a description of a monitoring stack that does not exist.

| Companion document | What it adds |
|---|---|
| [P1-009 observability](p1-009-observability.md) | the signals, the endpoint contract, the metric names |
| [P1-007 backup and restore](p1-007-backup-restore.md) | the backup tool, the restore procedure, what is not protected |
| [Restore drill](restore-drill.md) | the measured `create → verify → restore → check` timing on a production-shaped set |
| [Deployment](deployment.md) | how a release happens and how it is rolled back |
| [Temporary worker activation](worker-temporary-activation.md) | the bounded activation window and its watchdog |
| [Pending owner sign-offs](owner-signoffs.md) | the decisions this runbook is still waiting for |

**What this runbook is not yet:** "an alert fires" below means "a signal changes and a human is watching
it", not "a page reached a phone". The repository ships no paging integration, and neither the scheduled
[ops probe](monitoring.md) nor the [release smoke gate](smoke-test.md) has run against staging yet
([P1-009 §9](p1-009-observability.md#9-limitations)).

## 1. Severity and expectations

| Level | Meaning | Response expectation |
|---|---|---|
| **P1** | Users cannot sign in, upload, read documents or publish; or data is at risk | Start immediately, regardless of hour. Open an incident note, contain, then fix |
| **P2** | A workflow is degraded but the system serves (processing backlog, one provider failing, retries) | Start within the working day; keep the queue visible |
| **P3** | Cosmetic, slow-burn or a single tenant affected with a workaround | Next working day |

There is **one operator** (the maintainer). The on-call expectation is therefore: any P1 is handled by
that person or explicitly handed over in writing before they go offline; a P1 with no available operator
is an accepted, recorded risk, not an alarm that nobody hears. The Product Owner is the escalation point
for scope, for approving a production restore, and for any decision this runbook does not cover
([constitution Art. VIII.1](../constitution.md), [owner sign-offs](owner-signoffs.md)).

### 1.1 Escalation paths

| Trigger | Escalate to | Through |
|---|---|---|
| P1 and the operator is unavailable or out of depth | Product Owner | The private operations channel |
| Any restore into a production database; any destructive action | Product Owner (explicit approval before acting) | The private operations channel, with the plan and the isolated restore result |
| Supabase incident, paused project or pooler failure beyond the first hour | Supabase support | Provider console / support ticket, quoting the project ref, the affected timestamp and `/health/ready` |
| Azure AI Foundry or Document Intelligence `5xx`/quota problems beyond the worker window | Azure support | Provider portal, quoting resource ids, deployment name and the `error_code` counts |
| Suspected credential exposure (log, screenshot, commit, device loss) | Product Owner, and rotate immediately | §7 — do not wait for the scheduled date |
| Security or data-protection concern (cross-tenant read, personal data in an unexpected place) | Product Owner | The private operations channel; do not paste the data itself |

Contacts, mailboxes and paging routes are deliberately **not** in this repository: they are recorded in
the private operations channel. The repository records roles and procedures, not people's phone numbers.
Until that channel exists and the owner has listed the contacts there, escalation is "reach the Product
Owner by whatever channel the team already uses", and that gap is visible rather than assumed.

## 2. Signals available today

| Signal | Where | Healthy | Degraded |
|---|---|---|---|
| `GET /health/live` | public edge (nginx proxies it) | 200 | process wedged |
| `GET /health/ready` | public edge | 200 | 503 when PostgreSQL is unreachable or the `app` schema is absent |
| `GET /health/ops` | **internal only** — see §2.1 | 200, `status: Healthy` | 503 `Unhealthy`; 200 `Degraded` for queue/heartbeat |
| `processing.queue.oldest_age_seconds`, `processing.queue.depth` | OpenTelemetry `Convenciones.Operations` | oldest age below threshold, depth small | age growing, depth growing |
| `processing.worker.heartbeat_age_seconds`, `processing.worker.heartbeat_present` | same | present and current | `0`/absent or age above threshold |
| `processing.operations.sample_failures` | same | 0 | increasing: the sampler cannot read the database |
| Deploy outcome | `.github/workflows/deploy.yml` run | green | red, or green with a failing `/health/ready` wait |

### 2.1 Reaching `/health/ops`

The report is **not** on the public surface. `deploy/nginx.conf` proxies `/health/live` and `/health/ready`
and answers `404` for `/health/ops`; every other path falls through to the SPA. The report itself is
published on the droplet's loopback only (`127.0.0.1:8081`, from `deploy/docker-compose.yml`), where the
scheduled probe in [`.github/workflows/ops-probe.yml`](../../.github/workflows/ops-probe.yml) reads it.

```bash
ssh <user>@<ip> 'curl -fsS http://127.0.0.1:8081/health/ops'
curl -sS -o /dev/null -w '%{http_code}\n' https://138-197-15-40.sslip.io/health/ops   # expect 404
```

If the loopback call is refused, the running stack predates the port mapping (redeploy) or the mapping was
edited: `ssh <user>@<ip> "ss -ltn | grep 8081"` must show `127.0.0.1:8081` and nothing else. The probe checks
all three properties — loopback bind, edge `404`, JSON verdict — and fails P1 when any of them drifts. The
design and the decision table are in [monitoring.md §2–§3](monitoring.md#2-reachability-loopback-only-404-at-the-edge).

### 2.2 Diagnostic queries

```sql
-- queue health: depth, oldest queued run, leases that have expired
select status, count(*) from platform.processing_run group by status order by status;
select min(created_at) as oldest_queued, now() - min(created_at) as age
from platform.processing_run where status = 'Queued';
select count(*) as expired_leases from platform.processing_run
where status = 'Running' and lease_expires_at < now();

-- worker liveness
select last_seen_at, now() - last_seen_at as age, enabled from platform.worker_heartbeat;

-- provider failures in the last hour
select error_code, count(*) from platform.processing_attempt
where started_at > now() - interval '1 hour' group by error_code order by 2 desc;

-- spend today, and the budgets that are near their limit
select company_id, sum(estimated_cost_usd) from platform.ai_usage_record
where recorded_at > date_trunc('day', now()) group by 1 order by 2 desc limit 10;
select company_id, limit_amount, spent_amount, reserved_amount, enforcement from platform.ai_budget
where spent_amount + reserved_amount > limit_amount * 0.8;
```

## 3. First response per failure scenario

Each scenario has the same shape: what you see, what you do in order, what proves it recovered, and what
not to do. The SQL above is the shared diagnostic kit.

### 3.1 Database down

| Step | Action |
|---|---|
| See | `/health/ready` 503; `/health/ops` overall `Unhealthy` with `database` failing; API 5xx on every write; `processing.operations.sample_failures` climbing; the worker logs connection errors |
| 1 | Confirm from the droplet that the API is up but the database is not: `curl -fsS https://138-197-15-40.sslip.io/health/ready` answers 503 while `docker compose ps` shows the containers healthy. (Caddy answers on 80/443 by host name, so a plain `http://127.0.0.1/...` probe is not reliable; use the sslip name or an in-network probe from §2.1) |
| 2 | Check the Supabase project: dashboard status, **project paused**, connection-pooler incidents, billing/plan state. This is the provider's console, not the repository |
| 3 | Check credentials and network: `docker compose exec -T api printenv` shows the `Database__*` parts present (names only — never print a value); the pooler host/port come from `SUPABASE_DB_HOST` |
| 4 | If the provider reports an incident: **contain** — stop ingestion (`Worker__Enabled=false`, worker stays disabled), publish a status note, wait. Do not restart-loop the API |
| 5 | If the data is lost or corrupted: open a P1, freeze writes, **ask the Product Owner before any restore into a production database** |
| 6 | Restore path: follow [P1-007 §4](p1-007-backup-restore.md#4-restore-runbook) into an **isolated** target first, run `check`, then cut over deliberately. The measured drill figures are in [restore-drill.md](restore-drill.md) |
| Verify | `/health/ready` 200; sign in; open one document and read its text; one write (create an agreement) succeeds |
| Escalate | Product Owner (approval to restore); Supabase support with the project ref and timestamps |

**Do not** point `Convenciones.Backup restore` at the production database to "try again": it refuses a
non-empty target by design, and a production restore is a destructive, approval-gated action.
**Do not** assume Supabase PITR is enabled — it has never been verified in the console
([P1-007 §1](p1-007-backup-restore.md)).

### 3.2 Worker stale or stopped

| Step | Action |
|---|---|
| See | `worker` check `Degraded` (heartbeat older than `Operations:WorkerHeartbeatMaxAgeSeconds`) with `heartbeat_present=1`; or `Unknown` ("never wrote a heartbeat") in an environment without a worker; queued runs do not move |
| 1 | `select last_seen_at, now() - last_seen_at, enabled from platform.worker_heartbeat;` — `enabled=false` plus an ageing timestamp is the normal, disabled state, not an incident |
| 2 | `docker compose ps worker` and `docker compose logs --tail=200 worker`. A crashed container is `restart: unless-stopped` and comes back; a container that is up but not beating is a defect |
| 3 | If the worker is intentionally off (default), the queue is expected to grow: the answer is capacity, not liveness. Follow §3.3 |
| 4 | If it must run: activate it **only** through the bounded path (`scripts/staging/worker-enable.sh`, deadline + watchdog + allowlist; see [worker-temporary-activation.md](worker-temporary-activation.md)). Never edit `.env` to `true` on the droplet |
| 5 | After a restart, confirm claims resume: `select count(*) from platform.processing_run where status='Running'` and the heartbeat age drops below the threshold |
| Verify | `worker` check `Healthy`; one run reaches `Completed`; `processing.queue.depth` falls |
| Escalate | Product Owner only if the worker must stay enabled beyond its authorized window |

Expired leases are not lost work: the recovery pass reclaims `Running` runs whose lease expired, so a
killed worker inside a legitimate window leaves claimable work, not orphans
([worker attempt fencing](worker-attempt-fencing.md)).

### 3.3 Queue stalled or backlogged

| Step | Action |
|---|---|
| See | `queue` `Degraded`, `oldest_age_seconds` and depth growing; runs stay `Queued` (no worker) or `Running` (stuck attempts); the UI shows documents that never finish |
| 1 | Split the two causes with one query: depth by status, plus `worker` heartbeat age. Healthy worker + growing backlog = throughput; stale worker = liveness (§3.2) |
| 2 | Look at the failure mix: `select error_code, count(*) from platform.processing_attempt where started_at > now() - interval '1 hour' group by 1`. A single dominant code is usually a provider or a quota (§3.4, §3.5) |
| 3 | Check expired leases; if a run is `Running` with an old lease, recovery will reclaim it at the next pass — wait one interval before intervening |
| 4 | Contain the growth: stop new uploads (announce) or keep the worker disabled so attempts are not burned against a failing provider |
| 5 | Capacity answer, not a fix: one worker drains ~29.8 documents/min against a 2 s provider latency ([P2-005](p2-005-capacity.md)). A 500-document import is ~17 minutes of queue. Quote that, not an unmeasured promise |
| Verify | depth falls, `oldest_age_seconds` below the threshold, a spot-checked document reaches `Completed` with text readable |
| Escalate | Product Owner for the SLO threshold ([owner sign-off #4](owner-signoffs.md)) and for enabling the worker |

### 3.4 AI provider outage (Azure AI Foundry / Document Intelligence)

| Step | Action |
|---|---|
| See | No direct provider probe exists ([P1-009 §5](p1-009-observability.md#5-failure-scenarios)); the symptom is runs failing with `provider_timeout`/`429`/`5xx` codes, attempts accumulating, `queue` degrading, and spend flat |
| 1 | Classify by error: `provider_timeout` (latency/quota), `429` (rate limit), `401/403` (key or deployment), `5xx` (provider fault). The `error_code` on `platform.processing_attempt` is the primary evidence |
| 2 | Check the Azure portal: resource health, the deployment name in `AzureAi__Deployment`, quota/TPM for the deployment, and the Document Intelligence resource status |
| 3 | For `401/403`: the key may have been rotated or disabled — see [§7](#7-credential-rotation-schedule-proposed) before changing anything, and never print the key |
| 4 | Contain cost and retries: keep the worker inside its window or disable it (§6). Redispatched attempts are bounded by attempt fencing, but each attempt costs money |
| 5 | Recovery: when the provider is healthy, let the queue drain (§3.3) or re-enable the worker through the bounded path |
| Verify | a new run completes; `estimated_cost_usd` rows appear for the company; no new `provider_*` codes |
| Escalate | Azure support with resource ids and timestamps; Product Owner if the outage outlives the authorized worker window |

**Do not** raise `Processing__MaxActiveRunsPerCompany` or disable the cost guard to "get through" an
outage: the budget is the control that stops a retry storm from becoming an invoice
([ADR-029](../architecture/decisions.md), [P1-005](../tasks/P1-005-cost-metering.md)).

### 3.5 Failed deployment

| Step | Action |
|---|---|
| See | `deploy.yml` red; or green with the `/health/ready` wait failing; the SPA loads but `/api/*` errors; the migrator exited non-zero |
| 1 | Read the failing step. A migrator failure aborted before `up -d`, so the old stack is still running — the site is usually **not** down |
| 2 | Automatic rollback already restored the previous `.env` and started the previous images. Confirm it did: `docker compose ps`, `curl -fsS https://138-197-15-40.sslip.io/health/ready`, `curl -I https://138-197-15-40.sslip.io/` |
| 3 | If the workflow did not roll back, do it by hand (§4) |
| 4 | Diagnose without touching the running stack: `docker compose logs --tail=200 api worker`, and compare the failing migration with `public.schema_migration` |
| 5 | Do not re-dispatch the same deploy until the cause is identified; a second failure extends the window |
| Verify | `/health/live` 200, `/health/ready` 200, SPA 200 with `text/html`, sign-in works, upload works; after a release, the [smoke gate](smoke-test.md) is the full check |
| Escalate | Product Owner (release decision); GitHub Actions logs kept as evidence |

## 4. Rollback

Automatic: the deploy workflow restores the previous `.env` and brings the previous images back up when a
step fails ([deployment.md §3](deployment.md#3-deployment-flow-githubworkflowsdeployyml)).

Manual rollback, on the droplet:

```bash
ssh <user>@<ip>
cd /opt/convenciones
cp .env.previous .env        # the previous commit's images
docker compose up -d --remove-orphans
curl -fsS https://138-197-15-40.sslip.io/health/ready
```

(The last line is the documented rollback verification from [deployment.md §6](deployment.md#6-rollback),
with the address changed to the HTTPS name: Caddy answers by host name, so the `http://127.0.0.1/...` form
in that section only reflects the HTTP era it was written in.)

Then verify as in §3.5. The previous images stay on the droplet until `docker image prune`.

**Migrations are forward-only.** A rollback restores the binary, not the schema: a release that changed
the schema needs its own reverse migration, and none exists in this MVP
([deployment.md §6](deployment.md#6-rollback)). Consequence for the operator: a rollback after a
schema-changing release is **not** a supported recovery path — restoring the previous database backup
into an isolated environment and cutting over is (§3.1), and it is an owner decision.

## 5. Backup and restore

- Tool and procedure: [P1-007 §3](p1-007-backup-restore.md#3-commands). The tool never writes to
  anything but an empty database whose name you confirmed.
- Measured drill: [restore-drill.md](restore-drill.md). On 2026-11-22 a production-shaped synthetic set
  (10,000 documents, 5,000 agreements, 50,000 clauses, 229.9 MiB including 10,000 storage objects) ran
  `create` in 14.1 s, `verify` in 2.7 s, `restore` in 16.8 s and `check` in 2.0 s — **35.7 s of
  lifecycle time on a local Docker Desktop, which is an environment-specific baseline, not a production
  RTO guarantee**.
- Scheduling, retention, off-site copies and whether managed PITR exists are **unsigned owner decisions**
  ([owner sign-off #3](owner-signoffs.md#1-release-and-acceptance-sign-offs)). Until they are decided,
  there is no backup schedule: backups happen when an operator runs one.
- Storage is a separate recovery domain. A database backup alone cannot recover a lost document
  ([P1-007 §5](p1-007-backup-restore.md#5-consistency-between-database-and-storage-snapshots)).

## 6. Worker kill-switch

The worker is the only component that spends money and calls external providers, so stopping it is the
default containment action. It fails closed: `Worker__Enabled=false` in `.env`, `restart: "no"` in the
activation override, a mandatory `Worker__DeadlineUtc`, and a detached watchdog that calls the same
disable script at the deadline ([worker-temporary-activation.md](worker-temporary-activation.md)).

| Situation | Action |
|---|---|
| Routine stop of an authorized window | `ssh <user>@<ip> 'cd /opt/convenciones && .staging-ai/disable-worker.sh'` (or from a workstation: `SSH_TARGET=... scripts/staging/worker-disable.sh`, which retries 3×) |
| Stop from a workstation, full path | `scripts/staging/worker-disable.sh` — copies and runs `worker-off.sh` on the droplet, verifies the container env is `false`, the restart policy is `unless-stopped`, waits for health, writes `.staging-ai/disabled-at`, then stops the watchdog |
| Emergency: stop claiming now, do not wait for a clean disable | `cd /opt/convenciones && docker compose -f docker-compose.yml stop worker` — then still run the disable script, because `stop` leaves the override and the next `up` could re-enable it |
| Watchdog is stuck or must not fire | `sh .staging-ai/watchdog-stop.sh .staging-ai/watchdog.pid` (it refuses to kill anything that is not the recorded watchdog) |
| Verify off | `grep '^Worker__Enabled=' .env` → `false`; `docker compose ps worker` → healthy; `docker inspect` shows `Worker__Enabled=false` and `unless-stopped`; `/health/ops` `worker.enabled=false` |

Never enable the worker by editing `.env` on the droplet: the only intended path is
`worker-enable.sh`, which requires the authorized company id and one document or run id.

## 7. Credential rotation schedule (proposed)

**Status: proposed, pending owner sign-off.** Nothing below has been executed; no rotation has been
rehearsed, and the repository records no rotation dates. The intervals are a baseline for the owner to
accept, shorten or replace; provider policy wins where it is stricter.

| # | Credential | Where it lives | Used by | Proposed interval | Rotation outline | Verification after rotation | Blast radius if leaked |
|---|---|---|---|---|---|---|---|
| 1 | `SSH_PRIVATE_KEY` (deploy key) and `SSH_KNOWN_HOSTS` | GitHub Environment `staging` secrets | `deploy.yml` | 90 days, and on any personnel change | Generate a new key pair offline; append the public key to the droplet's `authorized_keys`; update both secrets; dispatch a deploy; then remove the old public key | Deploy run green; `deploy` user can still SSH; old key refused | Shell on the droplet as the deploy user; full `.env` (database and service keys) |
| 2 | Supabase database password (`SUPABASE_DB_PASSWORD`) | GitHub secret | API and migrator | 90 days, and on suspicion | Reset the database password in the Supabase dashboard; update the secret; dispatch a deploy so `.env` is rewritten | `/health/ready` 200; one authenticated read; migrator can still connect | Full database access: every tenant's data |
| 3 | Supabase secret key (`SUPABASE_SECRET_KEY`, `service_role`-class) | GitHub secret | API Storage access | 90 days, and on suspicion | Create a new secret key in Supabase, update the secret, deploy, verify an upload and a download, then revoke the old key | Upload + download + delete of a test document; no 403 in API logs | Object-storage access for every tenant, bypassing storage RLS |
| 4 | Azure AI Foundry key (`AzureAi__ApiKey`) | GitHub secret | Worker classification | 90 days or provider policy; immediately on a `401/403` surge | Regenerate the secondary key in the Azure portal, update the secret, deploy, exercise one classification, then regenerate the primary | One run reaches `Completed` with usage rows; no `401/403` codes | Inference spend on the resource; no tenant data (prompts only) |
| 5 | Azure Document Intelligence key (`DocumentIntelligence__ApiKey`) | GitHub secret | Worker extraction | 90 days or provider policy | Same two-key procedure as #4 | One document extraction completes; no `401/403` codes | Extraction spend on the resource |
| 6 | Supabase publishable/anon key (`SUPABASE_PUBLISHABLE_KEY`) | GitHub **variable** (not a secret) | Browser client | Only with a JWT-secret rotation, or on suspicion | Tied to the project's JWT signing secret: rotating it signs every user out | All clients reload and sign in again; no stale-token 401 storm | No data by itself; RLS is the guard |
| 7 | GitHub Actions `GITHUB_TOKEN` and GHCR login | issued per run | CI and deploy | not applicable (minutes) | — | — | — |
| 8 | TLS certificate (Caddy / Let's Encrypt) | `caddy_data` volume | edge | automatic | Renewed by Caddy | `openssl s_client` expiry date; HTTPS 200 | — |
| 9 | Legacy MySQL credentials (T16, sets A–D) | decommissioned | — | not applicable | The servers no longer exist; rotation does not apply ([owner sign-offs §3](owner-signoffs.md#3-already-decided-for-context)) | — | Archived analysis only |

Rules that apply to every row:

1. Rotate in this order — **create the new credential, deploy it, verify, then revoke the old one** — so a
   failed rotation never leaves the system without a working credential. The SSH key is the exception in
   form only (add new before removing old).
2. Record the date, the actor and the verification evidence in the private operations log, **not** in
   this repository: no secret, key fragment or password belongs in Git, an issue or a PR
   ([constitution Art. VI.3](../constitution.md)).
3. A rotation is complete when the verification column passed. "Secret updated" is not completion.
4. Any credential that appears in a log, a screenshot, a chat or a commit is rotated immediately, not at
   its next scheduled date.

## 8. Logging and data handling

- No log line may contain a token, a password, a `service_role`-class key or a connection string with a
  password. The deployment assembles the connection string inside the process
  (`NpgsqlConnectionStringBuilder`), so an env-file quoting mistake cannot put the password into a log
  ([deployment.md §2](deployment.md#2-configuration)).
- Never paste `.env`, `docker compose config` output, a full connection string or a JWT into an issue, a
  PR, a chat or this runbook. Quote the variable **name** and the symptom.
- Backup sets contain personal and financial data: keep them encrypted at rest, access-controlled and out
  of the repository ([P1-007 §6](p1-007-backup-restore.md#6-security)).
- Restored environments are production data. Restrict access, do not copy fixtures out of them, and
  destroy them when the drill or the incident is closed.
- Health endpoints disclose whether the schema is present (audit M4). They are reachable at the edge
  today; do not extend them with tenant data ([P1-009 §8](p1-009-observability.md#8-security-and-roles)).

### 8.1 What may be uploaded to staging

**Staging is not production and must not hold real client data.** It exists to demonstrate and test the
product with material that can be deleted at any time.

| Allowed | Prohibited |
|---|---|
| Contract **synthetic** documents and the repository fixtures | Signed collective agreements, annexes or pay tables from a real company |
| **Public** legislation: gacetas, laws, regulations, resolutions | Anything confidential, personal or commercially sensitive |
| Anonymised demonstration material reviewed by the Product Owner | Client contracts pending redaction or anonymisation |

Why: the AI provider terms are not signed and the synthetic-only policy stands until they are
([P1-006 sign-off](owner-signoffs.md), issue #47), the environment has no production controls, and any
document there may be deleted without notice. The T19 release gate creates and removes its own synthetic
tenant on every run ([smoke-test.md](smoke-test.md)); the document inventory, and purging anything that
does not belong, is tracked in issue #140.

When in doubt, the answer is no: ask the Product Owner before uploading, and never upload a document to
"see if it works".

## 9. Open items this runbook depends on

| Item | Why it blocks | Owner |
|---|---|---|
| SLO thresholds and alert destination | every alert in §3 needs a number and a recipient | [sign-off #4](owner-signoffs.md) |
| Queue/worker reachable by a probe | **done (slice C):** loopback port, edge `404` and the scheduled probe; see [monitoring.md](monitoring.md) | — |
| A metrics scrape, dashboard or hosted monitor | the probe covers the HTTP signals; metrics are exported over OTLP but nothing scrapes them | Product Owner (cost decision) |
| Backup frequency, retention, off-site, PITR verification | §5 has no schedule and no RTO commitment | [sign-off #3](owner-signoffs.md) |
| Rotation intervals and provider plan (support tiers) | §7 is a proposal | [sign-off #3 / policy](owner-signoffs.md) |
| Release smoke test | §3.5 verification is a manual list | **implemented (slice B):** [smoke-test.md](smoke-test.md); the first staging dispatch is still pending |

## 10. Identity provider settings for self-service on-boarding (ADR-030)

Apply this section **when ADR-030 is implemented** ([decisions](../architecture/decisions.md)), before the
feature is released to end users. Until then users are created in the Supabase dashboard and provisioned
through `/admin/users`, as §4.1 of
[deployment](deployment.md#41-onboarding-a-user-with-the-supabase-invitation-flow) describes.

| Setting | Where | Value |
|---|---|---|
| `Site URL` | Authentication → URL Configuration | `https://138-197-15-40.sslip.io` |
| `Redirect URLs` | Authentication → URL Configuration | must contain `https://138-197-15-40.sslip.io/auth/set-password` |
| Minimum password length | Authentication → Providers → Email | **12**, with no composition rules (NIST SP 800-63B) |
| Invitation template | Authentication → Email Templates → Invite | Spanish subject and body; keep `{{ .ConfirmationURL }}` intact |

**Order matters.** The redirect allow-list comes first: an invitation whose redirect is not allow-listed is
rejected when the person opens it, and the failure looks like a broken link rather than a configuration
mistake.

**Verify before announcing it**, with a mailbox you control:

1. Invite that address from `/admin/users`; the email arrives in Spanish.
2. Open the link: it lands on `/auth/set-password`, not on `/login`.
3. Set a 12-character password; the screen confirms and signs that temporary session out.
4. Sign in at `/login` with the new password and check that the account opens its company's documents.
5. Ask a second person to open the same link: it must be rejected as already used.

**Known limit.** The built-in Supabase SMTP sends only a few messages per hour. That is fine for onboarding
one or two people and is not suitable for a bulk import; a bulk path needs a provider decision first.
