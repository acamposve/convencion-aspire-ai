# F-06 — Evaluation corpus and annotation protocol

**Status:** schema v2 implemented and validated; corpus is **draft only** (7 documents, 36 clauses,
**0 reviewed**). No quality claim may be made from it.

## 1. What the corpus must contain

The current corpus proves the schema and the workflow. A *quality* corpus must cover the contract types
the product claims to support, the clause families in the taxonomy, and the cases where models fail.

### Contract types (target: ≥ 3 documents each)

| Type | Notes |
|---|---|
| Employment / collective labour agreement | the product's primary domain |
| Commercial agreement | pricing, delivery, liability |
| Service agreement | SLAs, scope, termination |
| Supplier agreement | warranties, indemnities |
| Confidentiality / NDA | confidentiality, data protection, duration |
| Other supported types | e.g. licensing instruments |

### Clause families (must all appear, ≥ 3 examples each)

`parties` · `scope` · `pricing` · `duration` · `termination` · `confidentiality` ·
`data_protection` · `ip` · `liability` · `warranties` · `disputes` · `governing_law` ·
`force_majeure` · `other`

### Difficulty cases (deliberately represented, not avoided)

- clauses that legitimately belong to **several** categories;
- clauses that **fit no category** (working time under taxonomy v1);
- ambiguous clauses two careful humans would label differently;
- very long clauses (multi-paragraph) and very short ones (one line);
- clauses that only **reference** another section;
- missing or malformed headings, repeated numbering;
- formatting irregularities: page breaks mid-clause, absent headings, duplicate numbers.

### Source restrictions

Synthetic documents, or documents with documented authorization. **No** real customer contracts,
personal data or confidential material without explicit written authorization.

## 2. File formats

### 2.1 Corpus (`*.dataset.json`, schema 2)

```json
{
  "schemaVersion": 2,
  "datasetVersion": "1",
  "corpusId": "synthetic-v2-draft",
  "status": "draft",
  "promptVersion": "extraction/v2",
  "taxonomyVersion": "20260109000100",
  "taxonomyKeys": ["parties", "scope", "…"],
  "annotators": ["assistant-draft"],
  "reviewers": [],
  "documents": [
    {
      "documentId": "synthetic-d01-labour-construction",
      "title": "…",
      "contractType": "labour",
      "clauses": [
        {
          "clauseId": "d01-c1",
          "number": "CLAUSULA PRIMERA",
          "heading": "PARTES",
          "text": "…",
          "pageFrom": 1,
          "pageTo": 1,
          "categories": ["parties"],
          "primary": "parties",
          "status": "draft",
          "annotator": "assistant-draft",
          "notes": "…"
        }
      ]
    }
  ]
}
```

| Field | Rule |
|---|---|
| `schemaVersion` | `1` = legacy demo shape (no provenance); `2` = this workflow, validated strictly |
| `datasetVersion` | bumped on **any** annotation change; predictions must match or scoring is refused |
| `status` | `draft` or `reviewed`; `reviewed` requires every clause reviewed |
| `promptVersion` / `taxonomyVersion` | the versions the annotations target |
| `taxonomyKeys` | the closed set of allowed categories; every category must be in it |
| `annotators` / `reviewers` | named or pseudonymous references; never credentials |
| `documentId` / `clauseId` | unique, stable |
| `contractType` | free text used by the coverage report |
| `text` | required, non-empty |
| `pageFrom` / `pageTo` | 1-based, `pageTo >= pageFrom` |
| `categories` | may be empty (unclassifiable), otherwise ⊆ `taxonomyKeys` |
| `primary` | `null`, or one of `categories` |
| `status` (clause) | `draft` means **not yet independently reviewed** |
| `annotator` | required — its absence is a missing annotation |
| `reviewer` | required once the clause is `reviewed` |
| `notes` | free text for ambiguity, cross-references, taxonomy gaps |
| `disagreement` | records an **unresolved** reviewer disagreement; forbids `reviewed` |

### 2.2 Predictions (`*.predictions.json`)

Unchanged from F-06: `runId`, `model`, `datasetVersion`, `promptVersion`, optional
`classificationPromptVersion` and `commit`, then documents and clauses. `runId` is mandatory.

## 3. Annotation protocol

1. **Source intake.** Synthetic or authorized document only; origin recorded outside the JSON.
2. **Segmentation first, labels second.** Mark boundaries, number, heading and page range before
   looking at model output. Reading predictions first biases the boundaries.
3. **Label against the taxonomy of record.** If nothing fits, leave `categories` empty and record the
   candidate concept in `notes`. Never invent a key: the validator rejects it.
4. **Multi-label is expected.** Two genuine obligations get two categories; `primary` is the dominant
   one and must be listed in `categories`.
5. **Draft, then review.** The author writes the clause with `"status": "draft"`. Only an independent
   reviewer who has read the source may set `"status": "reviewed"` and name themselves in `reviewer`.
6. **Never generate ground truth with the model under test.** Model output may help find candidate
   clauses; it may never decide the label.
7. **Freeze.** Bump `datasetVersion`; never edit a released version in place.

### 3.1 Review workflow

```
draft  ──(second person reads source + annotation)──►  reviewed
  │                                                        │
  └── disagreement recorded ──► stays draft ──► adjudicated ─┘
```

| Situation | Action |
|---|---|
| Reviewer agrees | set `status: reviewed`, `reviewer: "<ref>"` |
| Reviewer disagrees | keep `status: draft`, set `disagreement` with the alternative, and adjudicate |
| Adjudicated | agree the final label, clear `disagreement`, set `reviewed` + `reviewer` |
| Corpus fully reviewed | set corpus `status: reviewed` and list `reviewers`; the validator enforces that no draft clause remains |
| Correction after release | new `datasetVersion`; the old file stays for reproducibility |

`disagreement` is deliberately a free-text field rather than a second label set: preserving the
reasoning matters more than preserving a machine-readable vote count, and inter-annotator agreement
must not be computed unless genuinely independent annotations exist.

### 3.2 Review exports and import

Two blind copies are generated from the draft corpus:

```
dotnet run --project tools/Convenciones.Evaluation -- review-export \
  --dataset docs/quality/corpus/synthetic-v2-draft.dataset.json --slot A \
  --out-json docs/quality/review/synthetic-v2-draft.review-A.json \
  --out-md  docs/quality/review/synthetic-v2-draft.review-A.md
```

The export contains source facts (text, number, heading, page range) and **empty** category answers.
Draft categories are deliberately withheld: showing them would anchor the reviewer on the answer the
round exists to test. Reviewer A and Reviewer B receive identical files apart from the package id and
slot, so neither can see the other's work.

A completed file is imported with `review-import`. Non-technical reviewers do not need to edit JSON:
`review-template` generates `review-A-template.xlsx` / `review-B-template.xlsx`, and `review-excel`
converts a completed workbook into exactly the same submission JSON (see
`docs/quality/review/INSTRUCCIONES-revisores.md`). The converter only adds row-addressed messages; it
delegates every semantic rule to the same validation `review-import` uses. It is rejected when it has:

| Problem | Why |
|---|---|
| no `reviewerIdentity` | an anonymous review cannot be adjudicated |
| a corpus/version mismatch | the answers would belong to a different document set |
| a missing clause | partial reviews are not evidence |
| a duplicate clause id | ambiguous target |
| a category outside `taxonomyKeys` | unsupported label |
| a `primary` outside the selected categories | contradictory answer |
| an existing target file | **imports never overwrite**; use a new package id |

An import writes a `submitted` artifact beside the corpus and **never touches the corpus itself**. Being
imported is not being accepted: acceptance happens only through adjudication.

### 3.3 Adjudication

```
review-adjudicate --dataset <corpus> --a <submission-A> --b <submission-B> \
                  --out-json <worksheet.json> --out-md <worksheet.md>
```

The worksheet lists every clause with the draft, Reviewer A and Reviewer B answers, marks agreements and
classifies disagreements as `categories`, `primary` or `categories+primary`. Every resolution field —
`resolution`, `rationale`, `adjudicator`, `finalCategories`, `finalPrimary` — is left **empty**. The tool
cannot resolve anything, and no AI is involved in adjudication.

| Disagreement | Handling |
|---|---|
| categories differ | human decides the final set; rationale recorded |
| primary differs but set matches | human decides the dominant category |
| both differ | one decision covers both |
| reviewer marks `ambiguous` | the flag is preserved in the worksheet and in the final annotation |

Both original answers stay in the worksheet permanently; adjudication adds a decision, it does not
overwrite the inputs.

### 3.4 Publishing corpus v2

The worksheet is a draft of decisions, not ground truth. A human fills `resolution`, `rationale`,
`adjudicator`, `finalCategories`, `finalPrimary` — and `finalPageFrom`/`finalPageTo` when the reviewers
reported different page ranges — then the corpus is published:

```
corpus-materialize --dataset <corpus-v1.json> --adjudication <finalized.json> \
                   --out <corpus-v2.json> --version 2
```

| Rule | Behaviour |
|---|---|
| Anonymous clause | refused: every clause needs an `adjudicator` |
| Unresolved category disagreement | refused until `finalCategories` is set |
| Unresolved page disagreement | refused until `finalPageFrom`/`finalPageTo` is set |
| Final primary | must be one of the final categories |
| Version | must differ from the source; publishing v1 over v1 is refused |
| Output | `status: reviewed`, every clause `reviewed`, schema 2, passed through the same validator as any hand-written corpus |

The command never edits the source corpus and never resolves a disagreement itself.

## 4. Versioning

| Change | Action |
|---|---|
| annotation text/label/page, or a document added | `datasetVersion` +1 |
| taxonomy changes | new `taxonomyVersion`; re-annotate into a new dataset version |
| prompt contract changes | new `promptVersion` (F-06.1 pins it with a hash) |
| new model/deployment | new prediction file; corpus unchanged |

## 5. Current state

| Item | Value |
|---|---|
| Corpus | `docs/quality/corpus/synthetic-v2-draft.dataset.json` |
| Schema | 2 |
| Documents / clauses | **7 / 36** |
| Corpus status | **draft** |
| Reviewed clauses | **0** |
| Categories covered | **14 of 14** (see coverage report) |
| Contract types | labour (2), commercial, confidentiality, service, supplier, mixed |
| Predictions | none for this corpus — it has never been run through the pipeline |
| Representative? | **No.** Seven documents is a workflow demonstration, not a sample |

Expanding to the target composition, and reviewing what exists, requires **people**: that work is
**BLOCKED** on reviewer availability, not on tooling.
