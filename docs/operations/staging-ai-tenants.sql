-- =====================================================================================
-- Staging-only: two isolated tenants for the AI exercise (#47/#52)
--
-- BEFORE RUNNING: replace USER_A_UUID and USER_B_UUID with the Supabase Auth user UIDs
-- (Authentication -> Users). Run in Supabase -> SQL Editor as the project owner.
-- Do not run against production. Do not put real contract data here.
-- =====================================================================================

-- Fixed ids make the evidence reproducible and the curl examples stable.
-- Company A
insert into app.company (id, name)
values ('a0000000-0000-4000-8000-000000000001', 'Staging Tenant A')
on conflict (id) do update set name = excluded.name;

-- Company B
insert into app.company (id, name)
values ('b0000000-0000-4000-8000-000000000002', 'Staging Tenant B')
on conflict (id) do update set name = excluded.name;

-- Platform identity mirrors each Supabase Auth user.
insert into platform.user_account (id, email, display_name)
values
    ('USER_A_UUID', 'tenant-a@staging.invalid', 'Staging Tenant A'),
    ('USER_B_UUID', 'tenant-b@staging.invalid', 'Staging Tenant B')
on conflict (id) do update
set email = excluded.email,
    display_name = excluded.display_name;

insert into platform.user_profile (user_id, company_id)
values
    ('USER_A_UUID', 'a0000000-0000-4000-8000-000000000001'),
    ('USER_B_UUID', 'b0000000-0000-4000-8000-000000000002')
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
from (values ('USER_A_UUID'::uuid), ('USER_B_UUID'::uuid)) as u(id)
join platform.role r on r.key = 'ai_staging_tester'
on conflict do nothing;
-- Provisional staging quota: 5 documents, 100 pages, 500,000 tokens.
insert into platform.company_quota (company_id, documents_limit, pages_limit, tokens_limit)
values
    ('a0000000-0000-4000-8000-000000000001', 5, 100, 500000),
    ('b0000000-0000-4000-8000-000000000002', 5, 100, 500000)
on conflict (company_id) do update
set documents_limit = excluded.documents_limit,
    pages_limit = excluded.pages_limit,
    tokens_limit = excluded.tokens_limit,
    updated_at = now();

select c.id as company_id, c.name, up.user_id
from app.company c
left join platform.user_profile up on up.company_id = c.id
where c.id in ('a0000000-0000-4000-8000-000000000001', 'b0000000-0000-4000-8000-000000000002')
order by c.name;
