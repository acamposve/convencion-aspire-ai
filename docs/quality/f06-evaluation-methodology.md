# F-06 — Evaluation methodology

**Status:** implemented (local, offline) · **Dataset:** `synthetic-labor-v1` v1 · **Date:** 2026-11-14

This document defines **how quality is measured**. It deliberately says nothing about whether the
current quality is good — that requires the corpus described in
[the annotation protocol](f06-corpus-and-annotation-protocol.md).

## 1. Why two independent dimensions

Extraction and classification fail differently and are fixed differently. A classifier cannot be
judged on clauses that were segmented wrongly, and a perfect segmentation is worthless if the
categories are wrong. They are therefore scored separately, and classification is scored **only on
clauses that extraction matched**, so a segmentation error is counted once, by extraction, instead of
being charged again to classification.

## 2. Extraction metrics

Clauses are matched with the repository's existing `BoundaryMetrics.Match`: for each annotated clause,
the unused predicted clause with the highest **longest-common-substring ratio** is paired, and the pair
counts as a match when the ratio reaches **0.8** (the prediction covers at least 80 % of the annotated
text, in order). Matching is greedy and order-dependent; that is a deliberate, documented simplification.

| Metric | Definition |
|---|---|
| `matched` | annotated clauses paired to a prediction |
| `missing` | annotated − matched (false negatives) |
| `extra` | predicted − matched (false positives) |
| `precision` | matched / predicted |
| `recall` | matched / annotated |
| `F1` | harmonic mean of precision and recall |
| `numberAccuracy` | matched pairs whose clause number is equal after trimming, whitespace collapsing and upper-casing |
| `headingAccuracy` | same normalisation applied to headings |
| `pageAccuracy` | matched pairs with **both** `pageFrom` and `pageTo` equal |
| `textFidelity` | matched pairs whose text is identical after whitespace collapsing (case-sensitive) |

`textFidelity` is strict on purpose: a paraphrase is a defect, not a match. Empty vs. null headings
are treated as equal; a null number against a non-null one is a miss.

## 3. Classification metrics

Classification is **multi-label**: a clause may carry zero, one or several categories, so single-label
accuracy would be misleading. All metrics below are computed over matched clause pairs.

| Metric | Definition |
|---|---|
| per-category `P / R / F1` | computed per category from TP/FP/FN over evaluated clauses |
| `macroF1` | mean F1 over categories with **support > 0** |
| `microPrecision/Recall/F1` | computed from pooled TP/FP/FN across all categories |
| `exactMatch` | clauses whose predicted category set equals the annotated set |
| `primaryAccuracy` | over clauses with an annotated primary, the predicted primary equals it (null counts as wrong) |
| `unclassifiedRate` | evaluated clauses with no predicted category / evaluated clauses |
| `expectedEmpty` | annotated clauses with no category — reported so "correctly unclassified" is visible |
| `falsePositiveCategories` | total predicted categories absent from the annotation |

**Categories with zero positive examples** are reported (support 0, FP counted) but excluded from the
macro average: an F1 of 0 for a category that never occurs would distort the mean, and dividing by
its zero denominators is meaningless.

## 4. What is deliberately not measured

| Not measured | Why |
|---|---|
| **Confidence calibration** | needs a large annotated sample per confidence bucket; four clauses prove nothing. The field exists in the corpus format for later. |
| **Semantic correctness beyond the annotation** | the scorer compares against an annotation, it does not know law or intent. |
| **Character-level boundary offsets** | the matcher works on text overlap; `sourceCharStart/End` exist in the database but are not annotated yet. |
| **Cross-document consistency** | out of scope for this increment. |

## 5. Determinism and isolation

Scoring takes exactly two JSON files — a corpus and a captured prediction set — and produces a JSON
result plus a Markdown report. There is **no** network call, provider call, database connection or
staging dependency in the scoring path. The same two files always produce the same numbers; the only
varying field is the generation timestamp.

Model execution is deliberately **outside** the scorer. A prediction file can be produced from a
staging run, a recorded fixture or a future batch job; re-scoring never re-calls the provider, so cost
and non-determinism stay out of the measurement loop.

## 6. Version recording

Every report records four versions, and a prediction file whose `datasetVersion` does not match the
corpus is **rejected** rather than scored:

| Version | Meaning |
|---|---|
| `datasetVersion` | the annotation revision |
| `corpusId` | which corpus |
| `promptVersion` | the prompt contract the production code declares |
| `taxonomyVersion` | the migration that seeded the category set |
| `commit` (predictions) | the deployed commit that produced the output |

> **Historical ambiguity (cannot be repaired):** between PR #95 and F-06.1, the code declared
> `extraction/v1` while actually sending the v2 contract, so runs from that window carry a version that
> names the wrong text. The `commit` field is what disambiguates them. From F-06.1 onwards the contract
> is `extraction/v2` / `classification/v2`, and the version is **pinned to a SHA-256 of the prompt text**
> by `PromptCatalog` + `PromptCatalogTests`: editing a prompt without bumping the version fails the
> suite.

## 7. How results may be cited

A score is **agreement with an annotation file**. Citing it as "the system is 100 % accurate" is wrong
unless the corpus is representative, independently annotated, and large enough to make the confidence
interval meaningful. The sample in `docs/quality/results/` exists to prove the pipeline works, not to
support a quality claim.
