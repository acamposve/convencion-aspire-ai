# P1-005 — AI cost metering and financial traceability

**Status:** implemented — metering, traceability, reservations, shadow accounting and opt-in
enforcement. **The operating policy is still open:** who creates budgets, the default limit and the
overspend rule are Product Owner decisions, so no environment enforces a ceiling today.
**Finding:** F-05 · **Scope:** worker AI stages, usage persistence, pricing, budget accounting,
migrations

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

### 2.4 Budgets, reservations and opt-in enforcement (PR B–D)

| Table | Purpose |
|---|---|
| `platform.ai_budget` | one row per company-period: `limit_amount`, `max_overspend`, `reserved_amount`, `spent_amount`, `enforcement` (`shadow` by default, `enforced`) |
| `platform.ai_budget_reservation` | one row per provider invocation, keyed by the durable invocation id; states `reserved` → `submitted` → `settled` / `settled_estimated` / `reconciled` / `released`, plus `outcome_unknown` |
| `platform.ai_budget_shadow` | for a period that is not enforced (no budget row, or `enforcement = 'shadow'`): the estimate and the would-admit decision are recorded, counters untouched |
| `platform.ai_budget_audit` | operator actions, with actor, amount and rationale |

- **A missing budget row is not unlimited.** It is *shadow* mode — as is a budget row that has not been
  activated: the cost is estimated and the admission decision recorded, but nothing is blocked and no
  counter moves.
- **Reserve before the call, settle after it.** `AiCostEstimator` bounds the cost before the request
  (characters/3 for input, `MaxOutputTokens` for output). The reservation is taken under the canonical
  company-period advisory lock (`platform.fin_lock_key`), the invocation is marked `submitted` before
  the HTTP call, and the ledger row plus the counter movement commit in one transaction. Over-reservation
  is released; unknown usage settles at the estimate and a later provider figure reconciles only the
  delta.
- **Enforcement is an explicit, audited cutover.** `ActivateAsync(actor, rationale)` converts submitted
  shadow rows into reservations already settled at their estimate and releases the rest; it refuses when
  unresolved `unavailable` usage exists for the period, because an unknown amount is not zero.
- **The sweep only runs while the worker is enabled.** It settles submitted reservations whose provider
  response never arrived at their estimate. Financial history is protected from retention: a document
  with ledger or reservation rows is never purged by ordinary processing retention.

## 3. What remains — the operating policy

The mechanism is built; the numbers and the authority are not. **Nothing is enforced in any environment
today**: budgets are created only by SQL, no environment is known to have one, and the activation path
(`ActivateAsync`) is invoked only by tests.

| Decision | Why it is not an engineering default |
|---|---|
| Who may create or change a budget, and through what (API, administration screen, SQL runbook) | There is no administration surface for `platform.ai_budget`; adding one is a capability and permission decision |
| The monthly limit and its period | A spend ceiling is a commercial decision about the customer, not a technical constant |
| `max_overspend` and the fate of an in-flight run that crosses the limit | "Stop mid-run" versus "finish and refuse the next" trades cost against a half-processed document |
| The alert threshold and who is told | Needs an owner and a channel |

Until those are decided, the system records what it would have blocked and the enforcement code stays
dormant. See
[ADR-029](../architecture/decisions.md#adr-029--ai-costs-are-metered-per-invocation-and-budgets-are-shadow-by-default-opt-in-enforced-p1-005)
and [pending owner sign-offs](../operations/owner-signoffs.md).

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
| `supabase/migrations/20261121000300_financial_budget.sql` | budgets, reservations, shadow rows, audit and the advisory-lock key |
| `supabase/migrations/20261121000400_financial_history_protection.sql` | ledger and reservation history protected from retention |
| `src/Convenciones.Worker/Financial/IFinancialBudget.cs` | reserve / mark submitted / settle contract |
| `src/Convenciones.Worker/Financial/FinancialBudgetStore.cs` | admission, settlement, reconciliation, shadow cutover and sweep |
| `src/Convenciones.Worker/Financial/FinancialSweeper.cs` | periodic settlement of responses that never arrived |
| `src/Convenciones.Worker/Financial/AiCostEstimator.cs` | pre-call bound: characters/3 for input, `MaxOutputTokens` for output |
| `tests/Convenciones.IntegrationTests/FinancialBudgetStoreTests.cs` | 33 PostgreSQL cases |
| `tests/Convenciones.UnitTests/MigrationFileTests.cs` | pinned list includes the new migrations |
| three unit-test fake stores | new interface member |

## 5. Results

| Check | Result |
|---|---|
| `build -c Release -warnaserror` | 0 warnings, 0 errors |
| Unit | **244 passed** |
| Integration | **277 passed** at PR A (270 before + 7 new); **303** at PR D after the budget cases |

New integration coverage at PR A: full attribution and cost; unreported usage stored as `unavailable`
with no cost; replay does not duplicate the row or the aggregate; two invocations in one attempt both
recorded with pages counted once; pricing version and amount preserved per record; a failed run keeps
its usage; a foreign attempt is rejected and records nothing.

Budget coverage (33 cases): admission in shadow and enforced modes; refusal without moving counters;
idempotent replay; settlement at actual cost and at the estimate; late reconciliation applies only the
delta; unknown outcomes are charged, never released on a timestamp; cutover converts submitted shadows
and releases unsubmitted ones; activation fails closed on unresolved `unavailable` usage; audit rows
carry actor, amount and rationale; the sweep settles only while the worker is enabled.

## 6. Limits

- A call that is billed but never returns (timeout, process crash before the response is read) leaves no
  ledger row. The reservation is settled at its estimate by the sweeper, so the hold is not lost, but the
  provider's actual figure is unknown; reconciling it requires provider-side billing data, which this
  system does not ingest.
- `provider` is the client's identifier (`azure-foundry`), not a provider-issued id.
- `processing_usage` remains the aggregate the quota uses; it stores `0` for unknown tokens, so it is
  usable for quota but never authoritative for cost. The detail table is.
- The page/token quota still has its own semantics; "quota absent means unlimited" for pages and tokens
  is unchanged by this task. Budget enforcement is per company-period and opt-in.
- No API endpoint exposes budgets, reservations or usage; `platform.ai_budget` is operated by SQL today.
- RLS read policies mirror `processing_usage`; writes are performed by the worker's privileged
  connection.
