-- =====================================================================================
-- 20260101000900 — roles and privileges
--
-- Three database roles, each with a distinct job:
--
--   anon          Supabase's unauthenticated role. Reads ONLY the public portal projection.
--                 Row visibility is narrowed further by RLS policies in the next migration.
--   authenticated Supabase's signed-in role. Reads business data. Holds NO write privilege:
--                 every write goes through the API, which is the only component that sanitises
--                 rich text, validates documents and writes audit entries.
--   app_api       the API's own role. The only role with DML. Still subject to RLS, so a missing
--                 capability check in application code is caught by the database.
--
-- The roles are created conditionally because Supabase already provides anon and authenticated,
-- while a plain PostgreSQL instance (used to validate this schema) does not. Creating them when
-- absent is what makes the same migration files run on both.
-- =====================================================================================

do $$
begin
    if not exists (select 1 from pg_roles where rolname = 'anon') then
        create role anon nologin noinherit;
    end if;
    if not exists (select 1 from pg_roles where rolname = 'authenticated') then
        create role authenticated nologin noinherit;
    end if;
    if not exists (select 1 from pg_roles where rolname = 'app_api') then
        create role app_api nologin noinherit;
    end if;
    -- The API connects as postgres through the session pooler and switches role per request.
    -- On PostgreSQL 16+ a role that creates another is not guaranteed SET membership, so grant it
    -- explicitly when it is missing. Harmless where postgres already has it.
    if exists (select 1 from pg_roles where rolname = 'postgres') then
        if not pg_has_role('postgres', 'app_api', 'MEMBER') then
            execute 'grant app_api to postgres';
        end if;
    end if;
end $$;

-- -------------------------------------------------------------------------------------
-- Schema usage
-- -------------------------------------------------------------------------------------
grant usage on schema app      to anon, authenticated, app_api;
grant usage on schema platform to authenticated, app_api;

-- -------------------------------------------------------------------------------------
-- anon — the public portal surface only
--
-- Deliberately narrow: reference geography, company identity, published agreements and their
-- clauses, published news. No statutes (the public portal does not show them in the MVP), no
-- negotiation data, no identity data, no audit.
-- -------------------------------------------------------------------------------------
grant select on
    app.country,
    app.state,
    app.municipality,
    app.company,
    app.agreement,
    app.agreement_clause,
    app.agreement_annex,
    app.news_category,
    app.news_article
to anon;

-- -------------------------------------------------------------------------------------
-- authenticated — read everything, write nothing
-- -------------------------------------------------------------------------------------
grant select on all tables in schema app to authenticated;

grant select on
    platform.user_account,
    platform.user_profile,
    platform.role,
    platform.capability,
    platform.role_capability,
    platform.user_role,
    platform.document,
    platform.audit_entry
to authenticated;

-- -------------------------------------------------------------------------------------
-- app_api — full DML, still policed by RLS
-- -------------------------------------------------------------------------------------
grant select, insert, update, delete on all tables in schema app      to app_api;
grant select, insert, update, delete on all tables in schema platform to app_api;

-- audit_entry.id is GENERATED ALWAYS AS IDENTITY
grant usage, select on all sequences in schema platform to app_api;

-- The API executes the guard triggers and calls the capability helpers.
grant execute on function platform.current_user_id()            to anon, authenticated, app_api;
grant execute on function platform.is_active_user()             to authenticated, app_api;
grant execute on function platform.has_capability(text)         to authenticated, app_api;

comment on role app_api is
  'The ASP.NET Core API. Sets request.jwt.claims per request so RLS evaluates the real caller. '
  'Never used from a browser: privileged credentials stay server-side (Phase 4 requirement).';
