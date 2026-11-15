# F-06 — Implementation report

**Date:** 2026-11-14 · **Mode:** local and offline · **Status:** framework implemented; corpus **not**
representative.

## 1. Starting state

| Item | Value |
|---|---|
| Branch / HEAD | `fix/classification-prompt-contract` @ `cc96acb` (already merged into `main` as `97a2cac`) |
| Working tree | `deploy/Caddyfile` modified; nine untracked audit reports; `admin.ps1` (not opened) |
| Committed or pushed | **nothing** — implementation is uncommitted by design |

## 2. What already existed (reused, not duplicated)

`tools/Convenciones.Evaluation` already contained:

- `BoundaryMetrics` — greedy longest-common-substring matching at a 0.8 threshold;
- `ClassificationMetrics` — per-category F1, macro over supported categories, micro F1;
- `EvaluationMetricsTests` — four tests covering exact match, missing clause, partial text, macro F1;
- `DocumentProfiler` — PDF text-layer profiling.

The new work **extends** these rather than replacing them: `BoundaryMetrics.Compute` now delegates to a
new `Match` method that returns the matched pairs, so field-level accuracy becomes possible without
changing any existing behaviour or test.

## 3. Design decisions

| Decision | Rationale |
|---|---|
| Two independent scorers | classification is scored **only** on boundary-matched pairs, so a segmentation error is not charged twice |
| Corpus and predictions as separate JSON | model execution is decoupled from measurement; re-scoring never calls a provider, so cost and non-determinism stay out of the loop |
| Version-mismatch is an error | a prediction file for another dataset version is refused, not silently scored |
| Macro-F1 excludes categories with support 0 | an absent category should not drag the mean down or divide by zero; it is still listed with its false positives |
| Fail-closed validation | empty datasets, blank clause text, duplicate ids, invalid page ranges, and a `primary` outside `categories` all abort with a clear message |
| No new service | the existing console tool gains an `evaluate` subcommand; no microservice, no extra project |

## 4. Files

**Extended**

- `tools/Convenciones.Evaluation/Metrics.cs` — added `ClauseMatch` and `BoundaryMetrics.Match`; `Compute` reuses it.
- `tools/Convenciones.Evaluation/Program.cs` — `evaluate` subcommand; default PDF profiling preserved.

**Added**

- `tools/Convenciones.Evaluation/EvaluationCorpus.cs` — versioned dataset/prediction models, JSON I/O, structural validation.
- `tools/Convenciones.Evaluation/EvaluationEngine.cs` — extraction and classification metrics, aggregation, deterministic scoring.
- `tools/Convenciones.Evaluation/EvaluationReport.cs` — machine-readable JSON plus a Markdown rendering.
- `tests/Convenciones.UnitTests/Evaluation/F06EvaluationTests.cs` — 17 tests.
- `docs/quality/f06-evaluation-methodology.md`, `f06-corpus-and-annotation-protocol.md`, `f06-taxonomy-assessment.md`, this report.
- `docs/quality/corpus/synthetic-labor-v1.dataset.json`, `docs/quality/corpus/d6-run-fbd16bc0.predictions.json`.
- `docs/quality/results/d6-synthetic-labor-v1.result.json`, `docs/quality/results/d6-synthetic-labor-v1.report.md`.

## 5. Metrics implemented

| Dimension | Metric |
|---|---|
| Extraction | matched, missing, extra, precision, recall, F1 |
| Extraction (matched pairs) | number accuracy, heading accuracy, page-range accuracy, text fidelity |
| Classification | per-category precision/recall/F1 with support, macro-F1 (support > 0), micro P/R/F1 |
| Classification | exact-set match, primary accuracy, unclassified rate, expected-empty count, false-positive categories |

Definitions, normalisation rules and exclusions are in
[f06-evaluation-methodology.md](f06-evaluation-methodology.md).

## 6. Tests

`dotnet test tests/Convenciones.UnitTests` — **171 passed**, 0 failed (154 before + 17 new).

New coverage: perfect extraction/classification, missing clause, extra clause, sub-threshold boundary,
wrong heading, wrong page range, wrong categories (FP/FN), multi-label, unclassified, zero-support
category excluded from macro, empty dataset, blank annotation text, duplicate clause ids, primary
outside categories, invalid page range, dataset-version mismatch, determinism.

Existing `EvaluationMetricsTests` — 4 tests, unchanged and green.

**Integration suite:** `249 passed`, 0 failed (2 m 15 s) — no regression.

**Build:** `dotnet build Convenciones.slnx -c Release -warnaserror` → 0 warnings, 0 errors.

## 7. Sample evaluation (offline, no provider call)

```
dotnet run --project tools/Convenciones.Evaluation -- evaluate \
  --dataset docs/quality/corpus/synthetic-labor-v1.dataset.json \
  --predictions docs/quality/corpus/d6-run-fbd16bc0.predictions.json \
  --out-json docs/quality/results/d6-synthetic-labor-v1.result.json \
  --out-md  docs/quality/results/d6-synthetic-labor-v1.report.md
```

Result on the 4-clause D6 document:

| Metric | Value |
|---|---|
| Extraction matched | 4 / 4, P = R = F1 = 1.0000 |
| Number / heading / page / text | 4 / 4 each |
| Classification macro-F1 / micro-F1 | 1.0000 / 1.0000 |
| Primary accuracy | 1.0000 over 3 |
| Unclassified rate | 0.2500 (1 of 4 — the `JORNADA` clause, expected empty) |
| False-positive categories | 0 |

**This is not a quality result.** Both files describe the same four sentences; the annotation was
authored against the same text the model read, and the model happened to agree. It proves the scorer
runs, is deterministic and can be driven from captured output — nothing more.

## 8. Remaining gaps

| Gap | Classification |
|---|---|
| Representative, independently annotated corpus (target: ≥ 3 documents per contract type, all 14 categories, all difficulty cases) | **BLOCKED** — needs human annotation effort and, for non-synthetic material, explicit authorization |
| Confidence calibration | **NOT IMPLEMENTED** — needs a large sample per confidence bucket; the field exists for later |
| Character-offset boundary metrics (`sourceCharStart/End`) | **NOT IMPLEMENTED** — the format has no annotation field yet |
| Prompt version does not change when prompts change (`extraction/v1` covers two contracts) | **NOT IMPLEMENTED** — requires bumping `ExtractionOptions.PromptVersion` / `ClassificationOptions.PromptVersion`; tracked as a risk in the methodology |
| `working_conditions` taxonomy category | **DEFERRED** — recommendation documented, decision required, no migration written |
| Batch harness that runs the pipeline over a whole corpus to produce predictions | **DEFERRED** — today predictions are captured per run; a batch producer would need staging quota and authorization |
| CI job for the evaluation sample | **DEFERRED** — the sample is documentation, not a gate; a gate would need a stable corpus first |

## 9. Confirmations

| Item | Status |
|---|---|
| External AI calls (Foundry / Mistral) | **none** |
| Document Intelligence calls | **none** |
| Processing runs created | **none** |
| Quota consumed or changed | **none** |
| Deployments | **none** |
| Staging modified | **none** — worker remains disabled |
| Tenant roles or permissions changed | **none** |
| Production taxonomy or prompts changed | **none** |
| Commits, pushes, PRs | **none** |
| `deploy/Caddyfile`, audit reports, `admin.ps1` | untouched |

## 10. Verdict

The evaluation **framework** is complete and tested: deterministic, offline, version-aware, with
machine-readable results and a human-readable report. The **quality evaluation** is not complete, and
cannot be until a representative corpus is annotated by people. Those are the two separate
deliverables the task asked to keep separate, and they remain separate here.
