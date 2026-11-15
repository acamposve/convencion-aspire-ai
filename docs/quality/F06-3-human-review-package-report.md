# F-06.3 — Human annotation review package

**Date:** 2026-11-14 · **Mode:** local, offline · **Verdict: READY FOR REVIEW** — the package is built
and tested; **no human review has occurred**, and nothing here is accepted ground truth.

## 1. Initial repository state

| Item | Value |
|---|---|
| Branch / HEAD | `fix/classification-prompt-contract` @ `cc96acb…` |
| Working tree | F-06/F-06.1/F-06.2 changes, `deploy/Caddyfile`, audit reports; `global.json` intentionally pins SDK 11.0.100 (maintainer decision, outside this changeset) |
| Unrelated changes | `deploy/Caddyfile` and audit reports — untouched |

## 2. Files created or modified

| File | Change |
|---|---|
| `tools/Convenciones.Evaluation/ReviewPackage.cs` | **new** — blind export, validated import, adjudication worksheet |
| `tools/Convenciones.Evaluation/CorpusCoverage.cs` | draft/reviewed per category, multi-label, unclassified, ambiguous, disagreements |
| `tools/Convenciones.Evaluation/EvaluationCorpus.cs` | `ambiguous` flag on clause annotations |
| `tools/Convenciones.Evaluation/Program.cs` | `review-export`, `review-import`, `review-adjudicate` |
| `tests/Convenciones.UnitTests/Evaluation/ReviewWorkflowTests.cs` | **new** — 11 tests |
| `docs/quality/corpus/synthetic-v2-draft.dataset.json` | two clauses flagged ambiguous |
| `docs/quality/review/synthetic-v2-draft.review-A.json` / `.md` | **new** — reviewer A package |
| `docs/quality/review/synthetic-v2-draft.review-B.json` / `.md` | **new** — reviewer B package |
| `docs/quality/results/synthetic-v2-draft.coverage.md` | regenerated |
| `docs/quality/f06-corpus-and-annotation-protocol.md` | export/import/adjudication sections |

## 3. Review package

| Item | Value |
|---|---|
| Clauses per package | **36** |
| Reviewer slots | A and B, separate files |
| Draft categories in the export | **none** — verified: every `categories` list is empty |
| Reviewer identity in the export | **null** |
| Package status | `blank` |
| Human-readable copy | Markdown, one section per clause with blank answer lines |

Both exports carry identical content apart from `packageId`, `reviewerSlot` and the file name, so neither
reviewer can see the other's answers or the draft suggestions. The draft categories live only in the
corpus and reappear only in the adjudication worksheet, after both submissions exist.

## 4. Import workflow

`review-import --dataset <corpus> --submission <file> --out <file>` validates and then writes a
`submitted` artifact. It rejects:

| Condition | Message |
|---|---|
| anonymous submission | "reviewerIdentity is required" |
| corpus/version mismatch | "Submission targets … but the corpus is …" |
| unknown reviewer slot | "Unknown reviewer slot" |
| missing clause | "Clause '…' has no answer" |
| duplicate clause id | "Duplicate clause id" |
| unknown clause id | "Unknown clause id" |
| unsupported category | "uses unknown category" |
| primary outside selection | "primary category outside its selection" |
| existing output file | "Refusing to overwrite an existing import" |

**Import is not acceptance.** The corpus is not modified: a test asserts that after a successful import
every clause is still `draft` and the corpus status is still `draft`. Ground truth only changes through
adjudication.

## 5. Disagreement workflow

`review-adjudicate` merges both submissions into a worksheet. For each clause it keeps the draft, A's
answer and B's answer side by side, marks `agree`/`disagree`, and classifies the disagreement as
`categories`, `primary` or `categories+primary`. All resolution fields are emitted **empty**:
`resolution`, `rationale`, `adjudicator`, `finalCategories`, `finalPrimary`.

Nothing in the tooling can fill those fields. No AI is asked to resolve anything, and both original
answers remain in the worksheet permanently — adjudication **adds** a decision, it does not replace the
inputs.

## 6. Coverage improvements

The report now shows, per category: clauses, primary clauses, distinct documents, **draft** count,
**reviewed** count and a status (`UNCOVERED` / `draft only` / `reviewed`). Corpus-level totals add
multi-label, unclassified, ambiguous and unresolved-disagreement counts.

Current corpus:

| Metric | Value |
|---|---:|
| Documents | 7 |
| Clauses | 36 |
| Reviewed | **0** |
| Draft | 36 |
| Multi-label clauses | 2 |
| Unclassified clauses | 5 |
| Ambiguous clauses | 2 |
| Unresolved disagreements | 0 |
| Categories covered | 14 / 14 |

Coverage counts files. It says nothing about whether an annotation is correct, and 14/14 categories means
"every label appears at least once", **not** "each label is adequately represented". Two examples for a
category is not a statistical sample.

## 7. Tests and results

| Check | Result |
|---|---|
| `ReviewWorkflowTests` | **11 passed** |
| Full unit suite | **208 passed**, 0 failed |
| Integration suite | **249 passed**, 0 failed (1 m 36 s) |
| `dotnet build -c Release -warnaserror` | **0 warnings, 0 errors** |
| Exports verified blind | yes — no `categories` values in either file |

Covered: blind export contents, no cross-reviewer leakage, missing clause, duplicate/unknown clause,
unknown category, primary outside selection, anonymous submission, overwrite refusal, import leaving the
corpus untouched, adjudication preserving both answers and deciding nothing, adjudication rejecting an
incomplete submission.

## 8. Acceptance criteria

| # | Criterion | Status |
|---|---|---|
| 1 | Both reviewers can annotate independently | **met** — separate blind packages |
| 2 | Exports do not expose each other's answers | **met** — verified empty answers |
| 3 | Completed annotations import safely | **met** — validated, refuses overwrite |
| 4 | Original annotations remain recoverable | **met** — corpus untouched by import |
| 5 | Disagreements are preserved | **met** — both answers kept in the worksheet |
| 6 | Reviewed status requires the documented conditions | **met** — F-06.2 validator + reviewed fields |
| 7 | Coverage distinguishes presence from review depth | **met** — draft/reviewed per category |
| 8 | Tests cover export, import, validation, disagreement | **met** — 11 tests |
| 9 | Existing tests remain green | **met** |
| 10 | No external calls or environment changes | **met** — no external calls and no environment change by this task; the SDK pin is an intentional project decision (§9) |

## 9. SDK configuration

The repository is intentionally pinned to **.NET SDK 11.0.100** in `global.json`. This is a deliberate
project configuration decision made by the maintainer, not an incident and not an unexplained
modification. `global.json` is therefore left exactly as the maintainer set it.

All results in this report were produced with SDK 11.0.100 targeting `net10.0`; build and test are green
on that combination. No SDK installation, removal or configuration change was performed as part of
F-06.3.

## 10. Confirmations

| Item | Status |
|---|---|
| Human annotations generated | **none** — reviewers must fill the packages |
| Clauses marked reviewed | **none** |
| Expected categories modified | **none** |
| Taxonomy / prompts changed | **none** |
| External AI calls | **none** |
| Processing runs, quota, permissions | **none** |
| Staging / production | **none** — worker disabled |
| Deployments, commits, pushes, merges | **none** |
| `global.json` | **not touched** — intentionally pins SDK 11.0.100 (maintainer decision) |

## 11. Next step

Two people annotate `review-A.json` and `review-B.json` independently, without discussing clauses. Then:

```
review-import --dataset <corpus> --submission review-A.json --out review-A.submitted.json
review-import --dataset <corpus> --submission review-B.json --out review-B.submitted.json
review-adjudicate --dataset <corpus> --a review-A.submitted.json --b review-B.submitted.json \
                  --out-json adjudication.json --out-md adjudication.md
```

A human resolves every `disagree` row in the worksheet. Only after that does any clause become reviewed
ground truth — and only then can this corpus support a quality claim.
