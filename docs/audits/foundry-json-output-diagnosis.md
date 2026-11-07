# Foundry JSON output diagnosis — extraction contract

**Date:** 2026-11-07 · **Scope:** read-only diagnosis, no worker activated and no job executed during this audit.

## 1. Executive summary

**Classification: ROOT CAUSE PARTIALLY CONFIRMED.**

The failure is confirmed at the application boundary: Azure AI Foundry returned HTTP 200 twice, the
client's JSON Schema validation rejected both responses, and the worker persisted
`invalid_model_output`. The run did **not** fail on authentication, network, storage or Document
Intelligence.

The exact field/type mismatch is **not yet proven**: the failing run did not preserve the raw model
content or a detailed validation path. A bounded shape diagnostic was added in PRs #85/#86, but the
captured run in staging still shows the generic message. There is no basis to claim a specific field
difference without a new controlled synthetic run.

The confirmed contributory issues are:

1. The request uses JSON mode (`response_format: json_object`), not structured JSON Schema, for Mistral
   (`AzureAi:UseJsonSchema=false`).
2. Validation is strict (`additionalProperties: false`) and runs before the lenient parser.
3. The repair attempt receives only a generic error (`the JSON does not match the requested schema`)
   in the failing deployment, so the model has no actionable field to correct.
4. No `finish_reason`, content length, validation path or normalized shape is persisted with the run.

## 2. Evidence

### Staging evidence (already collected)

| Item | Value |
|---|---|
| Worker image | `ghcr.io/acamposve/convencion-aspire-ai/api:1c4b0808…` |
| Worker state | `Worker__Enabled=false`; healthy after shutdown |
| Failed run | `8523d480-10b6-4eca-989d-b91a624423d4` |
| Error code | `invalid_model_output` |
| Error message | `The model did not return JSON that satisfies the requested schema after one repair attempt` |
| Provider calls | Two Foundry `POST /models/chat/completions` returned **200** (initial + repair) |
| Document Intelligence | Analyze result polling returned **200** |
| Persisted clauses | None |
| Synthetic document | Only `tests/fixtures/synthetic-contract.pdf`; no real contract |

Other staging runs show the earlier connectivity/storage failures that were fixed:

- `62fd1b18…`, `b37dd32c…`, `582d1349…`, `a9fab029…`: `unexpected_error` before the storage fixes.
- `8523d480…`: `invalid_model_output` after storage was fixed.

The run ID above is the latest failed run captured in the smoke diagnostic. There is no raw response
body, no finish reason and no schema evaluation path in the persisted evidence.

### Code evidence

- `src/Convenciones.Ai/AzureFoundryAiModelClient.cs`
  - `BuildMessage`: when `UseJsonSchema=false`, sends `response_format = { "type": "json_object" }`.
  - `MatchesSchema`: validates with `Json.Schema` against `ExtractionSchema.Json`.
  - The old failure path threw a generic schema error; PRs #85/#86 add a bounded shape and validation
    detail, but the captured run predates or does not include that diagnostic output.
- `src/Convenciones.Worker/Extraction/ExtractionPrompt.cs`
  - Prompt requires JSON and tells the model not to invent, but gives no explicit field/type example.
- `ExtractionSchema.Json`:
  - Root requires `clauses`; each clause requires `text`, `page_from`, `page_to`, `confidence`;
    `number`/`heading` are nullable; `additionalProperties: false` at both levels.
- `ClauseResponseParser`:
  - Tolerates missing optional fields and defaults pages to the chunk, but validation rejects the
    content before this parser runs.
- `UseJsonSchema` default is **false**; the Mistral deployment is therefore not constrained by the
  schema at the provider.

### What is not known

- Whether Mistral returned a different field name (`page` vs `page_from`), wrong scalar types, extra
  properties, a wrapper object, markdown fences, or truncated JSON.
- Whether the provider honored `response_format: json_object` for this deployment.
- Whether `finish_reason` was `length` (truncation) or `stop`.
- Whether the repair response differed from the first response.

## 3. Alternatives

| Alternative | Advantages | Risks | Complexity | Architecture fit | Cost/latency | Recommendation |
|---|---|---|---|---|---|---|
| **A. Prompt-only correction** | No schema/model change; cheap | Model may still ignore exact shape; no guarantee | Low | Fits current client | Neutral | Necessary but insufficient alone |
| **B. Relax/adjust JSON Schema** | Accepts common field variations; low code | Weakens contract; may accept semantically poor output; parser must normalize | Medium | Fits | Neutral | Only for optional/format variants, not required fields |
| **C. Structured outputs (`json_schema`)** | Provider enforces schema; best structural guarantee | Mistral/Azure support must be verified; API may reject; tool/prompt compatibility | Medium | Set `UseJsonSchema=true`; feature flag/fallback | Slight prompt/token overhead | **Preferred first test** if supported |
| **D. Better repair feedback** | Actionable validation path lets model fix field/type; works with JSON mode | Adds one extra call; model may still fail | Low–medium | Fits existing one-repair design | One extra call only on failure | **Required regardless** |
| **E. Another Azure model** | May follow schema better | New bake-off, cost/latency, quality unknown; violates “don’t switch without evidence” | High | Requires evaluation harness | Unknown | Not now |
| **F. Tolerant parser / post-normalization** | Handles real-world model variation | Could hide semantic errors; must not bypass security validation | Medium | Parser can normalize optional fields, then validate | Neutral | Complementary, with strict required fields |

`UseJsonSchema=true` must not be assumed to work: the exact Mistral deployment/API version must be
probed with **one synthetic request** before relying on it.

## 4. Recommended solution

1. **Capture the real shape first** (one controlled synthetic request after explicit approval):
   - log only JSON shape, property names/types, `finish_reason`, content length and validation path;
   - never log contract text or values.
2. **Improve repair feedback**:
   - use detailed `Json.Schema` evaluation paths (instance location + keyword) in the repair prompt;
   - retain one repair, then permanent `invalid_model_output`.
3. **Probe structured outputs** with `UseJsonSchema=true` on the existing deployment:
   - if accepted and schema-valid, prefer it;
   - if unsupported, keep JSON mode and use the compact schema plus detailed repair.
4. **Harden the contract**:
   - include the exact schema/example in the system prompt;
   - require no markdown/commentary;
   - handle only safe optional/format variations in a normalization layer, never omit required
     `text`/page/confidence silently.
5. **Do not change model or cost** in this iteration.
6. **Validate quality, not just JSON**:
   - text fidelity against source, page ranges, clause boundaries, confidence calibration and
     hallucination checks; a schema-valid but wrong extraction is a failure.

## 5. Code changes needed (proposed, not implemented)

- `AzureFoundryAiModelClient`:
  - persist/emit `finish_reason`, content length and validation paths;
  - use detailed validation errors in repair;
  - support a feature flag for `json_schema`, with fallback;
  - consider extracting content shape metadata only.
- `ExtractionSchema` / prompt:
  - explicit field/type contract and a minimal example;
  - no markdown/extra prose.
- `ClauseResponseParser`:
  - normalize only known optional variants (e.g. page aliases) before strict required-field checks;
  - reject missing required data and out-of-range values.
- Tests:
  - fake HTTP responses for valid, optional-missing, required-missing, wrong types, malformed,
    empty, truncated, fenced/extra text, repair success/failure;
  - valid JSON but semantically wrong;
  - no Azure calls.

## 6. Test strategy

Local deterministic tests, no Azure consumption:

1. Valid JSON.
2. Missing optional `number`/`heading`.
3. Missing required `page_from`/`page_to`/`confidence`/`text`.
4. Wrong types (string page, string confidence, array root).
5. Malformed JSON.
6. Empty choices/content.
7. Truncated JSON / `finish_reason=length`.
8. Markdown fences and trailing prose.
9. Repair success and repair failure.
10. Valid schema but semantically incorrect: text not present in source, pages out of range,
    confidence out of range, invented clause.
11. `UseJsonSchema=true` body contains `json_schema`; `false` contains `json_object`.
12. No persistence of invalid content.

## 7. Residual risks

- The exact Mistral output behavior is still unproven until a controlled run.
- Structured outputs may be unsupported by the current deployment/API version.
- Repair adds latency and tokens; one repair must remain bounded.
- A schema-valid result can still be semantically wrong; quality gates need the golden set.
- Provider-side operations already accepted cannot be recalled by cancellation.
- No real contracts until terms approval.

## 8. Staging validation plan (only after approval)

1. Deploy diagnostic/repair change with `Worker__Enabled=false`.
2. Verify smoke and worker state.
3. Use the existing synthetic PDF only.
4. Authorize one short worker window; capture shape/validation evidence.
5. If structured outputs are supported, verify provider schema enforcement; otherwise apply detailed
   repair.
6. Require a `Completed` run with clauses whose text/pages/confidence are validated against the source.
7. Disable worker and verify `Worker__Enabled=false` and health.
8. Do not close #47 until quality and remaining acceptance criteria are met.

## 9. Decision

**ROOT CAUSE PARTIALLY CONFIRMED.**

The structural failure is confirmed (JSON-mode + strict validation + non-actionable repair), but the
exact model field mismatch is **INSUFFICIENT EVIDENCE** until the bounded diagnostics are captured in
a controlled synthetic run. No model or schema change should be made before that evidence exists.

## 10. Local implementation status (uncommitted)

Implemented in this local branch, without Azure calls:

- `AzureFoundryAiModelClient` now captures `finish_reason`, content length, bounded JSON shape
  (property names/types only) and JSON Schema instance/evaluation paths and failing keyword.
- Validation error messages are deliberately reduced to paths and keywords; schema error message
  values are not included, so contract values cannot leak.
- The repair request receives the same structural diagnostic, making the repair actionable.
- `UseJsonSchema=true` is technically wired and covered by a request-body test, but provider support
  for Mistral remains unverified without a controlled Azure call.
- Tests added: wrong type, missing required field, malformed/fenced JSON, truncated (`finish_reason=length`),
  empty content, no content echo, repair diagnostic, `json_schema` request body, and parser edge cases.
- Diagnostics redact unknown property names and never include schema error message values; only known\n  field names, JSON types, paths, keywords, content length and finish reason are emitted.\n- Local results: **113 unit tests passed; 170 integration tests passed**; no schema relaxation.

Still not available: the actual Mistral response shape from the failed staging run. The next synthetic
run should report `finishReason`, `contentLength`, `shape` and the failing `keyword`/instance path in
`processing_run.error_message`; no token or contract text is expected in that message.