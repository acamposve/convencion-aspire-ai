# F-06.1 — Evaluation framework hardening

**Date:** 2026-11-14 · **Mode:** local, offline · **Verdict: PASS**

The previous attempt was blocked when an external MSI transaction removed the .NET SDK mid-task. The
toolchain is restored, and every item on the checklist is now implemented and verified by execution.

---

## 1. Environment recovery

| Event | Detail |
|---|---|
| External installer | replaced the machine-wide SDK with **11.0.100** and removed the 10.x SDK |
| Consequence | `global.json` pins `10.0.401` with `rollForward: latestFeature`, which does **not** cross a major version — every build failed with "A compatible .NET SDK was not found" |
| Resolution | installed **10.0.404** into the user profile via `dotnet-install.ps1` and used that host explicitly |
| Why not edit `global.json` | that is a repository file outside this task; loosening the pin to accept SDK 11 would change the build contract for everyone |
| Docker | stopped by the reboot; restarted for the integration suite (Testcontainers needs a daemon) |

No repository file was changed to accommodate the environment.

## 2. Initial repository state

| Item | Value |
|---|---|
| Branch / HEAD | `fix/classification-prompt-contract` @ `cc96acb…` (already merged into `main` as `97a2cac`; the branch now only hosts F-06 work) |
| Tracked changes before this task | `deploy/Caddyfile` (pre-existing, unrelated), `tools/…/Metrics.cs`, `tools/…/Program.cs` |
| Untracked | audit reports, `admin.ps1` (not opened), `docs/quality/`, F-06 sources and tests |
| Unrelated changes | yes — `deploy/Caddyfile` and the audit reports belong to earlier tasks; untouched |

Nothing was staged, committed, pushed or discarded.

## 3. Files changed by this task

| File | Change |
|---|---|
| `src/Convenciones.Worker/Extraction/PromptCatalog.cs` | **new** — versions, content hashes |
| `src/Convenciones.Worker/Extraction/ExtractionOptions.cs` | default version from the catalog |
| `src/Convenciones.Worker/Extraction/ClassificationPrompt.cs` | default version from the catalog |
| `src/Convenciones.Worker/Convenciones.Worker.csproj` | `InternalsVisibleTo Convenciones.UnitTests` |
| `tools/Convenciones.Evaluation/EvaluationCorpus.cs` | `PredictionSet.ClassificationPromptVersion` |
| `tools/Convenciones.Evaluation/EvaluationEngine.cs` | result carries it |
| `tools/Convenciones.Evaluation/EvaluationReport.cs` | report prints it |
| `tests/Convenciones.UnitTests/Extraction/PromptCatalogTests.cs` | **new** — 8 tests, digests pinned |
| `tests/Convenciones.UnitTests/Evaluation/F06HardeningTests.cs` | **new** — 9 adversarial/metric-edge tests |
| `docs/quality/corpus/*.json` | prompt versions updated to v2, classification version added |
| `docs/quality/results/*` | regenerated with v2 provenance |
| `docs/quality/f06-evaluation-methodology.md` | stale "known gap" replaced |

## 4. Prompt-version design

| Property | Design |
|---|---|
| Versions | `extraction/v2`, `classification/v2` — PR #95 replaced both contracts while the constants still said `v1` |
| Independence | separate constants and separate digests; one cannot bump the other |
| Pinned digests | extraction `24c8b8a5c9566f68ebfa32d1803d07c34e799e429c1f930c4489e70db24fce4a`, classification `29e3d65fc9cab8b7b0bd45a9fbe79e627867deb7f9ae3e929f19ab92dee4c9d3`; both are SHA-256 over the **system prompt only** (the user prompt is per-document data) |
| Enforcement | `PromptCatalogTests` asserts the exact digest and the exact version string. Editing a prompt without bumping the version **fails the suite**, with a message telling the author to bump and re-pin |
| Wiring test | `new ExtractionOptions().PromptVersion` and `new ClassificationOptions().PromptVersion` must equal the catalog constants |
| Recorded where | per-run `processing_run.prompt_version`; per-evaluation `PredictionSet.PromptVersion` + `.ClassificationPromptVersion` |

Trade-off, stated plainly: pinning means two things to update when a prompt changes. That is the point —
it converts "remember to bump" into a build failure, which is the only form of this guarantee that
actually holds.

## 5. Matching algorithm — findings

Reviewed `BoundaryMetrics.Match`: greedy, annotation-ordered, one-to-one via a `used` array, ratio =
longest common substring **/ annotated length**, match when ratio ≥ 0.8 inclusive.

| Case | Finding | Test |
|---|---|---|
| Duplicate annotations | one-to-one enforced; both matched | `Duplicate_annotations_are_matched_one_to_one` |
| One prediction, two annotations | second annotation missing | `One_prediction_cannot_satisfy_two_annotations` |
| Reordered clauses | still matched (text-based) | `Reordered_clauses_still_match` |
| Threshold boundary | 8/10 = 0.8 matches; 7/10 does not | `Overlap_exactly_at_the_threshold_matches_and_just_below_does_not` |
| **Over-broad prediction** | a prediction containing the clause scores 1.0 and absorbs it, leaving the rest missing — **confirmed by test** | `An_over_broad_prediction_absorbs_the_first_clause…` |

**No correctness defect requiring a code change.** The over-broad case is a documented semantic of
annotation-normalised overlap, not a bug; the test now pins it, and any future change to the matcher
must change that test deliberately because it would move every historical score.

## 6. Metric audit — findings

### Classification

| Property | Verdict |
|---|---|
| Macro excludes unsupported categories | correct (support > 0 gate) |
| Micro | correct (pooled TP/FP/FN) |
| Exact-set accuracy | correct (`SetEquals`, denominator = evaluated) |
| Primary accuracy | correct (denominator = annotated primary; null predicted = wrong) |
| Unclassified-rate denominator | correct (evaluated clauses) — now pinned by test |
| False-positive accounting | correct |
| Empty dataset | rejected by the validator before scoring |
| Categories outside the reference taxonomy | counted as false positives, support 0, excluded from macro — pinned by test |
| Predictions missing entirely | all metrics 0, no division by zero — pinned by test |
| **No calculation defect found** | |

### Extraction

| Property | Verdict |
|---|---|
| matched / missing / extra | correct |
| precision / recall / F1 | correct, zero-guarded |
| number / heading / page / text fidelity | correct normalisation; null and empty compare equal |
| unmatched clauses in field accuracy | **impossible** — pinned by test: an extra clause with wrong number/heading/page does not change field denominators |
| **No calculation defect found** | |

## 7. Tests and results

| Check | Result |
|---|---|
| `PromptCatalogTests` | **8 passed** |
| `F06HardeningTests` (adversarial + edges) | **9 passed** |
| Full unit suite | **188 passed**, 0 failed |
| Integration suite | **249 passed**, 0 failed (1 m 45 s) |
| `dotnet build Convenciones.slnx -c Release -warnaserror` | **0 warnings, 0 errors** |

The integration suite initially failed 249/249 because Docker was down after the reboot; with the
daemon restarted, all pass. That failure was infrastructure, not code, and is reported rather than
hidden.

## 8. Sample evaluation (regenerated)

```
extraction: matched 4/4, P=1.0000 R=1.0000 F1=1.0000
classification: 4 clauses, macro-F1=1.0000 micro-F1=1.0000
```

The report now records `extraction/v2`, `classification/v2` and the deployed commit. The numbers remain
agreement with a 4-clause self-authored corpus — a demonstration that the scorer runs, not a quality
claim.

## 9. Remaining limitations

| Item | Classification |
|---|---|
| Greedy matching is not globally optimal | **DOCUMENTED** — deterministic and order-sensitive; a Hungarian assignment would change historical scores, so it is a deliberate choice, not an oversight |
| Over-broad predictions score 1.0 | **DOCUMENTED** — semantic of the ratio; pinned by test |
| Confidence calibration | **NOT IMPLEMENTED** — needs a large annotated sample |
| Character-offset boundary metrics | **NOT IMPLEMENTED** — corpus format has no field yet |
| Representative corpus | **BLOCKED** — needs human annotation, separate deliverable |
| Historical `extraction/v1` rows written under the v2 contract | **UNREPAIRABLE** without rewriting data (not authorized); disambiguated by `commit` |

## 10. Confirmations

| Item | Status |
|---|---|
| Deployment / staging / production | **none** |
| Processing runs, quota, permissions | **none** |
| Azure AI / Document Intelligence / Mistral calls | **none** |
| Worker | still disabled |
| Commits, pushes, merges, PRs | **none** |
| Prompt content changed | **none** — versions and wiring only |
| Taxonomy changed | **none** |
| Repository change made to fit the environment | **none** (SDK installed outside the repo) |
| `deploy/Caddyfile`, audit reports, `admin.ps1` | untouched |

## 11. Verdict

**PASS.** Prompt versions now identify the exact contract and a test enforces it; the matching algorithm
is characterised by adversarial tests with no defect requiring a change; classification and extraction
metrics were audited, found correct, and the previously untested edges are now covered; the full unit
and integration suites and the warnings-as-errors build are green.
