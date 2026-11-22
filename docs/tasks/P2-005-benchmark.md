# P2-005 — Load, capacity and performance benchmark

**Status:** implemented and measured; the capacity statement is proposed and unsigned.

*Tracking: P2-005 of the production-v1 audit (F-15). The audit found no measured evidence for the
10,000-documents-per-month scale or the 99.9% availability claim.*

## Objective

Measure, on a disposable local stack with a stubbed provider, how much load the API and the worker
sustain against explicitly stated **initial benchmark targets** (not an SLA), and publish the numbers,
the limits and an unsigned capacity statement that the owner can ratify or reject.

## Scope

In scope:

- Real Kestrel API process, real PostgreSQL 17 container with the real migrations and seed, real HTTP
  over loopback; worker hosted in-process with a stub `IProcessingStage` (the provider boundary).
- Scenarios: sustained mixed API load, 60 s API burst, 1 MiB upload stream, 70-document burst with
  queue-wait measurement, 500-document single-worker drain.
- Two independent runs, raw JSON artifacts, a report with throughput, p50/p95/p99, error breakdown,
  queue/wait/drain metrics, generator CPU, machine specs and limitations.

Out of scope and reported as not measured: 99.9% availability, TLS/nginx/network path, multi-instance
scaling, Supabase Storage, real Azure Document Intelligence/OpenAI latency and cost, real documents or
personal data, backup/restore under load, worker-deadline activation.

## Baseline parameters (confirmed 2026-11-22)

| Parameter | Value |
|---|---|
| Volume | 10,000 documents/month (~333/day, ~14/h, ~42/h business hours) |
| Short burst | 5× the business-hour rate for 10 min (~70 documents) |
| Queued batch | 500 documents over ~10 min, then drained |
| Sustained API | 20 req/s for 10 min, 85% read / 15% write |
| Burst API | 100 req/s for 60 s |
| Upload stream | 2 uploads/s of a 1 MiB PDF for 5 min |

Targets (initial benchmark targets, **not** an SLA):

| Dimension | Target |
|---|---|
| Reads p95 / p99 | ≤ 200 ms / ≤ 500 ms |
| Writes p95 / p99 | ≤ 400 ms / ≤ 1,000 ms |
| Upload 1 MiB p95 / p99 | ≤ 1,500 ms / ≤ 3,000 ms |
| 5xx rate under valid load | < 0.1% |
| Queue wait p95 (70-doc burst) | ≤ 60 s |
| Run duration p95 (10-page doc, 2 s stub) | ≤ 30 s |
| 500-doc drain, one worker | ≤ 30 min |

## Planned deliverables

| Deliverable | Path |
|---|---|
| Harness | `tools/Convenciones.LoadTest/` |
| Task record | `docs/tasks/P2-005-benchmark.md` (this file) |
| Capacity report | `docs/operations/p2-005-capacity.md` |
| Raw runs | `docs/operations/p2-005-results/<date>-run1.json`, `...-run2.json` |
| Statistics unit tests | `tests/Convenciones.UnitTests/LoadStatisticsTests.cs` |
| Self-test in CI | step in `.github/workflows/ci.yml` |

## Planned implementation

1. Harness skeleton: disposable PostgreSQL container, migrations + seed, fixture seeding, `ANALYZE`,
   connection-pool sizing, JWT minting for the local symmetric key, API child process lifecycle.
2. Statistics: p50/p95/p99 with explicit interpolation, unit-tested; `--self-test` validates the
   generator against a local stub HTTP server (injected 300 ms delay and 1% 5xx must be detected).
3. API scenarios: sustained mixed, burst, upload stream; per-request latency, status classes, throughput,
   generator CPU.
4. Worker scenarios: 70-document burst arriving over 10 min (queue wait), 500-document drain with one
   worker (drain time, run duration), both against a stub stage with 2 s latency.
5. Two runs, report, proposed capacity statement.

## Risks

- The generator shares the machine with the system under test; generator CPU is measured and reported,
  and a no-op endpoint baseline bounds the measurement ceiling.
- Docker Desktop resource limits make absolute numbers machine-specific; the report records CPU, RAM
  and Docker version and states that the numbers are relative to this environment.
- Synthetic PDFs do not represent real OCR complexity; extraction quality is P1-006's scope.
- Seeding is done through the API where the endpoint contract matters (documents, agreements, clauses)
  and through SQL where the fixture is pure queue input; both are documented.
- Load runs are excluded from CI; only the statistics unit tests and the self-test run there, so the
  harness cannot rot unnoticed without making CI slow or flaky.

## Evidence

Two independent runs on 2026-11-22, each with a fresh container and database (42.5 minutes each):

| Gate | Result |
|---|---|
| `dotnet build Convenciones.slnx -c Release -warnaserror` | 0 warnings, 0 errors |
| Unit | **253 passed** (248 + 5 statistics tests) |
| Architecture | **10 passed** |
| `--self-test` | **PASSED** — injected 300 ms delay observed (p95 317 ms), injected 1% error rate observed (0.67%, 1/150) |
| Integration | 365 passed |
| CI | pending the pull request |

| Scenario | Run 1 | Run 2 | Target | Verdict |
|---|---|---|---|---|
| Mixed read p95 (20 req/s) | 52.7 ms | 49.7 ms | ≤ 200 ms | pass |
| Burst read p95 (100 req/s) | 121.6 ms | 107.8 ms | ≤ 200 ms | pass |
| Burst read p99 | 176.8 ms | 181.3 ms | ≤ 500 ms | pass |
| Write p95 (burst) | 57.5 ms | 56.0 ms | ≤ 400 ms | pass |
| Upload p95 (1 MiB) | 43.5 ms | 43.2 ms | ≤ 1,500 ms | pass |
| 5xx rate | 0% | 0% | < 0.1% | pass |
| Burst queue wait p95 | 12.6 s | 12.7 s | ≤ 60 s | pass |
| Run duration p95 | 2.2 s | 2.2 s | ≤ 30 s | pass |
| 500-doc drain, one worker | 1,010.8 s | 1,005.7 s | ≤ 1,800 s | pass |
| Availability | not measured | not measured | — | out of scope |

Reproducibility: p95s and worker metrics within 10% (burst read p95 −11.4% while its p99 moved +2.5%;
write/upload p99s moved 36–47% on 600–900 samples — tail noise, documented in the report).

Report: [p2-005-capacity.md](../operations/p2-005-capacity.md). Artifacts:
[run 1](../operations/p2-005-results/2026-11-22-run1.json),
[run 2](../operations/p2-005-results/2026-11-22-run2.json).

## Left undone

- The signed capacity decision (owner), availability measurement and real-provider latency, by design.
