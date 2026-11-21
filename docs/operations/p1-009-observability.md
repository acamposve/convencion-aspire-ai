# P1-009 — Operational observability and availability

**Status:** implemented. The API exposes a machine-readable operational report and OpenTelemetry metrics
for queue age/depth and worker liveness. Thresholds are **required configuration**; when unset the report
says `Unknown` instead of inventing a number.

## 1. Signals

| Signal | Source | Meaning |
|---|---|---|
| `database` | existing readiness check | can the API reach PostgreSQL |
| `queue` | `platform.processing_run` | oldest `Queued` run age, `Queued`/`Running` depth |
| `worker` | `platform.worker_heartbeat` | age of the last worker heartbeat; `enabled` flag |

The worker writes the heartbeat singleton row on startup (including when `Worker:Enabled=false`) and on
every poll. A stopped worker therefore leaves an ageing timestamp rather than disappearing silently.

## 2. Endpoint contract

`GET /health/ops` (anonymous, for probes):

```json
{
  "status": "Healthy | Degraded | Unknown | Unhealthy",
  "checkedAt": "2026-11-21T15:00:00.0000000+00:00",
  "checks": [
    { "name": "database", "status": "Healthy", "description": "The database answered.", "data": {} },
    { "name": "queue", "status": "Degraded",
      "description": "The oldest queued run is 7200s old; the configured maximum is 900s.",
      "data": { "queued": 3, "running": 1, "oldestAgeSeconds": 7200, "thresholdSeconds": 900, "queueEmpty": false } },
    { "name": "worker", "status": "Healthy",
      "description": "The worker heartbeat is current.",
      "data": { "ageSeconds": 12, "thresholdSeconds": 120, "enabled": true } }
  ]
}
```

- `checkedAt` is UTC.
- HTTP status is `503` only when the overall status is `Unhealthy`; `Degraded` and `Unknown` return `200`
  so a monitoring probe does not treat a backlog or missing configuration as a dead service.
- `queue` on an empty queue: `Healthy` with `queueEmpty: true` and `oldestAgeSeconds: 0`.
- `worker` with no heartbeat row: `Unknown`, never `Healthy`.
- Neither `queue` nor `worker` affects `/health/ready` or `/health/live`: a processing backlog must not
  remove serving capacity.

## 3. Configuration

| Setting | Required | Meaning |
|---|---|---|
| `Operations:QueueMaxAgeSeconds` | yes, for a verdict | oldest queued run above it → `Degraded`; unset → `Unknown` |
| `Operations:WorkerHeartbeatMaxAgeSeconds` | yes, for a verdict | heartbeat older than it → `Degraded`; unset → `Unknown` |
| `Operations:MetricsIntervalSeconds` | no (default 30) | sampling cadence for the metrics below |

No default threshold exists in code. The operation team sets them from the agreed SLO; until then the
report is `Unknown`, which is visible and honest.

## 4. Metrics (OpenTelemetry `Convenciones.Operations`)

| Metric | Type | Meaning |
|---|---|---|
| `processing.queue.depth` | gauge | `Queued` runs |
| `processing.queue.oldest_age_seconds` | gauge | age of the oldest `Queued` run; `-1` when empty |
| `processing.worker.heartbeat_age_seconds` | gauge | seconds since the last heartbeat; `-1` when absent |
| `processing.worker.heartbeat_present` | gauge | `1`/`0` |
| `processing.operations.sample_failures` | counter | sampling errors (database unavailable) |

The sampler runs on a timer, so a slow database cannot block metric export; a failed sample keeps the last
known values and increments the failure counter.

## 5. Failure scenarios

| Scenario | Signal | Response |
|---|---|---|
| Database down | `/health/ready` 503; `/health/ops` overall `Unhealthy`, `database`/`queue`/`worker` unhealthy | alert; restore database; readiness removes the instance from traffic |
| Worker stopped | `worker` `Degraded` once the heartbeat age exceeds the threshold, `heartbeat_present=1` | restart worker; queued runs stay claimable by another instance |
| Worker never started in that environment | `worker` `Unknown` ("never written a heartbeat") | expected for an environment with no worker; confirm intent |
| Queue stalled | `queue` `Degraded`, `oldest_age_seconds` growing, depth growing | inspect worker logs and `platform.processing_run`; recovery reclaims expired leases |
| Backlog with a healthy worker | `queue` `Degraded`, `worker` `Healthy` | throughput problem, not liveness |
| Threshold unset | `Unknown` with the configuration message | set the threshold from the SLO |
| Provider outage | no direct signal (no provider call is instrumented here) | runs fail/retry; queue depth grows and the `queue` signal fires |

## 6. Alert templates (thresholds from the SLO)

The repository does not ship a monitoring stack, so these are expressions to wire into whatever scrapes the
service. `<QUEUE_MAX_AGE>` and `<HEARTBEAT_MAX_AGE>` come from the SLO; do not copy numbers from this page.

```promql
# queue backlog
max_over_time(processing_queue_oldest_age_seconds[10m]) > <QUEUE_MAX_AGE>

# worker heartbeat missing or stale
processing_worker_heartbeat_present == 0
or processing_worker_heartbeat_age_seconds > <HEARTBEAT_MAX_AGE>

# metrics sampling is failing (database unreachable)
increase(processing_operations_sample_failures[10m]) > 0
```

Probe-based alternative where metrics are not scraped:

```bash
curl -fsS http://<host>/health/ops | jq -e '.status != "Unhealthy" and (.checks[] | select(.name=="queue").status != "Degraded")'
```

## 7. SLO / RTO measurement

- **Queue latency**: `processing_queue_oldest_age_seconds` over time gives the backlog age distribution;
  the SLO value is a percentile the operation team chooses, and the alert threshold is derived from it.
- **Worker availability**: fraction of time `processing_worker_heartbeat_age_seconds` stays below the
  agreed heartbeat threshold.
- **Recovery time**: measure from "worker restart" to "queue age back below the threshold" after a
  controlled stop; repeat with a backlog of a known size. Record the measured value; do not publish an
  unmeasured RTO.
- **Alert quality**: every alert must name the signal, the scenario and the first diagnostic query
  (`select … from platform.processing_run where status='Queued'`).

## 8. Security and roles

- `platform.worker_heartbeat` contains a timestamp and an `enabled` flag only: no tenant, document or user
  data. It is readable by `app_api` **without** user claims, because `/health/ops` is a probe endpoint that
  runs before any session exists; RLS is enabled and only that read policy exists.
- The worker writes through its privileged connection, like the other worker-owned tables.
- `/health/ops` is anonymous by design and must not be extended with tenant data.

## 9. Limitations

- No scraper, dashboard or paging integration is included; the repository provides the signals, metrics
  and templates, not the monitoring stack.
- Provider health is not probed (it would consume credits); provider failure is observed indirectly via
  queue growth and run errors.
- The heartbeat proves the process is polling, not that it can execute a stage; a worker that fails every
  run still beats.
- Metrics are process-local; with several API instances each exports its own sample (same database, so
  values agree apart from timing).

## 10. Tests

`tests/Convenciones.IntegrationTests/OperationsHealthTests` (7, PostgreSQL 17):

1. empty queue + fresh heartbeat → `Healthy`, UTC `checkedAt`;
2. queue older than the configured threshold → `Degraded` with the observed age and threshold;
3. thresholds unset → `Unknown` for both signals;
4. stale heartbeat → `Degraded`; missing heartbeat row → `Unknown` ("never");
5. queue backlog does not change `/health/ready` (200) while `/health/ops` degrades;
6. unreachable database → `/health/ready` 503, `/health/ops` 503 with `Unhealthy`;
7. the heartbeat writer upserts a single row and moves it forward.
