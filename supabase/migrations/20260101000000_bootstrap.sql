-- =====================================================================================
-- 20260101000000 — bootstrap: schemas, enum types, shared functions
--
-- Two schemas separate business data from infrastructure:
--   app      — business tables (catalog, comparison, legal, agreements, negotiation, publishing)
--   platform — infrastructure tables (identity, documents, audit)
--
-- This is a refinement of ADR-003 (one DbContext, one schema). The Phase 4 requirement to
-- separate business from infrastructure tables is satisfied by the schema split; the EF Core
-- DbContext remains single, so cross-module transactions are still atomic. No FK crosses in a
-- direction that would create a cycle.
--
-- gen_random_uuid() is core since PostgreSQL 13; no extension is required.
-- =====================================================================================

create schema if not exists app;
create schema if not exists platform;

comment on schema app is
  'Business data. Owned by the application modules; projected to the public portal through RLS.';
comment on schema platform is
  'Infrastructure data: identity mirror, document metadata, audit trail. Never exposed publicly.';

-- -------------------------------------------------------------------------------------
-- Enum types
--
-- Native enums are used for state vocabularies because the database must reject unknown values
-- (legacy stored states in char(N) with no constraint — DS-15, DS-17). Adding a value later is
-- a migration, which is the correct ceremony for a state machine change.
-- -------------------------------------------------------------------------------------

create type app.publication_state as enum ('Draft', 'Published');
create type app.negotiation_case_state as enum ('Draft', 'Open', 'Closed', 'Promoted');
create type app.petition_state as enum ('Open', 'Agreed', 'Rejected', 'Withdrawn');
create type app.party_role as enum ('Employer', 'Union', 'LabourMinistry', 'Other');
create type app.statute_kind as enum ('LabourLaw', 'OtherStatute');
create type platform.extraction_status as enum
  ('NotRequested', 'Requested', 'InProgress', 'Completed', 'Failed');

comment on type app.negotiation_case_state is
  'Draft -> Open -> Closed -> Promoted. Reopen moves Closed -> Open; Promoted is terminal (ADR-014).';
comment on type app.petition_state is
  'Agreed means a meeting agreement was recorded for this petition.';

-- -------------------------------------------------------------------------------------
-- Shared trigger: maintain updated_at
--
-- Justification: audit columns exist on every business table (DS-30 recorded that the legacy
-- system had them on 4 of 35 tables, and only on geography). Doing this in a trigger means no
-- code path can forget it.
-- -------------------------------------------------------------------------------------

create or replace function platform.set_updated_at()
returns trigger
language plpgsql
as $$
begin
  new.updated_at := now();
  return new;
end;
$$;

comment on function platform.set_updated_at() is
  'BEFORE UPDATE trigger: stamps updated_at. Attached to every table that has the column.';

-- -------------------------------------------------------------------------------------
-- Shared trigger: make a table append-only
-- Used by platform.audit_entry. Enforced in the database so the guarantee does not depend on
-- the application (legacy audit was a table that did not exist — DS-32).
-- -------------------------------------------------------------------------------------

create or replace function platform.reject_mutation()
returns trigger
language plpgsql
as $$
begin
  raise exception 'table %.% is append-only and rejects %',
    tg_table_schema, tg_table_name, tg_op
    using errcode = 'restrict_violation';
end;
$$;

-- -------------------------------------------------------------------------------------
-- Identity helpers
--
-- These read the JWT claims that the API sets per request on the database session:
--     SET LOCAL request.jwt.claims = '{"sub":"<uuid>","role":"authenticated"}';
--
-- Using our own function rather than Supabase's auth.uid() keeps the schema portable to a plain
-- PostgreSQL instance (which is how it is validated) and keeps RLS testable outside Supabase.
-- In Supabase, request.jwt.claims is set the same way, so both work unchanged.
-- -------------------------------------------------------------------------------------

create or replace function platform.current_user_id()
returns uuid
language sql
stable
as $$
  select nullif(current_setting('request.jwt.claims', true)::jsonb ->> 'sub', '')::uuid;
$$;

comment on function platform.current_user_id() is
  'The authenticated subject from request.jwt.claims. NULL when unauthenticated or unset.';

-- NOTE: platform.is_active_user() and platform.has_capability() are defined at the end of
-- migration 20260101000100, immediately after the identity tables they read. Defining them here
-- would fail because PostgreSQL resolves relation references in SQL function bodies at creation
-- time (check_function_bodies is on by default).

