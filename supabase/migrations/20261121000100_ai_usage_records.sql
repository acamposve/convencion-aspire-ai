-- =====================================================================================
-- 20261121000100 — AI usage records
--
-- P1-005. `platform.processing_usage` only holds a monthly total per company, so a charge cannot be
-- attributed to a document, run, attempt, model, stage or pricing version. This table records one row
-- per provider invocation so a billed call is never lost when the stage that made it later fails.
--
-- The row is written inside the same transaction as the invoice-relevant aggregate, and
-- (attempt_id, stage, invocation_no) is unique so a repeated insert cannot duplicate a charge.
-- =====================================================================================

create table platform.ai_usage_record (
    id                          uuid primary key default gen_random_uuid(),
    company_id                  uuid        not null references app.company (id) on delete restrict on update restrict,
    document_id                 uuid        not null references platform.document (id) on delete restrict on update restrict,
    run_id                      uuid        not null references platform.processing_run (id) on delete cascade on update restrict,
    attempt_id                  uuid        not null references platform.processing_attempt (id) on delete cascade on update restrict,
    stage                       text        not null,
    invocation_no               integer     not null,
    provider                    text        not null,
    model                       text,
    deployment                  text,
    input_tokens                bigint,
    output_tokens               bigint,

    -- 'provider'   → the provider reported usage for this call.
    -- 'unavailable' → the call was made but usage was not reported; no cost is asserted.
    usage_source                text        not null,

    -- Internally calculated, never presented as a provider-reported charge. Null when usage is unknown.
    estimated_cost_usd          numeric(12,6),
    currency                    char(3)     not null default 'USD',
    pricing_version             text        not null,
    pricing_input_per_million   numeric(12,6) not null,
    pricing_output_per_million  numeric(12,6) not null,
    recorded_at                 timestamptz not null default now(),

    constraint ai_usage_record_stage_not_blank     check (btrim(stage) <> ''),
    constraint ai_usage_record_invocation_positive check (invocation_no > 0),
    constraint ai_usage_record_source_valid        check (usage_source in ('provider', 'unavailable')),
    constraint ai_usage_record_tokens_non_negative
        check ((input_tokens  is null or input_tokens  >= 0)
           and (output_tokens is null or output_tokens >= 0)),
    constraint ai_usage_record_cost_non_negative
        check (estimated_cost_usd is null or estimated_cost_usd >= 0),
    constraint ai_usage_record_usage_consistent check (
        (usage_source = 'provider'
            and input_tokens is not null
            and output_tokens is not null)
        or (usage_source = 'unavailable'
            and input_tokens is null
            and output_tokens is null
            and estimated_cost_usd is null)),
    constraint ai_usage_record_unique_invocation unique (attempt_id, stage, invocation_no),
    constraint ai_usage_record_pricing_non_negative
        check (pricing_input_per_million >= 0 and pricing_output_per_million >= 0)
);

-- Monthly reporting by tenant; the unique key above already serves per-attempt lookups.
create index ai_usage_record_company_recorded_at
    on platform.ai_usage_record (company_id, recorded_at);

alter table platform.ai_usage_record enable row level security;

create policy ai_usage_record_api_read on platform.ai_usage_record
    for select to app_api
    using (platform.is_active_user()
    and platform.can_access_company(company_id)
    and platform.has_capability('clause.read'));

create policy ai_usage_record_authenticated_read on platform.ai_usage_record
    for select to authenticated
    using (platform.is_active_user()
    and platform.can_access_company(company_id)
    and platform.has_capability('clause.read'));
