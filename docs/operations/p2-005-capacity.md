# P2-005 — Capacity and performance baseline

**Status:** measured on 2026-11-22 with two independent runs. The thresholds below are **initial
benchmark targets, not an SLA**: they exist so pass/fail has a meaning, and the owner ratifies or
replaces them.

Raw artifacts:

- [`p2-005-results/2026-11-22-run1.json`](p2-005-results/2026-11-22-run1.json)
- [`p2-005-results/2026-11-22-run2.json`](p2-005-results/2026-11-22-run2.json)

## 1. What this baseline establishes — and what it does not

Establishes, for one API instance and one worker on a disposable local stack:

- throughput and latency percentiles for the read, write and upload paths at the confirmed profile;
- how the queue behaves when documents arrive as a burst and when 500 are queued at once;
- the single-worker processing rate with a synthetic 2 s provider latency, and the drain time;
- the generator's own CPU cost, so contention with the system under test is visible.

Does **not** establish, and reports as not measured:

- **Availability (99.9%)** — needs sustained observation in an authorized environment, not a local run.
- **Real provider latency and cost** — the worker uses a 2 s stub stage; Azure Document Intelligence
  and the model provider are out of scope (P1-006 owns quality and terms).
- **TLS, nginx and the deployment network path** — traffic is HTTP over loopback to Kestrel.
- **Horizontal scaling** — one API instance, one worker.
- **RLS cost** — the harness connects as the container superuser, which bypasses row-level security;
  a production connection would pay that cost.
- **Extraction/classification quality** — synthetic PDFs only.

## 2. Environment

| Item | Value |
|---|---|
| Host | LENOVO 20TBS1P900 (Windows 11 Pro 10.0.26300) |
| CPU | 11th Gen Intel Core i5-1135G7 @ 2.40 GHz — 4 cores / 8 logical processors |
| RAM | 15.7 GiB host; Docker Desktop VM 8.0 GB |
| Disk | 476 GiB, 170 GiB free |
| .NET | SDK 11.0.100 (the working tree's `global.json` selection), runtime 10.0.15 |
| Docker | Engine 29.9.3 |
| PostgreSQL | `postgres:17-alpine` container, default `max_connections = 100` |
| API | Release build of `Convenciones.Api`, Kestrel, HTTP on loopback |
| Connection pools | API 60, harness 10, worker 5 (all below the database ceiling) |
| Load generator | same machine as the system under test (`Convenciones.LoadTest`) |

The API ran with `Documents:DeletionIntervalSeconds=0` (no background deletion sweep) and
`Operations:MetricsIntervalSeconds=300` (minimal background sampling). The worker ran in-process with
a stub stage that waits 2 s ± 250 ms and returns success.

## 3. Method

Per confirmed profile: 10,000 documents/month baseline (~333/day, ~14/h, ~42/h business hours), a
5× 10-minute burst (~70 documents), a 500-document queued batch, 20 req/s sustained API (85% read /
15% write), a 100 req/s 60 s burst, and 2 uploads/s of a 1 MiB PDF for 5 minutes.

- **Fixtures**: one company, one `administrator` account (all capabilities except `system.superadmin`),
  40 documents (64 KiB), 60 agreements × 5 clauses (300 clauses), 20 published agreements. Documents,
  agreements and clauses are created through the API so they satisfy the real contracts. Worker inputs
  (document row + `processing_run`) are inserted directly because the queue row is the contract under
  measurement; the process endpoint's quota semantics are not part of this task.
- **`ANALYZE`** runs after the fixtures and again before the worker scenarios.
- **Rate control** is open-loop: requests are launched on a schedule, so a slow response cannot
  quietly reduce the offered rate. Per-request latency, status class and scenario kind are recorded.
- **Writes** create agreements; the company + start-date pair is unique, so every write uses its own
  validity start date, far from the fixture dates.
- **Percentiles** use linear interpolation between the closest ranks (`(n - 1) * p / 100`), pinned by
  unit tests.
- **Two runs** on the same host, each with a fresh container and database. Each run took 42.5 minutes.

## 4. Results

### 4.1 API scenarios — run 1

| Scenario | Requests | Errors | Throughput | Read p50 / p95 / p99 | Write p50 / p95 / p99 | Upload p50 / p95 / p99 | Generator cores | Peak concurrency |
|---|---|---|---|---|---|---|---|---|
| `api-mixed` (20 req/s, 85/15, 10 min) | 12,000 | 0 | 20.0 req/s | 15.1 / **52.7** / 69.8 ms | 18.8 / **25.9** / 46.6 ms | — | 0.016 | 3 |
| `api-burst` (100 req/s, 60 s) | 6,000 | 0 | 99.8 req/s | 14.9 / **121.6** / 176.8 ms | 18.9 / **57.5** / 95.6 ms | — | 0.034 | 15 |
| `api-upload` (2 × 1 MiB/s, 5 min) | 600 | 0 | 2.0 req/s | — | — | 31.4 / **43.5** / 58.7 ms | 0.015 | 1 |

### 4.2 API scenarios — run 2

| Scenario | Requests | Errors | Throughput | Read p50 / p95 / p99 | Write p50 / p95 / p99 | Upload p50 / p95 / p99 | Generator cores | Peak concurrency |
|---|---|---|---|---|---|---|---|---|
| `api-mixed` | 12,000 | 0 | 20.0 req/s | 14.5 / **49.7** / 61.7 ms | 17.2 / **22.7** / 33.4 ms | — | 0.018 | 3 |
| `api-burst` | 6,000 | 0 | 99.8 req/s | 13.6 / **107.8** / 181.3 ms | 17.3 / **56.0** / 140.4 ms | — | 0.041 | 24 |
| `api-upload` | 600 | 0 | 2.0 req/s | — | — | 31.8 / **43.2** / 80.0 ms | 0.017 | 1 |

Every response was a 200 or 201: 18,600 requests per run, zero 4xx and zero 5xx.

### 4.3 Worker and queue — both runs

| Scenario | Run | Documents | Completed | Drain | Rate | Queue wait p50 / p95 / p99 | Run duration p50 / p95 / p99 | Max queue |
|---|---|---|---|---|---|---|---|---|
| `worker-burst` (70 docs over 10 min) | 1 | 70 | 70 | 555.2 s | 7.6 docs/min* | 6.5 / **12.6** / 12.8 s | 2.0 / **2.2** / 2.3 s | 7 |
| `worker-burst` | 2 | 70 | 70 | 555.2 s | 7.6 docs/min* | 6.5 / **12.7** / 13.2 s | 2.0 / **2.2** / 2.2 s | 7 |
| `worker-drain` (500 queued at once) | 1 | 500 | 500 | 1,010.8 s | **29.7 docs/min** | 506.4 / 958.5 / 998.1 s | 2.0 / **2.2** / 2.3 s | 500 |
| `worker-drain` | 2 | 500 | 500 | 1,005.7 s | **29.8 docs/min** | 501.8 / 952.7 / 993.4 s | 2.0 / **2.2** / 2.2 s | 500 |

\* `worker-burst` lasts as long as its 10-minute arrival window; the rate is arrival-limited, not
worker-limited. The 60 s queue-wait target applies to this scenario. The 500-document drain queues
everything at once, so its queue wait is expected to be large; the drain-time target applies instead.

The 500-document drain rate of ~29.8 docs/min against a 2.0 s stub stage is 2.01 s per document:
the queue and worker add about 13 ms over the stub latency.

## 5. Result against the initial targets

| Dimension | Target | Run 1 | Run 2 | Verdict |
|---|---|---|---|---|
| Read p95 | ≤ 200 ms | 52.7 ms (mixed) / 121.6 ms (burst) | 49.7 / 107.8 ms | **pass** |
| Read p99 | ≤ 500 ms | 69.8 / 176.8 ms | 61.7 / 181.3 ms | **pass** |
| Write p95 | ≤ 400 ms | 25.9 / 57.5 ms | 22.7 / 56.0 ms | **pass** |
| Write p99 | ≤ 1,000 ms | 46.6 / 95.6 ms | 33.4 / 140.4 ms | **pass** |
| Upload 1 MiB p95 | ≤ 1,500 ms | 43.5 ms | 43.2 ms | **pass** |
| Upload 1 MiB p99 | ≤ 3,000 ms | 58.7 ms | 80.0 ms | **pass** |
| 5xx rate | < 0.1% | 0.00% | 0.00% | **pass** |
| Queue wait p95 (70-doc burst) | ≤ 60 s | 12.6 s | 12.7 s | **pass** |
| Run duration p95 | ≤ 30 s | 2.2 s | 2.2 s | **pass** |
| 500-doc drain, one worker | ≤ 1,800 s | 1,010.8 s | 1,005.7 s | **pass** |
| Availability 99.9% | — | — | — | **not measured** |

### Reproducibility

| Metric | Run 1 | Run 2 | Difference |
|---|---|---|---|
| Mixed read p95 | 52.7 ms | 49.7 ms | −5.7% |
| Burst read p95 | 121.6 ms | 107.8 ms | −11.4% |
| Burst read p99 | 176.8 ms | 181.3 ms | +2.5% |
| Burst write p99 | 95.6 ms | 140.4 ms | +47% |
| Upload p95 | 43.5 ms | 43.2 ms | −0.7% |
| Upload p99 | 58.7 ms | 80.0 ms | +36% |
| Drain (500 docs) | 1,010.8 s | 1,005.7 s | −0.5% |
| Burst queue wait p95 | 12.6 s | 12.7 s | +0.8% |
| Throughput (all scenarios) | 20.0 / 99.8 / 2.0 | 20.0 / 99.8 / 2.0 | 0% |

The two runs agree within 10% on every p95 and on the worker numbers. Two tails exceed that: the
burst read p95 differs by 11.4% while its p99 differs by 2.5%, and the write/upload p99s differ by
36–47% on 900 and 600 samples respectively. Tail percentiles at these sample sizes are dominated by
scheduler noise on a 4-core laptop, and the p99 values stay two orders of magnitude under their
targets; the p95s and the medians are the stable numbers to plan with.

## 6. Limitations

- The generator shares the machine with the API, the worker and the database; its CPU is reported and
  interpreted against the 4-core host. Absolute numbers are specific to this host.
- Docker Desktop's VM has 8 GB of RAM and shares the host CPU; a production host would not.
- PostgreSQL runs in a container with default settings; no tuning was applied, and the WAL is on the
  container filesystem.
- The harness connects as the container superuser, so RLS policy evaluation is not measured.
- Synthetic PDFs are structurally valid but do not reproduce real contract layout or OCR cost.
- Two runs are a reproducibility check, not a statistical sample of production behaviour.
- The worker rate is bounded by the stub's 2 s; real provider latency is not measured and will change
  the numbers. The overhead the queue and worker add is ~13 ms per document, which is what this task
  can legitimately claim.

## 7. Proposed capacity statement (unsigned)

For one worker and one API instance, on this host, with a 2 s synthetic provider latency:

1. **Processing:** one worker sustains **~29.8 documents/min (~1,790 documents/hour)**. The confirmed
   business-hour average is ~42 documents/hour and the 10-minute burst is ~7 documents/min, so the
   measured worker has roughly **42× headroom at the average** and **4.2× at the burst**. For a
   different provider latency `p`, the rate is approximately `60 / (p + 0.013)` documents/minute.
2. **Bulk import:** 500 queued documents drain in **~16.8 minutes**; the oldest item waits ~16 minutes.
   Plan imports with that expectation; the 60 s queue-wait target is not achievable when 500 documents
   are queued at once with one worker.
3. **API reads/writes:** at 20 req/s (85/15) the API answers reads at p95 ~50 ms and writes at p95
   ~25 ms; at 100 req/s for 60 s, reads stay at p95 ~110–122 ms with no errors. The burst roughly
   doubles read p95 and quadruples write p95 — the system degrades gracefully rather than failing.
4. **Uploads:** 2 × 1 MiB per second is not a bottleneck locally (p95 ~44 ms).
5. **Not claimed:** availability, behaviour above 100 req/s, multi-instance scaling, real provider
   latency, RLS cost, or production network paths.

**Owner decision required:** accept, adjust, or reject. Until signed, this is a measured baseline, not
an operating limit.

## 8. Reproduction

```powershell
dotnet build Convenciones.slnx -c Release -warnaserror
dotnet run --project tools/Convenciones.LoadTest -c Release --no-build -- run `
  --output docs/operations/p2-005-results/<date>-run1.json --label run1
```

`--only api` or `--only worker` runs one half; `--scale` shortens the durations for a smoke run and is
not used for the recorded artifacts. Docker must be running. `--self-test` needs neither Docker nor a
database and runs in CI.
