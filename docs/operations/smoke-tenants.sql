-- =====================================================================================
-- Staging-only: the reusable tenant and least-privilege role for the T19 release smoke gate.
--
-- BEFORE RUNNING: replace __COMPANY_UUID__, __USER_UUID__, __EMAIL__, __DISPLAY_NAME__ and
-- __SMOKE_NAME__. The gate script does this with sed; run it by hand only for a manual retry.
--
-- Do not run against production. The tenant holds synthetic data only, and the role is
-- deliberately narrower than Administrator: it can author, publish and compare, but it cannot
-- administer users, companies, quotas or the platform.
--
-- The account and the tenant are NOT deleted by the gate: platform.audit_entry is append-only and
-- its actor_id foreign key performs ON DELETE SET NULL, an UPDATE the append-only trigger rejects,
-- so a user with audit rows cannot be deleted (verified 2026-11-22). The gate cleans the tenant's
-- content instead, and keeps this one identity for reuse.
-- =====================================================================================

insert into app.company (id, name)
values ('__COMPANY_UUID__', '__SMOKE_NAME__')
on conflict (id) do update set name = excluded.name;

insert into platform.user_account (id, email, display_name)
values ('__USER_UUID__', '__EMAIL__', '__DISPLAY_NAME__')
on conflict (id) do update
set email = excluded.email,
    display_name = excluded.display_name;

insert into platform.user_profile (user_id, company_id)
values ('__USER_UUID__', '__COMPANY_UUID__')
on conflict (user_id) do update set company_id = excluded.company_id;

-- Staging-only role. Capability set is exactly what the gate exercises, and nothing else:
-- documents (upload, extract, queue), clauses (read), agreements (author, publish) and the
-- comparison (which also needs statute.read: the endpoint requires both).
insert into platform.role (key, name, description, is_system)
values ('smoke_tester', 'Smoke tester', 'Synthetic-only role for the T19 release smoke gate.', false)
on conflict (key) do nothing;

insert into platform.role_capability (role_id, capability_id)
select r.id, c.id
from platform.role r
join platform.capability c on c.key in (
    'document.read', 'document.write',
    'clause.read', 'clause.process',
    'agreement.read', 'agreement.write', 'agreement.publish',
    'comparison.read', 'statute.read'
)
where r.key = 'smoke_tester'
on conflict do nothing;

insert into platform.user_role (user_id, role_id)
select u.id, r.id
from (values ('__USER_UUID__'::uuid)) as u(id)
join platform.role r on r.key = 'smoke_tester'
on conflict do nothing;

-- Hard ceiling for the gate: one document per run, and the teardown removes the usage row, so this
-- is a safety net rather than a quota the gate should ever reach.
insert into platform.company_quota (company_id, documents_limit, pages_limit, tokens_limit)
values ('__COMPANY_UUID__', 50, 500, 2000000)
on conflict (company_id) do update
set documents_limit = excluded.documents_limit,
    pages_limit = excluded.pages_limit,
    tokens_limit = excluded.tokens_limit,
    updated_at = now();

select c.id as company_id, c.name, up.user_id
from app.company c
left join platform.user_profile up on up.company_id = c.id
where c.id = '__COMPANY_UUID__';
