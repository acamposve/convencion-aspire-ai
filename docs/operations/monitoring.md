# Monitoring — what is measured, what alerts, and who is told

**Status:** T19, slice C. The operational report is now reachable by an automated probe, the probe runs on
a schedule and records its verdict as an issue, and the public edge answers `404` for it. There is still
**no** metrics scraper, dashboard or paging integration: what exists is one HTTP probe with a written
decision table.

| Companion document | What it adds |
|---|---|
| [P1-009 observability](p1-009-observability.md) | the endpoint contract, the metric names, the threshold configuration |
| [Runbook](runbook.md) | what the operator does when this page says something is wrong |
| [Deployment](deployment.md) | how the loopback port is published and verified |
| [T19](../tasks/task-19-production-readiness.md) | the acceptance criteria this slice closes and leaves open |

## 1. The signals

`GET /health/ops` (anonymous, JSON) reports three checks plus an overall verdict:

| Signal | Source | Healthy means | Failing means |
|---|---|---|---|
| `database` | the existing readiness check | PostgreSQL answered | the database is unreachable |
| `queue` | `platform.processing_run` | oldest `Queued` run is inside `Operations:QueueMaxAgeSeconds` (or the queue is empty) | `Degraded`: the oldest run is older than the threshold; `Unhealthy`: the queue cannot be read |
| `worker` | `platform.worker_heartbeat` | the last heartbeat is inside `Operations:WorkerHeartbeatMaxAgeSeconds` | `Degraded`: stale heartbeat; `Unhealthy`: cannot be read; `Unknown`: never wrote one |

Overall status is `Unhealthy` > `Degraded` > `Unknown` > `Healthy`, and the HTTP status is `503` only for
`Unhealthy`. `Degraded` and `Unknown` return `200`, deliberately: a backlog or a missing threshold must not
look like a dead service.

`/health/live` and `/health/ready` are unchanged and stay public: `/health/live` answers whether the
process serves at all, `/health/ready` answers whether it can serve (database reachable, schema present).

## 2. Reachability: loopback only, 404 at the edge

`/health/ops` reports internal state — queue depth and age, heartbeat age, the database verdict, and, when a
query fails, the database error text. It is not part of the public surface.

| Layer | Behaviour | Why |
|---|---|---|
| API container | listens on `8080` inside the compose network | unchanged |
| `deploy/docker-compose.yml` | publishes `127.0.0.1:8081:8080` on the droplet | loopback is not routable from the internet; the probe needs a local HTTP endpoint |
| `deploy/nginx.conf` | `location = /health/ops { return 404; }` | without the rule the request fell through to the SPA and answered `200` with HTML |
| Caddy (edge) | proxies to nginx, so it inherits the `404` | one place to decide what is public |

Rules that keep it that way:

1. **Never** publish `8081` on `0.0.0.0`, and never remove the `127.0.0.1:` prefix. The probe fails (P1) if
   the listening socket on `8081` is anything other than `127.0.0.1:8081`, so a bad edit is caught by the
   next probe run instead of being discovered by someone scanning the host.
2. The edge must answer `404` for `/health/ops`. The probe fails (P1) if it answers anything else, including
   `200`, because that means the diagnostics became public.
3. The report stays anonymous **inside** the network: there is no credential to distribute for a probe that
   only runs from the droplet's loopback.

Manual check, from a workstation:

```bash
ssh <user>@<ip> 'curl -fsS http://127.0.0.1:8081/health/ops'
curl -sS -o /dev/null -w '%{http_code}\n' https://138-197-15-40.sslip.io/health/ops   # expect 404
```

## 3. The probe

`tools/ops-probe.ps1` is the whole implementation. It reads the two public endpoints, the loopback report
and the listening sockets, applies a pure decision table, prints a Markdown verdict and exits `1` for P1/P2.

`.github/workflows/ops-probe.yml` runs it every 15 minutes on the default branch (and on demand):

| Trigger | `schedule: '*/15 * * * *'` and `workflow_dispatch` |
|---|---|
| Credentials | the existing `SSH_PRIVATE_KEY` / `SSH_KNOWN_HOSTS` and `vars.SSH_USER` / `vars.SSH_HOST` from the `staging` environment. **No new secret** |
| Self-check | the workflow runs `-SelfTest` first: a broken probe must not be reported as a broken service |
| Output | `$GITHUB_STEP_SUMMARY` always; a labelled issue on failure |
| Failure record | `ops-alert` issue, created once, commented while it keeps failing, **closed automatically** when the probe passes |
| Permissions | `contents: read`, `issues: write` |

### 3.1 Decision table

| Observation | Severity | First response |
|---|---|---|
| `/health/live` is not `200` | P1 | [runbook §3.5](runbook.md#35-failed-deployment) — the process is not serving |
| `/health/ready` is not `200` | P1 | [runbook §3.1](runbook.md#31-database-down) |
| `/health/ops` answers anything but `404` at the edge | P1 | configuration drift: restore the `404` (this page, §2) |
| `8081` is bound to anything but `127.0.0.1` | P1 | configuration drift: restore the loopback bind, then check the host for exposure |
| overall `Unhealthy`, or `database` `Unhealthy` | P1 | [runbook §3.1](runbook.md#31-database-down) |
| `queue` `Unhealthy` or `worker` `Unhealthy` (query failed) | P1 | [runbook §3.1](runbook.md#31-database-down) |
| `queue` `Degraded` | P2 | [runbook §3.3](runbook.md#33-queue-stalled-or-backlogged) |
| `worker` `Degraded` (stale heartbeat) | P2 | [runbook §3.2](runbook.md#32-worker-stale-or-stopped) |
| `queue`/`worker` `Unknown` (thresholds unset) | P3 | configuration gap, not an outage: set the thresholds (§5) |
| a check is missing from the report | P1 | deployment defect: the API did not register a probe |

P3 exits `0` and writes a warning to the summary: an unset threshold is a decision that is missing, not a
failure that needs waking someone up.

### 3.2 What the probe cannot see

- **Provider health.** No probe calls Azure; a provider outage shows up as `queue` growth and run errors.
- **`processing.operations.sample_failures`.** It is a metric, not part of the HTTP report; see §6.
- **A worker that beats but fails every run.** The heartbeat proves the process polls, not that it can
  execute a stage ([P1-009 §9](p1-009-observability.md#9-limitations)).
- **Anything about request latency or error rate.** No request instrumentation is exported anywhere today.

## 4. Limits of this alerting

1. **It is not paging.** GitHub can delay a scheduled run, and scheduled workflows are disabled after 60
   days without repository activity. The issue and the failed run are the alert; a watcher on the repository
   is the delivery mechanism.
2. **It costs Actions minutes** on a private repository: 96 runs a day if nothing else is dispatched.
3. **Fifteen minutes is the floor for this mechanism**, and GitHub does not guarantee that floor.
4. **One host.** The probe reads the same droplet the service runs on; a host that is entirely gone shows up
   as `000` on every check, which is exactly what §3.1 documents.
5. If a scheduled workflow run is skipped or fails to start, no alert is produced. That is a real limitation
   of alerting with a CI scheduler, stated rather than glossed over.

## 5. Thresholds — required configuration, not defaults

| Setting | Meaning | State |
|---|---|---|
| `Operations:QueueMaxAgeSeconds` | oldest `Queued` run above it → `Degraded` | **not set**; the report says `Unknown` |
| `Operations:WorkerHeartbeatMaxAgeSeconds` | heartbeat older than it → `Degraded` | **not set**; the report says `Unknown` |
| `Operations:MetricsIntervalSeconds` | metric sampling cadence (default 30) | optional |

No threshold is invented here and none has a code default. The values are an owner decision
([sign-off #4](owner-signoffs.md#1-release-and-acceptance-sign-offs)); the numbers in
[P1-009 §2](p1-009-observability.md#2-endpoint-contract) (`900` and `120` seconds) are illustrative
payloads, not adopted values. Until they are set, the probe reports P3 and tells you why.

## 6. Metrics — exported, not scraped

The API and the worker emit OpenTelemetry metrics (`Convenciones.Operations`):

`processing.queue.depth`, `processing.queue.oldest_age_seconds`,
`processing.worker.heartbeat_age_seconds`, `processing.worker.heartbeat_present`,
`processing.operations.sample_failures`.

They are exported through OTLP (`UseOtlpExporter()` in `Convenciones.ServiceDefaults`). **There is no
`/metrics` endpoint and no collector**, so nothing scrapes them today. The PromQL expressions in
[P1-009 §6](p1-009-observability.md#6-alert-templates-thresholds-from-the-slo) are the contract for the day
a scraper exists; they are not wired, and this page does not pretend otherwise.

The three templates, copied from P1-009 so this page is self-contained, with the same placeholders
(`<QUEUE_MAX_AGE>` and `<HEARTBEAT_MAX_AGE>` come from the SLO and are **not** numbers to copy):

```promql
# queue backlog: same threshold as Operations:QueueMaxAgeSeconds
max_over_time(processing_queue_oldest_age_seconds[10m]) > <QUEUE_MAX_AGE>

# worker heartbeat missing or stale: same threshold as Operations:WorkerHeartbeatMaxAgeSeconds
processing_worker_heartbeat_present == 0
or processing_worker_heartbeat_age_seconds > <HEARTBEAT_MAX_AGE>

# the metrics sampler itself cannot read the database (a signal the HTTP probe cannot see)
increase(processing_operations_sample_failures[10m]) > 0
```

If a collector is added later, these become the alert rules; the HTTP probe stays as the
end-to-end check, because it also covers routing, TLS and the public surface that a metric scrape inside
the compose network would not notice.

Adding a collector, a dashboard or a hosted monitoring service is a separate decision with an operational
cost (another service to run), not a documentation gap.

## 7. Verification

| Evidence | Result |
|---|---|
| `pwsh tools/ops-probe.ps1 -SelfTest` | 8 of 8 decision-table cases pass: healthy, database down, API not serving, edge exposing `/health/ops`, `8081` bound publicly, queue backlog, stale worker, thresholds unset |
| Probe against unreachable targets (`-PublicHost 127.0.0.1:9 -SshTarget probe@127.0.0.1`) | verdict `P1`, exit `1`, no crash: the failure paths fail closed |
| `docker compose config` (temporary copy with the example `.env`) | valid; the api publishes `host_ip: 127.0.0.1`, `published: 8081` |
| `nginx -t` with the changed `deploy/nginx.conf` | syntax ok (with `api` resolvable) |
| `OperationsHealthTests` (integration, PostgreSQL 17) | the endpoint contract, including `503` on an unreachable database and `Unknown` when thresholds are unset |
| The scheduled workflow on staging | **not run**: it needs a deployment, which this slice did not perform |

## 8. Secrets and data handling

- The probe prints no credential: it uses the SSH key from the `staging` environment (written to a `600`
  file and never echoed) and the workflow's own `GITHUB_TOKEN` for the issue.
- The verdict and the issue contain check statuses, ages and thresholds only. When a database query fails,
  the description contains the database error text, which is why the report stays on loopback.
- No tenant data, document name or user identifier appears in any signal; the heartbeat row is a timestamp
  and a boolean.

## 9. Open items

| Item | Owner |
|---|---|
| Set the two thresholds and enable a real verdict instead of `Unknown` | [sign-off #4](owner-signoffs.md#1-release-and-acceptance-sign-offs) |
| Run the probe once against staging after the next deployment, including one induced failure | deployment window approval |
| Decide whether a scraper/collector is worth its operational cost, or whether the probe is enough for v1 | Product Owner |
| Release smoke test (T19 AC3) and the log-secret check (T19 AC5) | remaining T19 slices |
