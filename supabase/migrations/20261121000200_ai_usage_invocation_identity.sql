-- =====================================================================================
-- 20261121000200 — Durable invocation identity, USD enforcement, usage.read capability
--
-- P1-005 PR A.
--
-- 1. `invocation_no` was an execution ordinal, not an identity: a stage re-executed within the same
--    attempt restarted at 1 and its new billable call was silently discarded as a replay. Identity is
--    now a UUID generated once per logical provider invocation; the ordinal is kept for display only.
-- 2. Currency is fixed to USD at the database level until a multi-currency policy exists.
-- 3. Financial consumption gets its own read capability instead of reusing `clause.read`.
-- =====================================================================================

alter table platform.ai_usage_record add column invocation_id uuid;

-- Deterministic backfill: stable across retries, unique because the old constraint did not allow two
-- rows with the same (attempt, stage, ordinal).
update platform.ai_usage_record
set invocation_id = md5(attempt_id::text || ':' || stage || ':' || invocation_no)::uuid
where invocation_id is null;

alter table platform.ai_usage_record alter column invocation_id set not null;

create unique index ai_usage_record_invocation_id_key
    on platform.ai_usage_record (invocation_id);

-- The ordinal is no longer the identity; keeping this constraint would re-introduce the lost-charge
-- defect for a stage re-executed within one attempt.
alter table platform.ai_usage_record drop constraint ai_usage_record_unique_invocation;

alter table platform.ai_usage_record
    add constraint ai_usage_record_currency_usd check (currency = 'USD');

grant select on platform.ai_usage_record to app_api;
grant select on platform.ai_usage_record to authenticated;

insert into platform.capability (key, description)
select 'usage.read', 'Read company AI consumption and cost records'
where not exists (select 1 from platform.capability where key = 'usage.read');

insert into platform.role_capability (role_id, capability_id)
select r.id, c.id
from (values ('superadmin', 'usage.read'), ('administrator', 'usage.read')) as v(role_key, capability_key)
join platform.role r       on r.key = v.role_key
join platform.capability c on c.key = v.capability_key
where not exists (
    select 1 from platform.role_capability rc
    where rc.role_id = r.id and rc.capability_id = c.id
);

drop policy ai_usage_record_api_read on platform.ai_usage_record;
drop policy ai_usage_record_authenticated_read on platform.ai_usage_record;

create policy ai_usage_record_api_read on platform.ai_usage_record
    for select to app_api
    using (platform.is_active_user()
    and platform.can_access_company(company_id)
    and platform.has_capability('usage.read'));

create policy ai_usage_record_authenticated_read on platform.ai_usage_record
    for select to authenticated
    using (platform.is_active_user()
    and platform.can_access_company(company_id)
    and platform.has_capability('usage.read'));
