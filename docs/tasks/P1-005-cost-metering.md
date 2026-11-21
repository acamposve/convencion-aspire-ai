# P1-005 — AI cost metering and financial traceability

**Status:** metering and traceability implemented; **limit enforcement remains open (policy decision)**
**Finding:** F-05 · **Scope:** worker AI stages, usage persistence, pricing, migration

## 1. What was wrong

`platform.processing_usage` held one row per company and month with running totals. That is enough to
enforce a monthly quota and nothing else: a charge could not be attributed to a document, run, attempt,
model, deployment, stage or price list. Two further defects made even the totals wrong:

1. **Usage was recorded after the stage's persistence.** `ClauseExtractionStage` called
   `SaveClausesAsync` first and `RecordUsageAsync` afterwards. If saving failed — stale attempt, deleted
   document, lock timeout — the provider calls had already been billed and nothing was recorded. A later
   chunk failing inside the loop lost every token from the earlier chunks in the same way.
2. **Missing usage was coerced to zero.** `result.InputTokens ?? 0` meant an unreported count was
   persisted as "0 tokens, cost 0": an unknown charge presented as free.

Pricing arithmetic was duplicated in both stages, and no record carried the price list that produced the
amount.

## 2. What is implemented

### 2.1 Per-invocation persistence

New table `platform.ai_usage_record`, one row per provider invocation, written **immediately after the
call returns and before parsing or persistence** in both stages.

| Column | Meaning |
|---|---|
| `company_id`, `document_id`, `run_id`, `attempt_id` | attribution; selected from the run row, never supplied by the caller |
| `stage`, `invocation_no` | which call in which stage |
| `provider`, `model`, `deployment` | provider-reported identifiers (`azure-foundry` is the current client) |
| `input_tokens`, `output_tokens` | nullable; null means "not reported" |
| `usage_source` | `provider` or `unavailable` |
| `estimated_cost_usd`, `currency` | internal estimate, null when usage is unknown |
| `pricing_version`, `pricing_input_per_million`, `pricing_output_per_million` | the price list that produced the amount |
| `recorded_at` | timestamp |

### 2.2 Pricing

`AiPricing` (in `Convenciones.Ai`) is the single place where tokens become money. It returns `null` when
either count is missing, and carries `Version` and `Currency`. Both option classes gained
`PricingVersion` (default `configured-v1`). The hardcoded formula in the two stages is gone.

The prices themselves remain the configured values (`0.61` / `1.82` USD per million). **They are not
authoritative provider pricing** and were not invented here; they already existed in configuration. The
version string exists so a price change is a new version rather than an edit that silently rewrites
history.

### 2.3 Idempotency and consistency

- `unique (attempt_id, stage, invocation_no)` is the idempotency key.
- The detail row and the monthly aggregate are written in **one transaction**; the aggregate is only
  touched when the detail row was actually inserted, so a replay cannot double-count.
- A replay of the same invocation returns silently; an attempt that does not belong to the run raises
  `InvalidOperationException` and records nothing.
- A failed attempt keeps its rows: the record is written before the stage can fail, and nothing deletes
  it. A run that fails after a billable call therefore still shows what it cost.

## 3. What this does not do

**Budget enforcement.** The task requires a policy decision that does not exist: what the maximum
overspend is, whether budget is reserved before the call, and what happens to an in-flight run that
crosses the limit. The original finding also covers "quota absent means unlimited" for pages/tokens.
Neither is implemented here, because inventing a ceiling would be a financial policy choice, not a bug
fix. P1-005 therefore remains **partially addressed**: the data needed to enforce and reconcile now
exists and is trustworthy; the enforcement rule is open.

## 4. Files

| File | Change |
|---|---|
| `supabase/migrations/20261121000100_ai_usage_records.sql` | new table, constraints, unique key, RLS read policies |
| `src/Convenciones.Ai/AiPricing.cs` | new: price list + cost estimate, null when usage unknown |
| `src/Convenciones.Ai/AiCompletionResult.cs` | carries `Provider` (default `azure-foundry`) |
| `src/Convenciones.Ai/AzureFoundryAiModelClient.cs` | passes the provider explicitly |
| `src/Convenciones.Worker/Extraction/ExtractionOptions.cs`, `ClassificationPrompt.cs` | `PricingVersion` |
| `src/Convenciones.Worker/Extraction/IClauseStore.cs` | `RecordUsageAsync` → `RecordInvocationAsync` |
| `src/Convenciones.Worker/Extraction/NpgsqlClauseStore.cs` | per-invocation insert + aggregate, one transaction |
| `src/Convenciones.Worker/Extraction/ClauseExtractionStage.cs`, `ClauseClassificationStage.cs` | record each call before parsing; duplicated cost formula removed |
| `tests/Convenciones.UnitTests/Ai/AiPricingTests.cs` | 5 pricing cases |
| `tests/Convenciones.IntegrationTests/AiUsageRecordTests.cs` | 7 PostgreSQL cases |
| `tests/Convenciones.UnitTests/MigrationFileTests.cs` | pinned list includes the new migration |
| three unit-test fake stores | new interface member |

## 5. Results

| Check | Result |
|---|---|
| `build -c Release -warnaserror` | 0 warnings, 0 errors |
| Unit | **244 passed** |
| Integration | **277 passed** (270 before + 7 new) |

New integration coverage: full attribution and cost; unreported usage stored as `unavailable` with no
cost; replay does not duplicate the row or the aggregate; two invocations in one attempt both recorded
with pages counted once; pricing version and amount preserved per record; a failed run keeps its usage;
a foreign attempt is rejected and records nothing.

## 6. Limits

- A call that is billed but never returns (timeout, process crash before the response is read) leaves no
  record. Reconciling that requires provider-side billing data, which this system does not ingest.
- `provider` is the client's identifier (`azure-foundry`), not a provider-issued id.
- `processing_usage` remains the aggregate the quota uses; it stores `0` for unknown tokens, so it is
  usable for quota but never authoritative for cost. The detail table is.
- RLS read policies mirror `processing_usage`; writes are performed by the worker's privileged
  connection. No API endpoint exposes the records yet.
