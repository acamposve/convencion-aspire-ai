# F-06.4 — Excel annotation workflow

**Date:** 2026-11-14 · **Mode:** local, offline · **Verdict: ready for reviewers**

Reviewers can now annotate in `.xlsx` and never touch JSON. The converter produces exactly the
submission format `review-import` already accepts, and the existing validation stays authoritative.

## 1. What was inspected first

| Source | What it fixed |
|---|---|
| `ReviewPackage.cs` | the submission schema: `packageId`, `corpusId`, `datasetVersion`, `reviewerSlot`, `reviewerIdentity`, `status`, `taxonomyKeys`, `instructions`, `clauses[]`, `submissionVersion` |
| `ReviewClause` | per-clause fields: `clauseId`, `documentId`, `contractType`, `number`, `heading`, `text`, `pageFrom`, `pageTo`, `categories`, `primary`, `ambiguous`, `notes` |
| `ReviewWorkflow.ValidateSubmission` | the authoritative rules (identity, version match, missing/duplicate/unknown ids, taxonomy, primary) |
| `review-import` | refuses to overwrite, never mutates the corpus |
| taxonomy migration | the 14 keys and their descriptions for the reference sheet |

The Excel columns were derived from these types, not from assumptions.

## 2. Templates

`docs/quality/review/review-A-template.xlsx` and `review-B-template.xlsx`, generated from the corpus:

| Element | Content |
|---|---|
| Sheet `Anotacion` | title, corpus and dataset version, identity cell **B3**, submission version **B4**, instructions, headers at row 7, **36 clause rows** from row 8 |
| Sheet `Taxonomia` | the 14 keys with name, description and whether the key is active for this round |
| Columns A–E, H | reference only: Clause ID, document, contract type, number, heading, clause text (wrapped) |
| Columns F, G | **editable**: start and end page, pre-filled from the draft annotation so reviewers can correct them against the PDF |
| Columns I–L | **editable**: categories, primary, ambiguous, notes |
| Aesthetics | bold header, frozen rows above, sized columns, bordered cells, shading on the immutable id column |

**No draft categories or model predictions appear anywhere in the workbook** — verified by reading
every answer column. The blind-review requirement is preserved: A and B differ only by slot.

Templates are generated, not hand-built, so they cannot drift from the corpus:

```
review-template --dataset docs/quality/corpus/synthetic-v2-draft.dataset.json --slot A --out review-A-template.xlsx
```

## 3. Converter

```
review-excel --dataset <corpus.json> --slot A --input review-A.xlsx --out submission.json
```

It reads the workbook, validates every row, and writes the JSON that `review-import` expects. Errors are
addressed to the row the reviewer can see:

```
La planilla de revisión tiene errores.
  Fila 12 · Categorías: Categoría desconocida: 'working_conditions'. Use solamente las claves de la hoja Taxonomia.
  Fila 9 · Categoría principal: 'liability' no está entre las categorías seleccionadas en la columna H.
```

| Rule | Behaviour |
|---|---|
| Clause IDs | preserved exactly; unknown ids rejected |
| Duplicates | rejected, naming both rows |
| Missing clauses | rejected ("no borre filas de la plantilla") |
| Categories | matched case-insensitively against `taxonomyKeys`, canonicalised; unknown keys rejected, never dropped |
| Primary | must be one of the selected categories |
| Reviewer identity | required in B3 |
| Ambiguous | accepts Sí/Si/S/Yes/Y/True/1 and No/N/False/0; anything else rejected with the row |
| Empty fields | an empty id on a row with answers is an error; fully blank rows are padding |
| Untouched sheet | a named sheet where every answer is empty is rejected: it would otherwise read as "all 36 clauses are unclassifiable" |
| Overwrite | refuses to replace an existing output file |
| Incomplete submissions | rejected — a spreadsheet is never a partial review |

The converter does **not** reimplement the authoritative rules: after parsing, it calls
`ReviewWorkflow.ValidateSubmission`, the same method `review-import` uses. The Excel layer adds row
numbers and Spanish wording; it cannot loosen a rule.

## 4. Pipeline

```
template (.xlsx)  →  reviewer fills H-K + B3  →  review-excel  →  submission.json
                                                                       ↓
                                                              review-import (unchanged)
                                                                       ↓
                                                          review-adjudicate (unchanged)
```

`review-import` was not modified and remains the gate: a converted file still has to pass it, and an
import still does not mark anything as reviewed.

## 5. Validation against a third-party reader

The workbooks were opened with **openpyxl** (independent of the OpenXml SDK) with warnings treated as
errors: two sheets, 36 clause rows, 14 taxonomy rows, empty identity, no answers. An earlier version
lacked the default style parts and openpyxl warned; `cellStyleFormats`/`cellStyles` were added, and the
warning is gone. Accented characters round-trip as real Unicode (`0xf3`, `0xed`), not replacement
characters.

## 6. Tests

| Test | Covers |
|---|---|
| `Valid_spreadsheet_round_trips_into_a_submission` | happy path |
| `Multiple_selected_categories_are_preserved` | multi-label |
| `Reviewer_notes_and_ambiguity_are_preserved` | notes + Sí |
| `Unknown_category_identifies_the_row_and_field` | row-addressed error |
| `Primary_outside_the_selection_is_rejected` | cross-field rule |
| `Missing_reviewer_identity_is_rejected` | required identity |
| `Missing_clause_is_rejected` | deleted row |
| `Duplicate_clause_ids_are_rejected` | duplicates |
| `Unknown_clause_id_is_rejected` | unknown id |
| `Invalid_ambiguity_value_is_rejected_not_ignored` | "Quizás" |
| `Excel_file_is_never_overwritten_silently` | no overwrite |
| `Named_but_untouched_sheet_is_rejected_as_empty` | identity filled, nothing else |
| `Converted_submission_passes_the_authoritative_import_validation` | integration with `review-import` |
| `Template_has_no_answers_and_keeps_every_clause` | blank template cannot be submitted |

| Suite | Result |
|---|---|
| `ReviewExcelTests` | **14 passed** |
| Full unit suite | **222 passed**, 0 failed |
| Integration suite | **249 passed**, 0 failed |
| Build `-c Release -warnaserror` | 0 warnings, 0 errors |

## 7. Dependency

`DocumentFormat.OpenXml` **3.5.1** (MIT, Microsoft) was added to `Directory.Packages.props` and to
`Convenciones.Evaluation.csproj`. It requires no Office installation at build or run time, and central
transitive pinning needed no extra entries — restore and build are clean.

## 8. Deliverables

| Item | Path |
|---|---|
| Reviewer A template | `docs/quality/review/review-A-template.xlsx` |
| Reviewer B template | `docs/quality/review/review-B-template.xlsx` |
| Spanish instructions | `docs/quality/review/INSTRUCCIONES-revisores.md` |
| Converter | `tools/Convenciones.Evaluation/ReviewExcel.cs` + `review-excel` command |
| Template generator | `review-template` command |
| Tests | `tests/Convenciones.UnitTests/Evaluation/ReviewExcelTests.cs` |

## 9. Known limitations

- No dropdown for the ambiguous column; the parser accepts the documented spellings and rejects the rest.
- The workbook must keep its structure (headers, clause ids, no deleted rows). This is stated in the
  instructions and enforced by the converter.
- Reviewers still transcribe from a printed or on-screen contract; the file does not extract text from
  PDFs, by design.
- If a reviewer opens the file in a tool that rewrites the markup, the converter reads values only, so
  formatting loss does not matter as long as the cells survive.

## 10. Confirmations

| Item | Status |
|---|---|
| Taxonomy modified | **none** |
| Original corpus annotations modified | **none** |
| Clauses marked reviewed | **none** |
| Disagreements adjudicated | **none** |
| Synthetic human annotations generated | **none** |
| External AI calls | **none** |
| Processing runs / staging quota | **none** |
| Deployment, commit, push, merge | **none** |
| `global.json` / SDK configuration | **untouched** |

## 11. Update — editable pages and corpus publishing

Two gaps in the first version are now closed.

**Page ranges are editable.** The spreadsheet grew two columns (`F — Página desde`, `G — Página hasta`),
pre-filled from the draft annotation so reviewers correct them against the PDF instead of inheriting a
known-wrong pagination. They are validated as positive integers with `from <= to`; a blank or
non-numeric value is rejected with the row and column name. The answer columns moved to `I–L`
(categories, primary, ambiguous, notes).

**Corpus v2 can be published without hand-editing JSON.**

```
corpus-materialize --dataset <corpus-v1.json> --adjudication <finalized.json> \
                   --out <corpus-v2.json> --version 2
```

It reads the adjudication worksheet after a human has filled `resolution`, `rationale`, `adjudicator`,
`finalCategories`, `finalPrimary` (and the final page range when reviewers disagreed on pages) and emits
a `reviewed` corpus at the next dataset version. It refuses to publish when a clause has no adjudicator,
when a disagreement has no final decision, when a page disagreement has no final range, or when the
version would not change. The output is passed through the same validator as any hand-written corpus.

Tests added: 3 page-field cases in `ReviewExcelTests`, 8 publishing cases in `CorpusPublishingTests`
(agreement, unresolved disagreement, adjudicated decision, page disagreement, page override, anonymous
adjudicator, version reuse, validation of the published corpus). Full unit suite: **233 passed**.
