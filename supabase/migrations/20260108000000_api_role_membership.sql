-- =====================================================================================
-- 20260108000000 - SET membership for the API's role switch
--
-- The API opens a pooled connection as postgres and runs `set role app_api` (or `anon`) per
-- request, so RLS and the capability guards evaluate the caller's role rather than the login's.
--
-- PostgreSQL 16 changed role grants: the creator of a role receives ADMIN but not SET, and
-- `set role` requires SET. On Supabase this surfaced as `42501 permission denied to set role
-- "app_api"` after the first deployment. Granting SET explicitly fixes it, on Supabase and on a
-- plain PostgreSQL instance alike. Re-running the grant is harmless: it updates the options.
-- =====================================================================================
do $$
begin
    if exists (select 1 from pg_roles where rolname = 'postgres') then
        execute 'grant app_api to postgres with inherit true, set true';
        execute 'grant anon to postgres with inherit true, set true';
        execute 'grant authenticated to postgres with inherit true, set true';
    end if;
end $$;