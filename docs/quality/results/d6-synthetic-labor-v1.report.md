# F-06 evaluation report

| Version | Value |
|---|---|
| Dataset | `1` (synthetic-labor-v1) |
| Prompt | `extraction/v2` |
| Taxonomy | `20260109000100` |
| Predictions | `fbd16bc0-a520-420b-a8ec-08c00f3293e3` / `clauses-mistral` |
| Classification prompt | `classification/v2` |
| Deployed commit | `97a2cac09811af7579b4746fbfd08d283f1dfcfa` |
| Generated (UTC) | 2026-11-14T13:50:46Z |

## Extraction

| Metric | Value |
|---|---:|
| Annotated clauses | 4 |
| Predicted clauses | 4 |
| Matched | 4 |
| Missing | 0 |
| Extra | 0 |
| Precision | 1.0000 |
| Recall | 1.0000 |
| F1 | 1.0000 |

Field accuracy over matched clauses:

| Field | Correct / Matched | Rate |
|---|---:|---:|
| Number | 4 / 4 | 1.0000 |
| Heading | 4 / 4 | 1.0000 |
| Page range | 4 / 4 | 1.0000 |
| Text fidelity | 4 / 4 | 1.0000 |

## Classification (multi-label, matched clauses only)

| Metric | Value |
|---|---:|
| Evaluated clauses | 4 |
| Exact set match | 4 (1.0000) |
| Macro F1 (categories with support) | 1.0000 |
| Micro precision / recall / F1 | 1.0000 / 1.0000 / 1.0000 |
| Primary accuracy | 1.0000 over 3 |
| Unclassified rate | 0.2500 (1 of 4) |
| Expected-empty clauses | 1 |
| False-positive categories | 0 |

| Category | Support | Predicted | TP | FP | FN | P | R | F1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| other | 1 | 1 | 1 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |
| parties | 1 | 1 | 1 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |
| pricing | 1 | 1 | 1 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |
| scope | 1 | 1 | 1 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |

| Document | Annotated | Predicted | Matched | P | R | F1 |
|---|---:|---:|---:|---:|---:|---:|
| synthetic-contract-2293bbef | 4 | 4 | 4 | 1.0000 | 1.0000 | 1.0000 |

## How to read this

These numbers describe agreement with the **annotation file**, not with reality. A high score on a small or self-authored corpus proves the scorer works; it does not prove the pipeline is accurate. Only a representative corpus with independent, human-validated annotations supports a quality claim.

