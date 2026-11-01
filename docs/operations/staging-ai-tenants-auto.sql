-- =====================================================================================
-- Staging-only: two isolated tenants for the AI exercise (#47/#52)
--
-- BEFORE RUNNING: replace __USER_A_UUID__ and __USER_B_UUID__ with the Supabase Auth user UIDs
-- (Authentication -> Users). Run in Supabase -> SQL Editor as the project owner.
-- Do not run against production. Do not put real contract data here.
-- =====================================================================================

-- Fixed ids make the evidence reproducible and the curl examples stable.
-- Company A
insert into app.company (id, name)
values ('COMPANY_A_UUID', 'Staging Tenant A')
on conflict (id) do update set name = excluded.name;

-- Company B
insert into app.company (id, name)
values ('COMPANY_B_UUID', 'Staging Tenant B')
on conflict (id) do update set name = excluded.name;

-- Platform identity mirrors each Supabase Auth user.
insert into platform.user_account (id, email, display_name)
values
    ('__USER_A_UUID__', 'tenant-a@staging.invalid', 'Staging Tenant A'),
    ('__USER_B_UUID__', 'tenant-b@staging.invalid', 'Staging Tenant B')
on conflict (id) do update
set email = excluded.email,
    display_name = excluded.display_name;

insert into platform.user_profile (user_id, company_id)
values
    ('__USER_A_UUID__', 'COMPANY_A_UUID'),
    ('__USER_B_UUID__', 'COMPANY_B_UUID')
on conflict (user_id) do update set company_id = excluded.company_id;

-- Dedicated staging-only role: no agreement, publishing, comparison or quota administration.
insert into platform.role (key, name, description, is_system)
values ('ai_staging_tester', 'AI staging tester', 'Synthetic-only role for the AI staging exercise (#47).', false)
on conflict (key) do nothing;

insert into platform.role_capability (role_id, capability_id)
select r.id, c.id
from platform.role r
join platform.capability c on c.key in (
    'document.read', 'document.write',
    'clause.read', 'clause.process', 'clause.review'
)
where r.key = 'ai_staging_tester'
on conflict do nothing;

insert into platform.user_role (user_id, role_id)
select u.id, r.id
from (values ('__USER_A_UUID__'::uuid), ('__USER_B_UUID__'::uuid)) as u(id)
join platform.role r on r.key = 'ai_staging_tester'
on conflict do nothing;
-- Provisional staging quota: 5 documents, 100 pages, 500,000 tokens.
insert into platform.company_quota (company_id, documents_limit, pages_limit, tokens_limit)
values
    ('COMPANY_A_UUID', 5, 100, 500000),
    ('COMPANY_B_UUID', 5, 100, 500000)
on conflict (company_id) do update
set documents_limit = excluded.documents_limit,
    pages_limit = excluded.pages_limit,
    tokens_limit = excluded.tokens_limit,
    updated_at = now();

select c.id as company_id, c.name, up.user_id
from app.company c
left join platform.user_profile up on up.company_id = c.id
where c.id in ('COMPANY_A_UUID', 'COMPANY_B_UUID')
order by c.name;
