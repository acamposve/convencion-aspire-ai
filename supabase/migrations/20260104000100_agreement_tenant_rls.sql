-- =====================================================================================
-- 20260104000100 — scope agreements, clauses and annexes by company at the database
--
-- The original agreement policies checked only the capability. The API has always scoped by
-- company, and T8's tests prove it, but a capability-only policy meant a bug in a future handler
-- could read another company's agreement while still satisfying the database. This closes that
-- gap for the aggregate T8 introduced: the tenant predicate is now part of every read and write
-- policy, using the same platform.can_access_company() helper as documents.
--
-- Anonymous publication policies are untouched: published agreements are public by design.
--
-- Reversible: drop these policies and recreate the capability-only forms from migration
-- 20260101001000.
-- =====================================================================================

-- ---- agreement ----------------------------------------------------------------------------

drop policy if exists agreement_api_read on app.agreement;
create policy agreement_api_read on app.agreement
    for select to app_api
    using (
        platform.is_active_user()
        and platform.can_access_company(company_id)
    );

drop policy if exists agreement_authenticated_read on app.agreement;
create policy agreement_authenticated_read on app.agreement
    for select to authenticated
    using (
        platform.is_active_user()
        and platform.can_access_company(company_id)
    );

drop policy if exists agreement_api_insert on app.agreement;
create policy agreement_api_insert on app.agreement
    for insert to app_api
    with check (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and platform.can_access_company(company_id)
    );

drop policy if exists agreement_api_update on app.agreement;
create policy agreement_api_update on app.agreement
    for update to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and platform.can_access_company(company_id)
    )
    with check (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and platform.can_access_company(company_id)
    );

drop policy if exists agreement_api_delete on app.agreement;
create policy agreement_api_delete on app.agreement
    for delete to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and platform.can_access_company(company_id)
    );

-- ---- agreement_clause ---------------------------------------------------------------------
--
-- Clause visibility inherits the parent agreement. The EXISTS subquery is itself filtered by the
-- agreement policies, so the tenant check cannot be side-stepped through the child table.

drop policy if exists agreement_clause_api_read on app.agreement_clause;
create policy agreement_clause_api_read on app.agreement_clause
    for select to app_api
    using (
        platform.is_active_user()
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_clause.agreement_id
              and platform.can_access_company(d.company_id)
        )
    );

drop policy if exists agreement_clause_authenticated_read on app.agreement_clause;
create policy agreement_clause_authenticated_read on app.agreement_clause
    for select to authenticated
    using (
        platform.is_active_user()
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_clause.agreement_id
              and platform.can_access_company(d.company_id)
        )
    );

drop policy if exists agreement_clause_api_insert on app.agreement_clause;
create policy agreement_clause_api_insert on app.agreement_clause
    for insert to app_api
    with check (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_clause.agreement_id
              and platform.can_access_company(d.company_id)
        )
    );

drop policy if exists agreement_clause_api_update on app.agreement_clause;
create policy agreement_clause_api_update on app.agreement_clause
    for update to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_clause.agreement_id
              and platform.can_access_company(d.company_id)
        )
    )
    with check (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_clause.agreement_id
              and platform.can_access_company(d.company_id)
        )
    );

drop policy if exists agreement_clause_api_delete on app.agreement_clause;
create policy agreement_clause_api_delete on app.agreement_clause
    for delete to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_clause.agreement_id
              and platform.can_access_company(d.company_id)
        )
    );

-- ---- agreement_annex ----------------------------------------------------------------------

drop policy if exists agreement_annex_api_read on app.agreement_annex;
create policy agreement_annex_api_read on app.agreement_annex
    for select to app_api
    using (
        platform.is_active_user()
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_annex.agreement_id
              and platform.can_access_company(d.company_id)
        )
    );

drop policy if exists agreement_annex_authenticated_read on app.agreement_annex;
create policy agreement_annex_authenticated_read on app.agreement_annex
    for select to authenticated
    using (
        platform.is_active_user()
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_annex.agreement_id
              and platform.can_access_company(d.company_id)
        )
    );

drop policy if exists agreement_annex_api_insert on app.agreement_annex;
create policy agreement_annex_api_insert on app.agreement_annex
    for insert to app_api
    with check (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_annex.agreement_id
              and platform.can_access_company(d.company_id)
        )
    );

drop policy if exists agreement_annex_api_update on app.agreement_annex;
create policy agreement_annex_api_update on app.agreement_annex
    for update to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_annex.agreement_id
              and platform.can_access_company(d.company_id)
        )
    )
    with check (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_annex.agreement_id
              and platform.can_access_company(d.company_id)
        )
    );

drop policy if exists agreement_annex_api_delete on app.agreement_annex;
create policy agreement_annex_api_delete on app.agreement_annex
    for delete to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('agreement.write')
        and exists (
            select 1 from app.agreement d
            where d.id = agreement_annex.agreement_id
              and platform.can_access_company(d.company_id)
        )
    );

comment on table app.agreement is
  'An agreement is identified by (company_id, valid_from) — decision Q-1: the display title is '
  'composed on read from the company name and the validity period, never stored. RLS scopes every '
  'API read and write to the caller''s company (or company.read_all), in addition to the capability.';
