# P1-006 — AI quality validation and pilot readiness

**Status:** Phase 1 (plan and existing-corpus verification) **complete**; Phase 2 onward is **blocked on
owner authorization and reviewer availability**. No provider was called and no real contract was used.
**Finding:** F-06 (production-v1 audit) · **Issues:** #52 (pilot and production preparation), #47
(security and acceptance review) · **Evidence:** [verification report](../quality/review/synthetic-v3-candidate.verification.md),
[candidate corpus](../quality/corpus/synthetic-v3-candidate.dataset.json).

## Objective

Turn "the pipeline works on synthetic samples" into "the pipeline meets agreed quality gates on a
representative, human-annotated corpus, under known cost and latency, with a measured decision to enable
it for real contracts".

## Scope

In:

- the composition and manifest of a **golden set** of 30–50 Spanish contracts stored **outside Git**;
- the acceptance gates and the measurement procedure that decide pass or fail;
- stop and rollback criteria for the pilot;
- the human-review package for the seven synthetic documents already in the repository.

Out: real contract data in the repository, provider calls in this phase, worker activation, production
approval. Those need the Product Owner decisions tracked in
[pending owner sign-offs](../operations/owner-signoffs.md).

## 1. Golden-set composition

**30–50 documents**, each at least two pages, Spanish, with the contract types and clause families the
product claims to support. Synthetic or explicitly authorized; never real customer data without written
authorization.

| Dimension | Minimum |
|---|---|
| Contract types | labour/collective agreement, commercial supply, services/SLA, confidentiality/NDA, licence — **≥ 3 documents each** |
| Clause families | every taxonomy category in use (13 today; `other` is pending a decision) **≥ 3 clauses each**, and **≥ 10** for any per-category claim |
| Difficulty cases | multi-label, no-category (working time), ambiguous-by-design, very long and one-line clauses, cross-references, missing/repeated headings, mid-clause page breaks |
| Total | **≥ 200 annotated clauses** |

The existing seven synthetic documents do **not** satisfy this: they are a workflow demonstration, not a
sample.

## 2. Storage and privacy

| Rule | Detail |
|---|---|
| Documents | `var/golden-set/<corpusId>/` (ignored by Git); the manifest and annotations are committed, the documents never are |
| Manifest | `docs/quality/golden-set/<corpusId>.manifest.json`, no personal data, hashes only |
| Authorization | `authorizationRef` recorded for every non-synthetic document; the written approval itself stays with the owner, not in Git |
| Anonymization | names, identifiers and signatures removed before intake; the intake checklist records what was redacted |
| Access | owner plus named reviewers; no cloud-sync folder; retention date in the manifest |
| Leak check | manifest re-hashed and the document set checked for PII before the first provider call |

`var/golden-set/` is added to `.gitignore` in this change so the rule is enforceable, not advisory.

## 3. Manifest design

```json
{
  "manifestVersion": 1,
  "corpusId": "golden-2027-01",
  "createdAtUtc": "2027-01-15T00:00:00Z",
  "authorizedBy": "<owner reference>",
  "authorizationRef": "<written approval id, stored with the owner>",
  "sourceType": "synthetic | authorized | anonymized",
  "languages": ["es-VE"],
  "storage": { "kind": "local-directory", "locator": "var/golden-set/golden-2027-01" },
  "versions": {
    "promptVersion": "extraction/v2",
    "classificationPromptVersion": "classification/v2",
    "taxonomyVersion": "20260109000100",
    "annotationProtocolVersion": "1"
  },
  "documents": [
    {
      "documentId": "g01",
      "sha256": "<sha256 of the PDF bytes>",
      "pages": 12,
      "contractType": "labour",
      "textSource": "pdf-text",
      "annotationFile": "g01.annotations.json",
      "annotationSha256": "<sha256>"
    }
  ],
  "reviewers": [{ "ref": "reviewer-1" }, { "ref": "reviewer-2" }],
  "retentionUntilUtc": "2027-12-31T00:00:00Z",
  "manifestSha256": "<sha256 over the canonical JSON without this field>"
}
```

| Field | Rule |
|---|---|
| `sha256` | of the document bytes; the scoring run re-hashes and refuses on mismatch |
| `annotationFile` | one file per document, same schema as the corpus, hashed in the manifest |
| `authorizationRef` | required unless `sourceType` is `synthetic` |
| `versions` | pinned before annotation; a change creates a new corpus version |
| `manifestSha256` | makes a silent manifest edit detectable |

The manifest is a record, not a lock: edits are allowed only with a version bump and a new hash.

## 4. Acceptance gates

Measured on the golden set, against the frozen versions. These are release gates for enabling the worker
on real contracts; they are **not** an SLA. The five thresholds are the ones proposed in
[AI evaluation](../operations/ai-evaluation.md) §5; this record turns them into a plan with a corpus,
a procedure and a decision.

| Gate | Threshold | Measured on |
|---|---|---|
| Boundary precision | **≥ 95%** | matched clauses (greedy LCS ≥ 0.8) |
| Boundary recall | **≥ 90%** | annotated clauses |
| Classification macro-F1 | **≥ 90%** | matched clauses, categories with support |
| Page attribution | **≥ 98%** | annotated clauses |
| Schema validity, first attempt | **100%** | provider invocations |

Secondary, reported but not gated by a single number until the owner sets the target:

| Metric | Target |
|---|---|
| Hallucinated clauses (text not in the source) | **0** after manual review |
| Duplicate clause ids | **0** |
| Cost per contract | measured; owner sets the ceiling |
| p95 run duration per 10-page contract | measured; owner sets the ceiling |
| 5xx / error rate under load | < 0.1% |

**Sample discipline.** Gates apply to point estimates; every rate is reported with a 95% Wilson interval
and the per-category support. If the lower bound is more than five points below a gate, the result is
**provisional**, not a pass, regardless of the point estimate. A category with fewer than 10 clauses is
reported but cannot support a category-level claim.

## 5. Measurement procedure

1. **Intake.** Complete the manifest, hash the documents, run the PII checklist, record authorization.
2. **Annotate.** Two independent reviewers per document, blind to model output and to each other; a
   third person adjudicates disagreements; `disagreement` stays in the draft until resolved.
3. **Freeze.** Set the corpus `datasetVersion`, `promptVersion`, `taxonomyVersion` and the deployed
   `commit`; no edits afterwards without a version bump.
4. **Capture predictions.** Run the pipeline against the frozen corpus with the worker enabled only in
   a scoped staging window (synthetic/authorized documents only), or capture output offline through the
   harness. The predictions file carries `runId`, model, deployment, prompt versions, commit.
5. **Score.** `evaluate` produces the result JSON and Markdown report under
   `docs/quality/results/<corpusId>-<date>/`; `coverage` proves the corpus shape.
6. **Review failures.** Sample and classify the misses: boundary, page, label, schema, hallucination.
7. **Decide.** Apply the gates; the Product Owner signs or rejects with reasons. Record the decision in
   [owner sign-offs](../operations/owner-signoffs.md).
8. **Re-run** when the prompt, model deployment, taxonomy or corpus changes — a new corpus version, not
   an edit.

## 6. Stop and rollback criteria

**Before the first provider call — do not start** if any of these is true: no written authorization for
a non-synthetic document; any personal data detected; provider terms unapproved; corpus not frozen; fewer
than 30 documents or 200 clauses; cost or latency ceiling not set.

**During the pilot — stop immediately** if any of these happens:

| Trigger | Action |
|---|---|
| Any gate below threshold | stop the batch, keep the artifact, do not widen the scope |
| Schema validity below 100% | stop; investigate the model or prompt before the next batch |
| Any hallucinated clause, duplicate id or empty success | stop; treat as a correctness defect |
| Cross-tenant read/write observed | stop; run the #47 isolation check before anything else |
| Cost per contract above the agreed ceiling | stop; re-forecast before continuing |
| p95 latency above the agreed ceiling | stop; do not call it a capacity problem only |
| Error rate above 0.5% | stop; diagnose before retrying |
| PII in a log, output or provider request | stop; purge and notify the owner |

Rollback mechanics: the worker is disabled with `worker-disable` (bounded window, watchdog, main `.env`
never flipped); the prompt or model version is reverted by configuration; AI output is purged through
`DELETE /documents/{id}/processing` and the retention sweep; the incident is recorded with its timestamps.
Migrations are forward-only — a bad release uses the documented binary rollback, and a schema change
needs its own forward migration.

## 7. Phases

| Phase | Work | State |
|---|---|---|
| 1 | Plan, gates, manifest design, verification of the existing corpus, candidate dataset | **done in this change** |
| 2 | Owner approvals (terms, data, corpus), intake, manifest | blocked |
| 3 | Two-person annotation, adjudication, corpus v3 reviewed | blocked on reviewers |
| 4 | Measured gate run and decision | blocked on Phase 3 |
| 5 | Pilot (#52), staging drill (#47), activation | blocked on Phase 4 |

## 8. Roles

| Role | Responsibility |
|---|---|
| Product Owner | authorization, provider terms, cost/latency ceilings, final activation decision |
| Reviewer A / B | independent annotation from the source, no model output |
| Adjudicator | resolves disagreements, records rationale |
| Operator | scoped worker window, stop/rollback, evidence capture |
| Engineering | harness, scoring, manifests, report |

## 9. Phase 1 evidence

| Artifact | Purpose |
|---|---|
| [Verification report](../quality/review/synthetic-v3-candidate.verification.md) | method, findings V-1…V-7, label proposals, limits |
| [Verification worksheet](../quality/review/synthetic-v3-candidate.verification.json) | one row per clause with `verdict`, `proposed`, `rationale`, empty human decision fields |
| [Candidate corpus](../quality/corpus/synthetic-v3-candidate.dataset.json) | draft v3, 7 documents, 36 clauses, provenance corrected |
| `docs/quality/corpus/synthetic-v2.dataset.json` | untouched, kept for reproducibility |

What the pass established: text, numbering, headings and page ranges are **36/36 correct** against the
sources; the published metrics reproduce exactly (extraction F1 1.0000, macro-F1 0.8792, micro-F1
0.8649); the committed review exports are blank and the adjudication was AI-only — so v2 is **not**
human-validated and cannot support a quality claim. Two labels are proposed for change and eight clauses
need a human ruling.

## 10. Risks

| Risk | Mitigation |
|---|---|
| Corpus authorization delays | start intake only when the owner confirms; keep the synthetic corpus for tooling tests |
| Reviewers unavailable or biased | two independent reviewers, adjudicator, disagreement recorded |
| Small sample with wide intervals | report Wilson intervals and per-category support; treat low-support gates as provisional |
| Provider change invalidates results | pin deployment and prompt versions in the manifest and predictions |
| Synthetic corpus is near-tautological | it validates tooling only; quality claims require the golden set |
| Personal data leaks into Git or a log | ignored storage path, PII checklist, no prompt/response logging |
| Cost overrun during measurement | estimate from the price list, cap the run, measure cost per contract before scaling |

## Left undone

Everything from Phase 2 onward: owner approvals, the golden-set documents, human annotation, the measured
gate, the pilot and the activation decision. This record intentionally does not claim any of them.
