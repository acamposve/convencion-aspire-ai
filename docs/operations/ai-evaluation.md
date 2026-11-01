# AI evaluation — corpus profile, harness and quality gates

**Date:** 2026-11-01 · **Status:** harness implemented; real model evaluation pending credentials and annotations.

## 1. Corpus available

| Document | Pages | Characters | Non-whitespace | Chars/page | Text layer | OCR |
|---|---:|---:|---:|---:|---|---|
| `convencion-colectiva-de-la-construccion-2023-GOE-6.752.pdf` (in `docs/test`) | 16 | 127,891 | 105,565 | 6,597.8 | yes | **not required** |

Measured locally with PdfPig (`tools/Convenciones.Evaluation`), 1.7 s. The document is a text PDF: the
average non-whitespace characters per page is far above the 50 threshold, so this sample does **not**
justify enabling OCR.

## 2. Harness

`tools/Convenciones.Evaluation` (console, no model call):

```
dotnet run --project tools/Convenciones.Evaluation -- docs/test
```

It prints pages, characters, density, the OCR signal and the profile duration. It never uploads the
PDF.

`Metrics.cs` implements the reproducible metrics:

- **Boundary precision/recall/F1**: greedy match of each annotated clause to the unused predicted
  clause with the highest longest-common-substring ratio; a match needs ≥ 0.8 of the annotated text.
- **Classification macro-F1**: per-category precision/recall/F1 over categories with support, plus
  micro-F1.
- Unit tests cover exact match, missing clause, partial text and macro-F1 (`EvaluationMetricsTests`).

No model output is fabricated: the metrics are only computed when real predictions and annotations
exist.

## 3. What is measured vs pending

| Item | State |
|---|---|
| PDF text layer / OCR need | **Measured** for the one sample |
| Extraction precision/recall/F1 | **Pending**: no annotated clauses and no Azure credential |
| Classification macro-F1 | **Pending**: same |
| Page accuracy | **Pending** |
| Schema validity | Exercised by unit/integration tests; not measured on real contracts |
| Cost per contract | **Estimated** below, not yet measured |

**Do not claim the quality gates are met.** The one contract is a smoke sample, not a golden set.

## 4. Cost estimate for this contract (estimate)

- 127,891 characters ≈ **32k input tokens**.
- Chunking at 10k characters yields ~13 chunks; extraction output estimate ~300 clauses × 120 tokens ≈
  **36k output tokens** (generous), classification adds ~12k.
- At the verified Bedrock `sa-east-1` Mistral Large 3 rate ($0.61/$1.82 per 1M), the model cost is
  roughly **$0.02 input + $0.07 output ≈ $0.09 per contract**; 1,000 contracts ≈ **$90/month**. Azure
  Marketplace pricing must be confirmed for the same model.

This is an estimate, not a measured invoice.

## 5. Quality gates (proposed, from #46)

| Metric | Beta gate |
|---|---:|
| Boundary precision | ≥ 95% |
| Boundary recall | ≥ 90% |
| Classification macro-F1 | ≥ 90% |
| Page accuracy | ≥ 98% |
| Schema validity (first attempt) | 100% |

Enabling the capability for real contracts requires a measured report against these gates; no number
is claimed before that report.

## 6. OCR decision

- **Enable OCR only when the text layer is insufficient.** This sample is text-based, so OCR would add
  cost and latency for no gain.
- Scanned PDFs already end with `ocr_required`; they are never reported as an empty success.
- Revisit after profiling the real corpus: if > 10–15% of contracts are scanned, add an OCR stage
  (Document Intelligence OCR or Tesseract) as its own issue and measure its cost.

## 7. Next steps

1. Provide 30–50 representative Spanish contracts and human annotations (clause boundaries, pages,
   categories), stored outside the repository if confidential.
2. Configure the Azure AI credential and run the harness against `clauses-mistral`.
3. Produce the measured report, decide OCR, and only then enable the worker for real contracts.
