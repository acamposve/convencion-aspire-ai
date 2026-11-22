# Synthetic corpus verification — v2 to v3 candidate

**Date:** 2026-11-22 · **Scope:** the 7 synthetic documents and 36 clauses of
`synthetic-v2.dataset.json` · **Verifier:** an AI assistant (`assistant-verification-v1`), **not a human
reviewer** · **Status:** verification complete; human sign-off **pending**.

This is the preparation pass for the P1-006 golden set, not a quality evaluation. It calls no provider,
uploads no document, and does **not** make the corpus human-validated.

## 1. What was checked

| Layer | Method | Result |
|---|---|---|
| Text fidelity | Each clause's text compared with the HTML source that produced its PDF | **36/36 identical** after whitespace normalisation |
| Number and heading | Compared with the same source | **36/36**; `d06-c2` has no heading in either the source or the dataset |
| Page attribution | `pdf-pages` re-run against the PDFs | **36/36 located, 0 mismatches** |
| Schema and provenance | Validator via `coverage` | Valid schema 2; 7 documents, 36 clauses, 13/14 categories |
| Adjudication consistency | Worksheet vs published dataset | **0 differences**; 27/4/5 resolutions match the stated rule |
| Label review | Each current label read against its source text and the taxonomy definition | **26 confirmed, 8 need a ruling, 2 proposed changes** |
| Reproducibility | `evaluate` against `baseline-predictions-render-v2.json` | Extraction F1 **1.0000**; macro-F1 **0.8792**; micro-F1 **0.8649** — identical to the published baseline |

Commands actually run (offline, no provider):

```text
dotnet run --project tools/Convenciones.Evaluation -c Release -- \
  pdf-pages --dataset docs/quality/corpus/synthetic-v2.dataset.json \
  --pdf-dir docs/quality/corpus/pdf --out-json <temp>/pages-v2.json
  -> clauses: 36, mismatches: 0, not found: 0

dotnet run --project tools/Convenciones.Evaluation -c Release -- \
  coverage --dataset docs/quality/corpus/synthetic-v2.dataset.json
  -> synthetic-v2 v2: 7 documents, 36 clauses (36 reviewed, 0 draft), 13/14 categories covered

dotnet run --project tools/Convenciones.Evaluation -c Release -- \
  evaluate --dataset docs/quality/corpus/synthetic-v2.dataset.json \
  --predictions docs/quality/corpus/baseline-predictions-render-v2.json
  -> extraction: matched 36/36, P=1.0000 R=1.0000 F1=1.0000
  -> classification: 36 clauses, macro-F1=0.8792 micro-F1=0.8649
```

## 2. Findings

### V-1 — The committed review exports are blank *(high)*

`docs/quality/review/synthetic-v2-draft.review-A.json` and
`docs/quality/review/synthetic-v2-draft.review-B.json` are exports with `"status": "blank"` and **zero
answered clauses**. The A/B answers that the adjudication used exist only inside
`docs/quality/review/adjudication.json`; no `review-import` submission artifact supports them. The
adjudication's inputs therefore cannot be audited or replayed.

### V-2 — Both reviewers and the adjudicator are AI identities *(high)*

`reviewerAIdentity` and `reviewerBIdentity` are the same string (`ChatGPT — borrador asistido por IA (no
es revisión humana)`), and the adjudicator is `ai-adjudicator-v1`. The protocol states that no AI is
involved in adjudication and that a reviewer must be an independent person who has read the source
(F-06 protocol §3.3). Two answers from the same model are not independent evidence.

### V-3 — The corpus says "reviewed" although nobody reviewed it *(high)*

`synthetic-v2.dataset.json` has `"status": "reviewed"`, every clause `"status": "reviewed"`, and
`"reviewers": ["ai-adjudicator-v1"]`. The schema accepts any reviewer string, so tooling treats the
corpus as ground truth. The prose record (`F06-5-ai-adjudicated-corpus.md`) discloses that it is not
human-validated, but the machine-readable status contradicts it. That is the defect this pass corrects:
the new candidate is `status: draft`, with no reviewer and an explicit AI annotator.

### V-4 — `other` is never used: coverage is 13/14, not 14/14 *(medium)*

The taxonomy declares 14 categories, but no v2 clause uses `other`. The coverage tool reports
**13/14 categories covered**. The earlier 14/14 figure described the draft, where `other` appeared once
(`d06-c5`) before adjudication relabelled that clause as `scope`. The protocol also tells annotators to
leave the list empty when nothing fits, which makes `other` semantically redundant with empty.

### V-5 — The adjudication rationales are four templates *(medium)*

All 36 rows carry a rationale, but only four distinct strings exist: consensus at pages 1-1 (20),
consensus at pages 2-2 (7), unclassified working time (5), and "secondary labels from A and B" (4).
They record the resolution class, not clause-specific reasoning. The dataset's per-clause `notes` copy
these templates, so they look like justification without being one.

### V-6 — Two labels are proposed for change *(medium)*

Applying `duration` to the agreement's term only, and not to obligation-survival periods or renewal
opt-outs, yields two changes:

| Clause | Current | Proposed | Rationale |
|---|---|---|---|
| `d01-c5` | `duration`, `termination` | `duration` | "Denuncia" is the notice that prevents automatic renewal; the clause sets the term and renewal, not a cause or effect of termination. |
| `d03-c3` | `confidentiality`, `duration` | `confidentiality` | The clause fixes how long confidentiality survives; the agreement's term is not set here. |

Both are judgement calls. The human reviewer may keep the current labels; that is why the candidate is
a draft and the worksheet records a decision per clause rather than silently replacing v2.

### V-7 — Eight clauses need a human ruling *(medium)*

These are defensible under the taxonomy but not decidable by a second AI pass:

| Clause | Label | Open question |
|---|---|---|
| `d01-c3` | *(empty)* | Working time has no category. Keep empty, or add `working_conditions`? |
| `d07-c1` … `d07-c4` | *(empty)* | Same question for shift distribution, weekly rest, rotating shifts, overtime. |
| `d05-c2` | `ip`, `liability` | Both apply; is the primary `liability` (responsibility assumed) or `ip` (unauthorised use is the trigger)? |
| `d06-c3` | `scope` | It incorporates another clause by reference; reference-only is a difficulty case. `scope` or empty? |
| `d06-c5` | `scope` | A quarterly meeting obligation; `scope` (obligations) or `other` (procedural)? |

## 3. What changed

| Artifact | Change |
|---|---|
| `docs/quality/corpus/synthetic-v3-candidate.dataset.json` | New **draft** corpus (version 3, 7 documents, 36 clauses) derived from v2: same text, numbers, headings and pages; the two proposed changes applied; every clause `draft`, annotator `assistant-verification-v1`, no reviewer, per-clause verification note. |
| `docs/quality/review/synthetic-v3-candidate.verification.json` | Machine-readable worksheet: structural results, provenance findings, and one row per clause with `verdict`, `proposed`, `rationale` and empty `humanDecision` / `humanReviewer` / `humanDecidedAt`. |
| `docs/quality/review/synthetic-v3-candidate.verification.md` | This report. |

`synthetic-v2.dataset.json` is untouched: it stays for reproducibility, with its AI provenance on
record. The candidate is what a human reviews; it is not evidence until signed.

## 4. Taxonomy decisions needed before the corpus can be signed

1. **Working time.** Add a `working_conditions` category (recommended by the F-06 taxonomy assessment)
   or keep the five clauses unclassified. A new category requires a new taxonomy version and re-annotation.
2. **`other`.** Remove it, or define the difference between `other` and an empty list. Today both exist
   and the protocol prefers empty.
3. **`duration`.** State whether it means the agreement's term only (this verification's reading) or any
   stated period, including obligation survival.
4. **Primary label.** State a tie-break rule for multi-label clauses whose dominant category is arguable
   (`d05-c2`).

These are product decisions for the taxonomy owner, not verification outcomes.

## 5. Human sign-off procedure

1. Read the source PDF in `docs/quality/corpus/pdf/` for each document; the HTML beside it is the source
   used to render the PDF.
2. Open `synthetic-v3-candidate.verification.json` and, for every clause, set `humanDecision` to
   `accept`, `accept-proposal`, `reject` or `defer`, and fill `humanReviewer` and `humanDecidedAt`.
3. For a `reject` or `defer`, add a `humanRationale` field to the row with the reason.
4. Do not edit `synthetic-v2.dataset.json`. A signed corpus is a new dataset version published through
   the protocol of `f06-corpus-and-annotation-protocol.md` §3.4.
5. Record the decision in `docs/operations/owner-signoffs.md` (item 2) and in the P1-006 record.

The candidate validates today:

```text
dotnet run --project tools/Convenciones.Evaluation -c Release -- \
  coverage --dataset docs/quality/corpus/synthetic-v3-candidate.dataset.json
  -> synthetic-v3-candidate v3: 7 documents, 36 clauses (0 reviewed, 36 draft), 13/14 categories covered
```

## 6. Limits — read before citing anything here

1. **The verifier is an AI.** Its structural checks are mechanical and reproducible; its label review is
   a second opinion, not human judgement.
2. **Seven synthetic documents are not a sample.** They prove the workflow, not the product's accuracy.
3. **The reproduced metrics are agreement with the annotations**, not accuracy against reality. Macro-F1
   0.8792 is measured against labels this report just showed are not human-validated.
4. **No provider was called**, so nothing here measures latency, cost or real-contract behaviour.
5. **Text, number, heading and page checks are objective** and can be re-run by anyone with the commands
   above; the label verdicts are proposals.
