# F-06.5 — AI-adjudicated reference corpus (v2)

**Date:** 2026-11-15 · **Dataset:** `synthetic-v2` v2 · **Status:** reviewed, **AI-generated**

## What this is

The first `reviewed` version of the corpus, produced without human reviewers. It exists so metrics can
be computed against a stable, page-correct label set while human review is unavailable.

**It is not human ground truth and must never be described as such.** Annotators and adjudicator are
the same language model family, so the labels share whatever bias that model has.

## Provenance (recorded in the dataset itself)

| Field | Value |
|---|---|
| `corpusId` / `datasetVersion` | `synthetic-v2` / `2` |
| `status` | `reviewed` |
| `annotators` | `ChatGPT — borrador asistido por IA (no es revisión humana)` |
| `reviewers` | `ai-adjudicator-v1` |
| `notes` | "AI-adjudicated reference ground truth (v2)… **NOT human-validated**. Page ranges verified from the PDFs with the pdf-pages command." |

## 1. Page verification (`pdf-pages`)

New command: it locates every clause inside the rendered PDF by text and reports the real page span
next to what the draft claimed. It reads word positions rather than raw glyph runs, because the PDF
joins words across line breaks (`alliindicados`).

Result: **36 / 36 clauses located, 3 corrections.**

| Clause | Draft | PDF |
|---|---|---|
| d01-c5 | 1-2 | **1-1** |
| d04-c3 | 1-2 | **1-1** |
| d06-c1 | 1-2 | **1-1** |

The other 33 draft ranges were right — including the 7 clauses that genuinely start on page 2. So the
earlier "8 page misses" were 3 annotation errors and 5 model errors, not one or the other.

## 2. Adjudication (`ai-adjudicator-v1`)

Every row carries an explicit adjudicator, a resolution class and a rationale. No clause was left
undecided.

| Resolution | Clauses | Rule |
|---|---:|---|
| `confirmed-consensus` | 27 | A and B agreed; confirmed unchanged |
| `resolved-multilabel` | 4 | union of both label sets; primary = A's primary if still present, else B's |
| `unclassified-no-taxonomy-category` | 5 | working-time clauses left empty under taxonomy v1 |

The five unclassified clauses are `d01-c3` (JORNADA) and all of `d07`. That is the same decision the
F-06 taxonomy assessment recommended, now applied consistently instead of varying between runs.

## 3. Scores against the captured baseline run

| Metric | v1 (draft labels) | **v2 (AI-adjudicated)** |
|---|---:|---:|
| Extraction F1 | 1.0000 | 1.0000 |
| Number / text fidelity | 36/36 | 36/36 |
| Heading accuracy | 31/36 | 31/36 |
| Page-range accuracy | 28/36 | **29/36** |
| Exact category-set match | 27/36 (0.750) | 26/36 (0.722) |
| **Macro-F1** | 0.8048 | **0.8792** |
| **Micro-F1** | 0.8333 | **0.8649** |
| Primary accuracy | 0.9032 | 0.9677 |
| Unclassified rate | 0.0278 | 0.0278 |
| False-positive categories | 9 | **7** |

Reading the deltas honestly:

- **Macro-F1 rose 0.074** mostly because the working-time clauses now agree with the model's own
  "unclassified" behaviour on 1 of 5 and multi-label unions match more of what the model predicts. Part
  of this is genuine label correction; part is correlation, because the annotator and the evaluated
  model are siblings.
- **Heading accuracy did not move.** The 5 misses are in `d04` and come from the *captured* predictions,
  which were produced from the pre-fix rendering (`- ALCANCE DE LOS SERVICIOS`). The PDFs were fixed
  afterwards and verified on `d04`; a fresh run would very likely reach 36/36, but that has **not** been
  measured.
- **Page accuracy is 29/36.** These are now genuine model page errors: on the two-page documents the
  model assigns page 1 to clauses that start on page 2.

## 4. Limits — read before citing these numbers

1. **No human validation.** Two people disagreeing with this corpus is the expected next signal, not a
   failure of the corpus.
2. **Correlated bias.** Annotator and model are the same family; agreement is inflated relative to a
   human benchmark.
3. **Extraction is near-tautological** — the PDFs were generated from the same text.
4. **Heading metric is stale** until a fresh capture against render v2.

## 5. Reproduce

```
pdf-pages       --dataset docs/quality/corpus/synthetic-v2-draft.dataset.json \
                --pdf-dir docs/quality/corpus/pdf \
                --out-json docs/quality/corpus/synthetic-v2-draft.pages.json

corpus-materialize --dataset docs/quality/corpus/synthetic-v2-draft.dataset.json \
                --adjudication docs/quality/review/adjudication.json \
                --out docs/quality/corpus/synthetic-v2.dataset.json --version 2 --note "…"

evaluate        --dataset docs/quality/corpus/synthetic-v2.dataset.json \
                --predictions docs/quality/corpus/baseline-predictions-v2.json \
                --out-json docs/quality/results/baseline-v2.result.json \
                --out-md  docs/quality/results/baseline-v2.report.md
```

No network, no database, no provider call at any step.
