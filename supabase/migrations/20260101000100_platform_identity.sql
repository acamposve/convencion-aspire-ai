-- =====================================================================================
-- 20260101000100 — platform: identity, roles and capabilities
--
-- Identity is delegated to Supabase Auth (approved architecture: security-model §2.1). This
-- schema therefore stores NO password, hash, salt or reset token. It stores the authorisation
-- record: who may do what, plus blocking and expiry.
--
-- The legacy system stored plaintext passwords in usuarios.Clave_usuario varchar(20) and
-- compared them with LIKE (security-findings §S3). That column has no counterpart here by design.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- user_account — mirrors a Supabase Auth user
--
-- Deliberately NO foreign key to auth.users(id): that schema is managed by Supabase, is absent
-- from a plain PostgreSQL instance (which is how this schema is validated), and would couple our
-- migrations to a Supabase-internal table. The referential expectation is enforced by the
-- application on provisioning, and documented in docs/database/target-schema.md §7.
-- -------------------------------------------------------------------------------------
create table platform.user_account (
    id              uuid        primary key,
    email           text        not null,
    display_name    text        not null,
    is_blocked      boolean     not null default false,
    expires_on      date,
    last_seen_at    timestamptz,
    created_at      timestamptz not null default now(),
    created_by      uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at      timestamptz not null default now(),
    updated_by      uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint user_account_email_not_blank        check (btrim(email) <> ''),
    constraint user_account_display_name_not_blank check (btrim(display_name) <> '')
);

create unique index user_account_email_key on platform.user_account (lower(email));

comment on table  platform.user_account is
  'Authorisation record for a Supabase Auth user. id equals the JWT sub claim.';
comment on column platform.user_account.expires_on is
  'Optional account expiry. Enforced on every request by platform.is_active_user(); '
  'legacy enforced expiry in one view only, with a hand-rolled and fatally broken check.';

create trigger user_account_set_updated_at
    before update on platform.user_account
    for each row execute function platform.set_updated_at();

-- -------------------------------------------------------------------------------------
-- user_profile — 1:1 optional profile detail
-- -------------------------------------------------------------------------------------
create table platform.user_profile (
    user_id     uuid        primary key references platform.user_account (id) on delete cascade on update restrict,
    phone       text,
    company_id  uuid,       -- FK added in migration 20260101000300 (app.company)
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint user_profile_phone_not_blank check (phone is null or btrim(phone) <> '')
);

create trigger user_profile_set_updated_at
    before update on platform.user_profile
    for each row execute function platform.set_updated_at();

comment on column platform.user_profile.company_id is
  'Scopes a negotiator to their own company. FK to app.company added in the catalog migration '
  'to keep schema creation acyclic.';

-- -------------------------------------------------------------------------------------
-- role / capability / grants
-- -------------------------------------------------------------------------------------
create table platform.role (
    id          uuid        primary key default gen_random_uuid(),
    key         text        not null,
    name        text        not null,
    description text,
    is_system   boolean     not null default false,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint role_key_format   check (key ~ '^[a-z][a-z0-9_]*$'),
    constraint role_name_not_blank check (btrim(name) <> '')
);

create unique index role_key_key on platform.role (key);

create trigger role_set_updated_at
    before update on platform.role
    for each row execute function platform.set_updated_at();

comment on column platform.role.is_system is
  'System roles cannot be deleted or renamed. Protects the administration role.';

create table platform.capability (
    id          uuid        primary key default gen_random_uuid(),
    key         text        not null,
    description text        not null,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint capability_key_format check (key ~ '^[a-z][a-z0-9_]*\.[a-z][a-z0-9_]*$')
);

create unique index capability_key_key on platform.capability (key);

create trigger capability_set_updated_at
    before update on platform.capability
    for each row execute function platform.set_updated_at();

comment on table platform.capability is
  'resource.action capability keys, e.g. agreement.publish. Replaces the legacy model which '
  'granted access per PHP filename (pantallas.url_pantalla) — a key that cannot survive a SPA.';

create table platform.role_capability (
    role_id       uuid not null references platform.role (id)       on delete cascade on update restrict,
    capability_id uuid not null references platform.capability (id) on delete cascade on update restrict,

    primary key (role_id, capability_id)
);

comment on table platform.role_capability is
  'Unique by primary key. The legacy grant table (seguridades) had no uniqueness at all, so the '
  'same grant could be recorded repeatedly.';

create table platform.user_role (
    user_id     uuid        not null references platform.user_account (id) on delete cascade on update restrict,
    role_id     uuid        not null references platform.role (id)         on delete cascade on update restrict,
    granted_at  timestamptz not null default now(),
    granted_by  uuid        references platform.user_account (id) on delete set null on update restrict,

    primary key (user_id, role_id)
);

-- -------------------------------------------------------------------------------------
-- Identity helper functions
--
-- Defined here rather than in bootstrap because PostgreSQL resolves relation references in SQL
-- function bodies at creation time.
--
-- SECURITY DEFINER is required: these functions are called from RLS policies on platform tables,
-- and would otherwise recurse through those same policies. search_path is pinned to defeat
-- search_path hijacking of a definer-rights function.
-- -------------------------------------------------------------------------------------

create or replace function platform.is_active_user()
returns boolean
language sql
stable
security definer
set search_path = platform, pg_temp
as $$
  select exists (
    select 1
    from platform.user_account ua
    where ua.id = platform.current_user_id()
      and ua.is_blocked = false
      and (ua.expires_on is null or ua.expires_on >= current_date)
  );
$$;

comment on function platform.is_active_user() is
  'True when the caller has a provisioned profile that is neither blocked nor expired. '
  'Enforced on every request — legacy enforced blocking only at login and expiry never.';

create or replace function platform.has_capability(p_capability text)
returns boolean
language sql
stable
security definer
set search_path = platform, pg_temp
as $$
  select exists (
    select 1
    from platform.user_account ua
    join platform.user_role ur       on ur.user_id = ua.id
    join platform.role_capability rc on rc.role_id = ur.role_id
    join platform.capability c       on c.id = rc.capability_id
    where ua.id = platform.current_user_id()
      and ua.is_blocked = false
      and (ua.expires_on is null or ua.expires_on >= current_date)
      and (c.key = p_capability or c.key = 'system.superadmin')
  );
$$;

comment on function platform.has_capability(text) is
  'Capability check used by write policies. system.superadmin satisfies every capability, but '
  'only when explicitly granted — there is no implicit superuser like the legacy if ($t_usuario != 1).';
