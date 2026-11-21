-- =====================================================================================
-- 20261121000300 — Financial budget and reservation model (P1-005 PR B)
--
-- Reservation-based budget accounting for provider invocations. Everything that can change a
-- company-period's financial state takes the same advisory lock, so the lock key lives in one place:
-- platform.fin_lock_key(company_id, period). Lock order: advisory -> budget row -> reservation -> usage.
--
-- A company-period with no ai_budget row is *not* unlimited: it runs in shadow mode (cost estimated and
-- recorded for comparison, counters untouched). Enforcement begins only when a budget row exists with
-- enforcement='enforced'.
-- =====================================================================================

create function platform.fin_lock_key(p_company uuid, p_period date)
returns bigint
language sql
immutable
parallel safe
as $$ select hashtextextended(p_company::text || ':fin:' || p_period::text, 0) $$;

comment on function platform.fin_lock_key(uuid, date) is
    'Canonical advisory-lock key for financial operations of one company and period. Every financial writer must use it.';

create table platform.ai_budget (
    id               uuid primary key default gen_random_uuid(),
    company_id       uuid        not null references app.company (id) on delete restrict on update restrict,
    period_start     date        not null,
    period_end       date        not null,
    currency         char(3)     not null default 'USD',
    limit_amount     numeric(12,6) not null,
    max_overspend    numeric(12,6) not null default 0,
    reserved_amount  numeric(12,6) not null default 0,
    spent_amount     numeric(12,6) not null default 0,
    enforcement      text        not null default 'shadow',
    version          integer     not null default 1,
    created_at       timestamptz not null default now(),
    updated_at       timestamptz not null default now(),

    constraint ai_budget_unique_period   unique (company_id, period_start, currency),
    constraint ai_budget_period_valid    check (period_end >= period_start),
    constraint ai_budget_limit_valid     check (limit_amount >= 0),
    constraint ai_budget_overspend_valid check (max_overspend >= 0),
    constraint ai_budget_reserved_valid  check (reserved_amount >= 0),
    constraint ai_budget_spent_valid     check (spent_amount >= 0),
    constraint ai_budget_enforcement_valid check (enforcement in ('shadow', 'enforced')),
    constraint ai_budget_currency_usd    check (currency = 'USD')
);

create trigger ai_budget_set_updated_at
    before update on platform.ai_budget
    for each row execute function platform.set_updated_at();

create table platform.ai_budget_reservation (
    invocation_id   uuid primary key,
    budget_id       uuid        not null references platform.ai_budget (id) on delete restrict on update restrict,
    company_id      uuid        not null references app.company (id) on delete restrict on update restrict,
    run_id          uuid        not null references platform.processing_run (id) on delete restrict on update restrict,
    attempt_id      uuid        not null references platform.processing_attempt (id) on delete restrict on update restrict,
    stage           text        not null,
    estimated_cost  numeric(12,6) not null,
    settled_amount  numeric(12,6),
    currency        char(3)     not null default 'USD',
    status          text        not null default 'reserved',
    origin          text        not null default 'reservation',
    submitted_at    timestamptz,
    expires_at      timestamptz not null,
    settled_at      timestamptz,
    reconciled_at   timestamptz,
    released_at     timestamptz,
    created_at      timestamptz not null default now(),
    updated_at      timestamptz not null default now(),

    constraint ai_budget_reservation_stage_not_blank check (btrim(stage) <> ''),
    constraint ai_budget_reservation_estimate_valid  check (estimated_cost >= 0),
    constraint ai_budget_reservation_settled_valid   check (settled_amount is null or settled_amount >= 0),
    constraint ai_budget_reservation_currency_usd    check (currency = 'USD'),
    constraint ai_budget_reservation_status_valid
        check (status in ('reserved', 'submitted', 'outcome_unknown', 'settled', 'settled_estimated', 'reconciled', 'released')),
    constraint ai_budget_reservation_origin_valid
        check (origin in ('reservation', 'shadow_cutover')),
    constraint ai_budget_reservation_settled_has_amount
        check (status not in ('settled', 'settled_estimated', 'reconciled') or settled_amount is not null),
    constraint ai_budget_reservation_submitted_consistent
        check (status not in ('submitted', 'outcome_unknown') or submitted_at is not null)
);

create index ai_budget_reservation_budget_status
    on platform.ai_budget_reservation (budget_id, status);

create table platform.ai_budget_shadow (
    invocation_id   uuid primary key,
    company_id      uuid        not null references app.company (id) on delete restrict on update restrict,
    run_id          uuid        not null references platform.processing_run (id) on delete restrict on update restrict,
    attempt_id      uuid        not null references platform.processing_attempt (id) on delete restrict on update restrict,
    stage           text        not null,
    period          date        not null,
    estimated_cost  numeric(12,6) not null,
    currency        char(3)     not null default 'USD',
    would_admit     boolean     not null,
    status          text        not null default 'shadowed',
    submitted_at    timestamptz,
    converted_at    timestamptz,
    created_at      timestamptz not null default now(),

    constraint ai_budget_shadow_estimate_valid check (estimated_cost >= 0),
    constraint ai_budget_shadow_currency_usd   check (currency = 'USD'),
    constraint ai_budget_shadow_status_valid   check (status in ('shadowed', 'converted', 'released'))
);

create index ai_budget_shadow_company_period
    on platform.ai_budget_shadow (company_id, period);

create table platform.ai_budget_audit (
    id            uuid primary key default gen_random_uuid(),
    company_id    uuid        not null references app.company (id) on delete restrict on update restrict,
    period_start  date        not null,
    actor         text        not null,
    action        text        not null,
    amount        numeric(12,6),
    details       jsonb       not null default '{}'::jsonb,
    created_at    timestamptz not null default now(),

    constraint ai_budget_audit_actor_not_blank  check (btrim(actor) <> ''),
    constraint ai_budget_audit_action_not_blank check (btrim(action) <> '')
);

alter table platform.ai_budget             enable row level security;
alter table platform.ai_budget_reservation enable row level security;
alter table platform.ai_budget_shadow      enable row level security;
alter table platform.ai_budget_audit       enable row level security;

grant select on platform.ai_budget, platform.ai_budget_reservation, platform.ai_budget_shadow,
    platform.ai_budget_audit to app_api;
grant select on platform.ai_budget, platform.ai_budget_reservation, platform.ai_budget_shadow,
    platform.ai_budget_audit to authenticated;

create policy ai_budget_api_read on platform.ai_budget
    for select to app_api
    using (platform.is_active_user() and platform.can_access_company(company_id)
       and platform.has_capability('usage.read'));

create policy ai_budget_authenticated_read on platform.ai_budget
    for select to authenticated
    using (platform.is_active_user() and platform.can_access_company(company_id)
       and platform.has_capability('usage.read'));

create policy ai_budget_reservation_api_read on platform.ai_budget_reservation
    for select to app_api
    using (platform.is_active_user() and platform.can_access_company(company_id)
       and platform.has_capability('usage.read'));

create policy ai_budget_reservation_authenticated_read on platform.ai_budget_reservation
    for select to authenticated
    using (platform.is_active_user() and platform.can_access_company(company_id)
       and platform.has_capability('usage.read'));

create policy ai_budget_shadow_api_read on platform.ai_budget_shadow
    for select to app_api
    using (platform.is_active_user() and platform.can_access_company(company_id)
       and platform.has_capability('usage.read'));

create policy ai_budget_shadow_authenticated_read on platform.ai_budget_shadow
    for select to authenticated
    using (platform.is_active_user() and platform.can_access_company(company_id)
       and platform.has_capability('usage.read'));

create policy ai_budget_audit_api_read on platform.ai_budget_audit
    for select to app_api
    using (platform.is_active_user() and platform.can_access_company(company_id)
       and platform.has_capability('usage.read'));

create policy ai_budget_audit_authenticated_read on platform.ai_budget_audit
    for select to authenticated
    using (platform.is_active_user() and platform.can_access_company(company_id)
       and platform.has_capability('usage.read'));

-- Financial period of a usage row: the period of the reservation that authorised the call. Nullable for
-- rows written before the reservation model existed; the settlement path populates it.
alter table platform.ai_usage_record add column period date;
