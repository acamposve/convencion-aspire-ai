# F-06 evaluation report

| Version | Value |
|---|---|
| Dataset | `1` (synthetic-v2-draft) |
| Prompt | `extraction/v2` |
| Taxonomy | `20260109000100` |
| Predictions | `baseline-corpus-2026-11-15` / `clauses-mistral` |
| Classification prompt | `classification/v2` |
| Deployed commit | `97a2cac09811af7579b4746fbfd08d283f1dfcfa` |
| Generated (UTC) | 2026-11-15T15:36:39Z |

## Extraction

| Metric | Value |
|---|---:|
| Annotated clauses | 36 |
| Predicted clauses | 36 |
| Matched | 36 |
| Missing | 0 |
| Extra | 0 |
| Precision | 1.0000 |
| Recall | 1.0000 |
| F1 | 1.0000 |

Field accuracy over matched clauses:

| Field | Correct / Matched | Rate |
|---|---:|---:|
| Number | 36 / 36 | 1.0000 |
| Heading | 31 / 36 | 0.8611 |
| Page range | 28 / 36 | 0.7778 |
| Text fidelity | 36 / 36 | 1.0000 |

## Classification (multi-label, matched clauses only)

| Metric | Value |
|---|---:|
| Evaluated clauses | 36 |
| Exact set match | 27 (0.7500) |
| Macro F1 (categories with support) | 0.8048 |
| Micro precision / recall / F1 | 0.7692 / 0.9091 / 0.8333 |
| Primary accuracy | 0.9032 over 31 |
| Unclassified rate | 0.0278 (1 of 36) |
| Expected-empty clauses | 5 |
| False-positive categories | 9 |

| Category | Support | Predicted | TP | FP | FN | P | R | F1 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| confidentiality | 2 | 4 | 2 | 2 | 0 | 0.5000 | 1.0000 | 0.6667 |
| data_protection | 2 | 2 | 2 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |
| disputes | 2 | 3 | 2 | 1 | 0 | 0.6667 | 1.0000 | 0.8000 |
| duration | 2 | 1 | 1 | 0 | 1 | 1.0000 | 0.5000 | 0.6667 |
| force_majeure | 1 | 1 | 1 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |
| governing_law | 3 | 3 | 3 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |
| ip | 2 | 1 | 1 | 0 | 1 | 1.0000 | 0.5000 | 0.6667 |
| liability | 3 | 3 | 3 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |
| other | 1 | 0 | 0 | 0 | 1 | 0.0000 | 0.0000 | 0.0000 |
| parties | 1 | 1 | 1 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |
| pricing | 3 | 3 | 3 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |
| scope | 5 | 10 | 5 | 5 | 0 | 0.5000 | 1.0000 | 0.6667 |
| termination | 4 | 4 | 4 | 0 | 0 | 1.0000 | 1.0000 | 1.0000 |
| warranties | 2 | 3 | 2 | 1 | 0 | 0.6667 | 1.0000 | 0.8000 |

| Document | Annotated | Predicted | Matched | P | R | F1 |
|---|---:|---:|---:|---:|---:|---:|
| synthetic-d01-labour-construction | 6 | 6 | 6 | 1.0000 | 1.0000 | 1.0000 |
| synthetic-d02-commercial-supply | 6 | 6 | 6 | 1.0000 | 1.0000 | 1.0000 |
| synthetic-d03-nda | 5 | 5 | 5 | 1.0000 | 1.0000 | 1.0000 |
| synthetic-d04-service-sla | 5 | 5 | 5 | 1.0000 | 1.0000 | 1.0000 |
| synthetic-d05-ip-licence | 5 | 5 | 5 | 1.0000 | 1.0000 | 1.0000 |
| synthetic-d06-irregular-formatting | 5 | 5 | 5 | 1.0000 | 1.0000 | 1.0000 |
| synthetic-d07-unclassifiable | 4 | 4 | 4 | 1.0000 | 1.0000 | 1.0000 |

## How to read this

These numbers describe agreement with the **annotation file**, not with reality. A high score on a small or self-authored corpus proves the scorer works; it does not prove the pipeline is accurate. Only a representative corpus with independent, human-validated annotations supports a quality claim.

