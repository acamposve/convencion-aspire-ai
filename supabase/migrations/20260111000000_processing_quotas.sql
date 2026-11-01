-- =====================================================================================
-- 20260111000000 — processing quotas and monthly usage
--
-- One quota row per company (nullable limit = unlimited) and one usage row per company/month.
-- The API reserves a document atomically when a run starts; the worker adds pages and tokens when
-- the stage finishes. Tenant data, RLS on every table.
-- =====================================================================================

create table platform.company_quota (
    company_id       uuid primary key references app.company (id) on delete cascade on update restrict,
    documents_limit  integer,
    pages_limit      integer,
    tokens_limit     bigint,
    updated_at       timestamptz not null default now(),

    constraint company_quota_documents_non_negative check (documents_limit is null or documents_limit >= 0),
    constraint company_quota_pages_non_negative     check (pages_limit is null or pages_limit >= 0),
    constraint company_quota_tokens_non_negative    check (tokens_limit is null or tokens_limit >= 0)
);

create trigger company_quota_set_updated_at
    before update on platform.company_quota
    for each row execute function platform.set_updated_at();

create table platform.processing_usage (
    company_id         uuid        not null references app.company (id) on delete cascade on update restrict,
    period             date        not null,
    documents          integer     not null default 0,
    pages              integer     not null default 0,
    input_tokens       bigint      not null default 0,
    output_tokens      bigint      not null default 0,
    cost_estimate_usd  numeric(12,6) not null default 0,
    updated_at         timestamptz not null default now(),

    primary key (company_id, period),
    constraint processing_usage_documents_non_negative check (documents >= 0),
    constraint processing_usage_pages_non_negative     check (pages >= 0),
    constraint processing_usage_input_non_negative     check (input_tokens >= 0),
    constraint processing_usage_output_non_negative    check (output_tokens >= 0),
    constraint processing_usage_cost_non_negative      check (cost_estimate_usd >= 0)
);

create trigger processing_usage_set_updated_at
    before update on platform.processing_usage
    for each row execute function platform.set_updated_at();

alter table platform.company_quota    enable row level security;
alter table platform.processing_usage enable row level security;

create policy company_quota_api_read on platform.company_quota
    for select to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy company_quota_authenticated_read on platform.company_quota
    for select to authenticated
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy company_quota_api_write on platform.company_quota
    for all to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('quota.manage'))
    with check (platform.is_active_user()
        and platform.can_access_company(company_id)
        and platform.has_capability('quota.manage'));

create policy processing_usage_api_read on platform.processing_usage
    for select to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy processing_usage_authenticated_read on platform.processing_usage
    for select to authenticated
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy processing_usage_api_write on platform.processing_usage
    for all to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.process'))
    with check (platform.is_active_user()
        and platform.can_access_company(company_id)
        and platform.has_capability('clause.process'));

grant select, insert, update, delete on platform.company_quota, platform.processing_usage to app_api;
grant select on platform.company_quota, platform.processing_usage to authenticated;

insert into platform.capability (key, description)
select 'quota.manage', 'Configure company processing quotas'
where not exists (select 1 from platform.capability where key = 'quota.manage');

insert into platform.role_capability (role_id, capability_id)
select r.id, c.id
from (values ('superadmin', 'quota.manage'), ('administrator', 'quota.manage')) as v(role_key, capability_key)
join platform.role r       on r.key = v.role_key
join platform.capability c on c.key = v.capability_key
where not exists (
    select 1 from platform.role_capability rc
    where rc.role_id = r.id and rc.capability_id = c.id
);
