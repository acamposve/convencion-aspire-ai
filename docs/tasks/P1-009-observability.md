# P1-009 — Observability and availability

**Status:** implemented and tested; SLO thresholds are configuration set by operations.

## What was delivered

| Component | Location |
|---|---|
| Worker heartbeat table | `supabase/migrations/20261121000700_worker_heartbeat.sql` |
| Worker heartbeat writer | `src/Convenciones.Worker/WorkerHeartbeat.cs`, wired in `ProcessingWorker` |
| Queue and heartbeat probes | `src/Convenciones.Api/Infrastructure/OperationsHealthChecks.cs` |
| Metrics sampler | `src/Convenciones.Api/Infrastructure/OperationsMetrics.cs` |
| `GET /health/ops` contract | `src/Convenciones.Api/Program.cs` |
| Runbook | `docs/operations/p1-009-observability.md` |
| Tests | `tests/Convenciones.IntegrationTests/OperationsHealthTests.cs` (7) |

## Design decisions

1. **No invented thresholds.** `Operations:QueueMaxAgeSeconds` and
   `Operations:WorkerHeartbeatMaxAgeSeconds` are required configuration; unset means `Unknown`.
2. **Heartbeat in PostgreSQL**, not a container file: it survives restarts, is visible to the API and is
   written even when the worker is disabled.
3. **Queue/backlog never affects readiness.** Only `/health/ops` reports it; serving capacity is not
   removed because processing is behind.
4. **Anonymous ops endpoint, minimal data.** The heartbeat row carries a timestamp and an enabled flag; no
   tenant data is exposed.

## Test evidence

7 integration tests cover queue healthy/degraded/unknown, heartbeat healthy/stale/missing, the JSON
contract and UTC timestamp, readiness independence, database-unreachable behaviour and the heartbeat
writer upsert.

## Open decisions

- The SLO values themselves (queue age threshold, heartbeat threshold, latency percentile) are not set
  here; the runbook documents how to derive them and how to measure RTO.
