-- =====================================================================================
-- 20260101001000 — row level security
--
-- Every table is protected. The policies encode three separate questions, which the Phase 4
-- requirement insists must not be conflated:
--
--   1. WHO is asking        -> platform.current_user_id()      (the Supabase JWT subject)
--   2. MAY they act         -> platform.is_active_user()        (provisioned, not blocked, not expired)
--                              platform.has_capability(...)     (explicit grant, no implicit superuser)
--   3. WHICH rows           -> policy USING/WITH CHECK clauses, including same-case composite rules
--
-- A tenant_id column would answer none of these: it is a row attribute, not an identity or an
-- authorisation context. This deployment is single-tenant (approval item A-04), so isolation is
-- between anonymous and authenticated callers and between capability holders, not between tenants.
-- §10 documents exactly what a future tenant dimension would require.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- Bootstrap helper
--
-- SECURITY DEFINER so that the policy on platform.user_account can ask "does any user exist?"
-- without recursing through that same policy.
-- -------------------------------------------------------------------------------------
create or replace function platform.has_any_user()
returns boolean
language sql
stable
security definer
set search_path = platform, pg_temp
as $$
  select exists (select 1 from platform.user_account);
$$;

grant execute on function platform.has_any_user() to authenticated, app_api;

comment on function platform.has_any_user() is
  'Drives the one-time bootstrap window (constitution Art. VI.6). While no user exists, the API '
  'may create exactly one; afterwards the same insert requires user.manage.';

-- -------------------------------------------------------------------------------------
-- Enable RLS on every table
-- -------------------------------------------------------------------------------------
do $$
declare
    t record;
begin
    for t in
        select schemaname, tablename
        from pg_tables
        where schemaname in ('app', 'platform')
    loop
        execute format('alter table %I.%I enable row level security', t.schemaname, t.tablename);
    end loop;
end $$;

-- -------------------------------------------------------------------------------------
-- anon — public portal, explicit per-table policies
--
-- Only published content. An anonymous caller cannot see a draft agreement even if they know its
-- id, which is the AC-S7 criterion.
-- -------------------------------------------------------------------------------------
create policy company_anon_read on app.company
    for select to anon using (true);

create policy country_anon_read on app.country
    for select to anon using (true);

create policy state_anon_read on app.state
    for select to anon using (true);

create policy municipality_anon_read on app.municipality
    for select to anon using (true);

create policy agreement_anon_read on app.agreement
    for select to anon using (state = 'Published');

create policy agreement_clause_anon_read on app.agreement_clause
    for select to anon using (
        exists (
            select 1 from app.agreement a
            where a.id = agreement_clause.agreement_id
              and a.state = 'Published'
        )
    );

create policy agreement_annex_anon_read on app.agreement_annex
    for select to anon using (
        exists (
            select 1 from app.agreement a
            where a.id = agreement_annex.agreement_id
              and a.state = 'Published'
        )
    );

create policy news_category_anon_read on app.news_category
    for select to anon using (true);

create policy news_article_anon_read on app.news_article
    for select to anon using (state = 'Published');

-- -------------------------------------------------------------------------------------
-- authenticated and app_api — read access to business data
--
-- is_active_user() is the identity + account-state check: an unprovisioned, blocked or expired
-- caller reads nothing, on every request, not only at sign-in.
-- -------------------------------------------------------------------------------------
do $$
declare
    t record;
begin
    for t in select tablename from pg_tables where schemaname = 'app' loop
        execute format(
            'create policy %I on app.%I for select to authenticated using (platform.is_active_user())',
            t.tablename || '_authenticated_read', t.tablename);
        execute format(
            'create policy %I on app.%I for select to app_api using (platform.is_active_user())',
            t.tablename || '_api_read', t.tablename);
    end loop;
end $$;

-- -------------------------------------------------------------------------------------
-- app_api — writes, gated by capability
--
-- One policy triple per table from an explicit map. A table that is missing from this map has no
-- write policy at all, so the database refuses every write to it — fail closed.
-- -------------------------------------------------------------------------------------
do $$
declare
    m record;
begin
    for m in
        select * from (values
            ('company',                  'company.write'),
            ('company_sector',           'catalog.write'),
            ('company_type',             'catalog.write'),
            ('company_category',         'catalog.write'),
            ('economic_activity',        'catalog.write'),
            ('country',                  'catalog.write'),
            ('state',                    'catalog.write'),
            ('municipality',             'catalog.write'),
            ('comparison_heading_group', 'catalog.write'),
            ('comparison_heading',       'catalog.write'),
            ('statute',                  'statute.write'),
            ('statute_article',          'statute.write'),
            ('agreement',                'agreement.write'),
            ('agreement_clause',         'agreement.write'),
            ('agreement_annex',          'agreement.write'),
            ('negotiation_case',         'negotiation.write'),
            ('negotiation_party',        'negotiation.write'),
            ('union_petition',           'negotiation.write'),
            ('employer_offer',           'negotiation.write'),
            ('negotiation_meeting',      'negotiation.write'),
            ('meeting_agreement',        'negotiation.write'),
            ('news_category',            'news.write'),
            ('news_article',             'news.write')
        ) as v(table_name, capability)
    loop
        execute format(
            'create policy %I on app.%I for insert to app_api with check (platform.has_capability(%L))',
            m.table_name || '_api_insert', m.table_name, m.capability);
        execute format(
            'create policy %I on app.%I for update to app_api using (platform.has_capability(%L)) with check (platform.has_capability(%L))',
            m.table_name || '_api_update', m.table_name, m.capability, m.capability);
        execute format(
            'create policy %I on app.%I for delete to app_api using (platform.has_capability(%L))',
            m.table_name || '_api_delete', m.table_name, m.capability);
    end loop;
end $$;

-- -------------------------------------------------------------------------------------
-- platform tables — every policy explicit, because each has its own authorisation story
-- -------------------------------------------------------------------------------------

-- A user may read their own account; managing others requires user.manage.
-- INSERT: allowed only while the database has no users at all (the one-time bootstrap window),
-- or by a holder of user.manage. This is constitution Art. VI.6 expressed as a database rule:
-- the bootstrap cannot be replayed once a user exists, and no seeded account can appear.
create policy user_account_api_read on platform.user_account
    for select to app_api
    using (id = platform.current_user_id() or platform.has_capability('user.manage'));

create policy user_account_authenticated_read on platform.user_account
    for select to authenticated
    using (id = platform.current_user_id() or platform.has_capability('user.manage'));

create policy user_account_api_insert on platform.user_account
    for insert to app_api
    with check (not platform.has_any_user() or platform.has_capability('user.manage'));

create policy user_account_api_update on platform.user_account
    for update to app_api
    using (platform.has_capability('user.manage'))
    with check (platform.has_capability('user.manage'));

create policy user_account_api_delete on platform.user_account
    for delete to app_api
    using (platform.has_capability('user.manage'));

create policy user_profile_api_read on platform.user_profile
    for select to app_api
    using (user_id = platform.current_user_id() or platform.has_capability('user.manage'));

create policy user_profile_authenticated_read on platform.user_profile
    for select to authenticated
    using (user_id = platform.current_user_id() or platform.has_capability('user.manage'));

create policy user_profile_api_insert on platform.user_profile
    for insert to app_api with check (platform.has_capability('user.manage') or user_id = platform.current_user_id());

create policy user_profile_api_update on platform.user_profile
    for update to app_api
    using (platform.has_capability('user.manage') or user_id = platform.current_user_id())
    with check (platform.has_capability('user.manage') or user_id = platform.current_user_id());

create policy user_profile_api_delete on platform.user_profile
    for delete to app_api using (platform.has_capability('user.manage'));

-- Roles and capabilities are readable by any active user (the SPA needs the capability list to
-- render affordances); mutating them requires role.manage.
create policy role_api_read on platform.role
    for select to app_api using (platform.is_active_user());
create policy role_authenticated_read on platform.role
    for select to authenticated using (platform.is_active_user());
create policy role_api_insert on platform.role
    for insert to app_api with check (platform.has_capability('role.manage'));
create policy role_api_update on platform.role
    for update to app_api using (platform.has_capability('role.manage')) with check (platform.has_capability('role.manage'));
create policy role_api_delete on platform.role
    for delete to app_api using (platform.has_capability('role.manage') and is_system = false);

create policy capability_api_read on platform.capability
    for select to app_api using (platform.is_active_user());
create policy capability_authenticated_read on platform.capability
    for select to authenticated using (platform.is_active_user());
create policy capability_api_insert on platform.capability
    for insert to app_api with check (platform.has_capability('role.manage'));
create policy capability_api_update on platform.capability
    for update to app_api using (platform.has_capability('role.manage')) with check (platform.has_capability('role.manage'));
create policy capability_api_delete on platform.capability
    for delete to app_api using (platform.has_capability('role.manage'));

create policy role_capability_api_read on platform.role_capability
    for select to app_api using (platform.is_active_user());
create policy role_capability_authenticated_read on platform.role_capability
    for select to authenticated using (platform.is_active_user());
create policy role_capability_api_insert on platform.role_capability
    for insert to app_api with check (platform.has_capability('role.manage'));
create policy role_capability_api_delete on platform.role_capability
    for delete to app_api using (platform.has_capability('role.manage'));

create policy user_role_api_read on platform.user_role
    for select to app_api
    using (user_id = platform.current_user_id() or platform.has_capability('user.manage'));
create policy user_role_authenticated_read on platform.user_role
    for select to authenticated
    using (user_id = platform.current_user_id() or platform.has_capability('user.manage'));
create policy user_role_api_insert on platform.user_role
    for insert to app_api with check (platform.has_capability('user.manage'));
create policy user_role_api_delete on platform.user_role
    for delete to app_api using (platform.has_capability('user.manage'));

-- Documents: metadata readable by active users; writes require document.write. The bytes live in
-- Storage and are only reachable through API-issued signed URLs.
create policy document_api_read on platform.document
    for select to app_api using (platform.is_active_user());
create policy document_authenticated_read on platform.document
    for select to authenticated using (platform.is_active_user());
create policy document_api_insert on platform.document
    for insert to app_api with check (platform.has_capability('document.write'));
create policy document_api_update on platform.document
    for update to app_api using (platform.has_capability('document.write')) with check (platform.has_capability('document.write'));
create policy document_api_delete on platform.document
    for delete to app_api using (platform.has_capability('document.write'));

-- Audit: insert is a side effect of an authorised change, so it needs an active user but no
-- dedicated capability. Reading requires audit.read. There is deliberately NO update or delete
-- policy, so no caller can alter history — on top of the append-only trigger.
create policy audit_entry_api_read on platform.audit_entry
    for select to app_api using (platform.has_capability('audit.read'));
create policy audit_entry_authenticated_read on platform.audit_entry
    for select to authenticated using (platform.has_capability('audit.read'));
create policy audit_entry_api_insert on platform.audit_entry
    for insert to app_api with check (platform.is_active_user());

-- -------------------------------------------------------------------------------------
-- Privileged transition guards
--
-- RLS cannot distinguish a column change, so these two transitions get CHECK-like triggers that
-- call the same capability function. They make it impossible to publish an agreement or promote a
-- negotiation case by any path — including a direct SQL update by the API role — without the
-- capability. The legacy system had exactly one such gate and it was commented out.
-- -------------------------------------------------------------------------------------

create or replace function app.guard_agreement_publication()
returns trigger
language plpgsql
as $$
begin
    if new.state is distinct from old.state
       and not platform.has_capability('agreement.publish') then
        raise exception
            'capability agreement.publish is required to change an agreement publication state'
            using errcode = 'insufficient_privilege';
    end if;
    return new;
end;
$$;

create trigger agreement_publication_guard
    before update of state on app.agreement
    for each row execute function app.guard_agreement_publication();

create or replace function app.guard_case_promotion()
returns trigger
language plpgsql
as $$
begin
    if new.state = 'Promoted' and old.state is distinct from 'Promoted'
       and not platform.has_capability('negotiation.promote') then
        raise exception
            'capability negotiation.promote is required to promote a negotiation case'
            using errcode = 'insufficient_privilege';
    end if;
    return new;
end;
$$;

create trigger negotiation_case_promotion_guard
    before update of state on app.negotiation_case
    for each row execute function app.guard_case_promotion();

comment on function app.guard_agreement_publication() is
  'Publish/unpublish requires agreement.publish at the database layer, not only in the API.';
comment on function app.guard_case_promotion() is
  'Promotion requires negotiation.promote at the database layer.';
