# Baseline corpus run — staging `97a2cac`

**Date:** 2026-11-15 · **Documents:** 7 · **Clauses:** 36 · **Worker:** 7 scoped windows, disabled after each

First time the pipeline has been run over the full 36-clause corpus. The purpose is a stakeholder
artifact of **actual model behaviour**, not a quality verdict: the corpus annotations are still draft.

## How it was produced

| Step | Detail |
|---|---|
| Input | 7 PDFs rendered from the corpus (`docs/quality/corpus/pdf/`), A4, real text layer, no OCR needed |
| Upload + extraction | 7 documents, all `Completed` on the first attempt |
| Processing | one worker window per document (the worker scope accepts a single document id), 10-minute watchdog, disabled and verified after each |
| Commit | `97a2cac` — no deploy was made for this run |
| Output | `docs/quality/corpus/baseline-predictions-97a2cac.json` |
| Scoring | offline, no provider calls: `docs/quality/results/baseline-97a2cac.report.md` |

### Provenance caveat

The deployed build predates F-06.1, so `processing_run.prompt_version` records `extraction/v1` even
though the prompt text executed is the v2 contract merged in PR #95. The predictions file records
`promptVersion: extraction/v2` (the contract that actually ran) **and** the deployment commit, and carries
an explicit `provenance` object explaining the discrepancy. Prompt content is identical between the two
labels; only the constant differs.

## Quota consumed

| Counter | Before | After |
|---|---:|---:|
| documents | 6 / 6 | **13 / 20** |
| pages | 1 / 100 | **11 / 100** |
| input tokens | 1 458 | 13 358 |
| output tokens | 568 | 6 516 |
| estimated cost | $0.0019 | **$0.0200** |

Limit raised from 6 to 20 beforehand (authorized). Note the quota row is per company, not per period;
usage counters are monthly.

## Results

| Dimension | Value |
|---|---|
| Clauses extracted | **36 / 36**, P = R = F1 = 1.00 |
| Number accuracy | 36 / 36 |
| Heading accuracy | 31 / 36 (0.861) |
| Page-range accuracy | 28 / 36 (0.778) |
| Text fidelity | 36 / 36 |
| Exact category-set match | 27 / 36 (0.750) |
| Macro-F1 | **0.8048** |
| Micro precision / recall / F1 | 0.7692 / 0.9091 / **0.8333** |
| Primary-category accuracy | 0.9032 over 31 |
| Unclassified rate | 1 / 36 (0.028) |
| False-positive categories | 9 |

### Classification by category

| Category | Support | Predicted | P | R | F1 |
|---|---:|---:|---:|---:|---:|
| data_protection | 2 | 2 | 1.00 | 1.00 | 1.00 |
| force_majeure | 1 | 1 | 1.00 | 1.00 | 1.00 |
| governing_law | 3 | 3 | 1.00 | 1.00 | 1.00 |
| liability | 3 | 3 | 1.00 | 1.00 | 1.00 |
| parties | 1 | 1 | 1.00 | 1.00 | 1.00 |
| pricing | 3 | 3 | 1.00 | 1.00 | 1.00 |
| termination | 4 | 4 | 1.00 | 1.00 | 1.00 |
| warranties | 2 | 3 | 0.67 | 1.00 | 0.80 |
| disputes | 2 | 3 | 0.67 | 1.00 | 0.80 |
| confidentiality | 2 | 4 | 0.50 | 1.00 | 0.67 |
| duration | 2 | 1 | 1.00 | 0.50 | 0.67 |
| ip | 2 | 1 | 1.00 | 0.50 | 0.67 |
| scope | 5 | 10 | 0.50 | 1.00 | 0.67 |
| other | 1 | 0 | 0.00 | 0.00 | 0.00 |

## What the numbers actually say

**Two of the field metrics are measurement artifacts, not model errors.** Reporting them as quality
would be wrong:

- **Heading accuracy 31/36** — all five misses are in `d04`, where the model returned
  `- ALCANCE DE LOS SERVICIOS` with a leading dash. The cause is my rendering: the PDF printed
  `CLAUSULA PRIMERA — ALCANCE DE LOS SERVICIOS` on one line, so the dash belongs to the source. The
  annotation omits it. **Fixed and verified** — see "Render v2" below.
- **Page-range accuracy 28/36** — all eight misses are in `d04` and `d06`, where the draft annotations
  assume a pagination the rendered PDF does not have. The corpus was authored before the PDFs existed.
  Fix: re-annotate page ranges against the rendered PDFs (new `datasetVersion`). Render v2 did not
  change this; it is an annotation task, not a rendering one.

**Extraction scoring is near-tautological** for this corpus: the PDFs were generated from the same text
the annotations describe, so 36/36 boundary matching confirms the pipeline preserves clause text, not
that it segments real contracts well.

**The classification signal is real.** Against the draft labels the model over-predicts:

- `scope` and `confidentiality` are predicted far more often than annotated (10 vs 5, 4 vs 2), which
  produces most of the 9 false positives. Plausible cause: "objeto/alcance" wording attracts `scope`,
  and any reference to reserved information attracts `confidentiality`.
- `duration` and `ip` each miss one clause (recall 0.50).
- `other` is **never** predicted: the one clause annotated `other` (governance meetings) came back with
  a different label. Either the model avoids the catch-all or the prompt does not describe it well
  enough — worth a prompt investigation, not a taxonomy change.

These are draft-label comparisons. A human review may conclude the model was right and the annotation
wrong in some of these cases; that is exactly what the review round is for.

## Reproduce

```
dotnet run --project tools/Convenciones.Evaluation -- evaluate \
  --dataset docs/quality/corpus/synthetic-v2-draft.dataset.json \
  --predictions docs/quality/corpus/baseline-predictions-97a2cac.json \
  --out-json docs/quality/results/baseline-97a2cac.result.json \
  --out-md  docs/quality/results/baseline-97a2cac.report.md
```

No network, no database, no provider call.

## Render v2 — heading fix (verified)

The PDFs were re-rendered with the clause number and heading on separate lines instead of a single
`NUMBER — HEADING` line. The seven documents keep the same page counts and text layers.

`d04` was re-processed as the verification probe (it is the only document with the dash artifact): one
scoped worker window, worker disabled immediately after.

| Clause | Heading before (render v1) | Heading after (render v2) |
|---|---|---|
| d04-c1 | `- ALCANCE DE LOS SERVICIOS` | `ALCANCE DE LOS SERVICIOS` |
| d04-c2 | `- PRECIO Y REAJUSTE` | `PRECIO Y REAJUSTE` |
| d04-c3 | `- CONFIDENCIALIDAD Y DATOS` | `CONFIDENCIALIDAD Y DATOS` |
| d04-c4 | `- RESPONSABILIDAD` | `RESPONSABILIDAD` |
| d04-c5 | `- TERMINACION` | `TERMINACION` |

Five of five repaired. A full re-run would be expected to move heading accuracy from 31/36 to 36/36;
that is a projection, not a measurement — the full baseline has not been re-run.

**Provenance consequence:** the committed PDFs are now render v2, while
`baseline-predictions-97a2cac.json` was captured from render v1. Re-scoring the unchanged predictions
file remains valid (it is a captured artefact), but re-processing the committed PDFs would produce a
different result for `d04`. The next full baseline should be captured after the human review, against
these render-v2 files, so corpus, PDFs and predictions share one generation.

Cost of the verification: 1 document (quota 13 → 14 of 20).
