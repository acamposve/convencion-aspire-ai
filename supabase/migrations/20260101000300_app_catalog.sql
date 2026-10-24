-- =====================================================================================
-- 20260101000300 — app: catalog (geography, company classification, companies)
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- Geography
--
-- Native surrogate keys with natural-key uniqueness. The legacy system used composite keys
-- (Id_estado, Id_pais) with a global AUTO_INCREMENT, so Id_pais in the primary key added no
-- uniqueness and no per-country numbering (legacy-schema-relationships §4.6).
-- -------------------------------------------------------------------------------------
create table app.country (
    id          uuid        primary key default gen_random_uuid(),
    code        text,
    name        text        not null,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint country_name_not_blank check (btrim(name) <> ''),
    constraint country_code_format    check (code is null or code ~ '^[A-Z]{2}$')
);

create unique index country_name_key on app.country (lower(name));
create unique index country_code_key on app.country (code) where code is not null;

create trigger country_set_updated_at
    before update on app.country
    for each row execute function platform.set_updated_at();

create table app.state (
    id          uuid        primary key default gen_random_uuid(),
    country_id  uuid        not null references app.country (id) on delete restrict on update restrict,
    name        text        not null,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint state_name_not_blank check (btrim(name) <> ''),
    -- redundant with the primary key, but required as a composite FK target so that a company
    -- cannot pair a state with the wrong country
    constraint state_id_country_id_key unique (id, country_id)
);

create unique index state_country_name_key on app.state (country_id, lower(name));

create trigger state_set_updated_at
    before update on app.state
    for each row execute function platform.set_updated_at();

create table app.municipality (
    id          uuid        primary key default gen_random_uuid(),
    state_id    uuid        not null references app.state (id) on delete restrict on update restrict,
    name        text        not null,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint municipality_name_not_blank check (btrim(name) <> ''),
    constraint municipality_id_state_id_key unique (id, state_id)
);

create unique index municipality_state_name_key on app.municipality (state_id, lower(name));

create trigger municipality_set_updated_at
    before update on app.municipality
    for each row execute function platform.set_updated_at();

-- -------------------------------------------------------------------------------------
-- Company classification vocabularies
--
-- Four separate tables rather than one table with a discriminator: a FK can then guarantee that
-- a company's sector is a sector and not a type. A single polymorphic catalogue table would make
-- that unenforceable, which is the kind of "absence as a value" defect this schema avoids.
-- -------------------------------------------------------------------------------------
create table app.company_sector (
    id          uuid        primary key default gen_random_uuid(),
    name        text        not null,
    description text,
    sort_order  integer     not null default 0,
    is_active   boolean     not null default true,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint company_sector_name_not_blank check (btrim(name) <> '')
);

create unique index company_sector_name_key on app.company_sector (lower(name));

create trigger company_sector_set_updated_at
    before update on app.company_sector
    for each row execute function platform.set_updated_at();

create table app.company_type (
    id          uuid        primary key default gen_random_uuid(),
    name        text        not null,
    description text,
    sort_order  integer     not null default 0,
    is_active   boolean     not null default true,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint company_type_name_not_blank check (btrim(name) <> '')
);

create unique index company_type_name_key on app.company_type (lower(name));

create trigger company_type_set_updated_at
    before update on app.company_type
    for each row execute function platform.set_updated_at();

create table app.company_category (
    id          uuid        primary key default gen_random_uuid(),
    name        text        not null,
    description text,
    sort_order  integer     not null default 0,
    is_active   boolean     not null default true,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint company_category_name_not_blank check (btrim(name) <> '')
);

create unique index company_category_name_key on app.company_category (lower(name));

create trigger company_category_set_updated_at
    before update on app.company_category
    for each row execute function platform.set_updated_at();

create table app.economic_activity (
    id          uuid        primary key default gen_random_uuid(),
    name        text        not null,
    description text,
    sort_order  integer     not null default 0,
    is_active   boolean     not null default true,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint economic_activity_name_not_blank check (btrim(name) <> '')
);

create unique index economic_activity_name_key on app.economic_activity (lower(name));

create trigger economic_activity_set_updated_at
    before update on app.economic_activity
    for each row execute function platform.set_updated_at();

-- -------------------------------------------------------------------------------------
-- company
--
-- ONE identity. The legacy table carried both `id` (116-195) and `codigo_empresa` (2-115) with
-- zero overlap in 80 rows, joined agreements on the code, and had one user row referencing the
-- id — two live identities for one entity (legacy-schema-relationships §3). Legacy_code keeps
-- the old value as data when the business cites it (approval item A-42); it is never a join key.
-- -------------------------------------------------------------------------------------
create table app.company (
    id                  uuid        primary key default gen_random_uuid(),
    name                text        not null,
    legal_name          text,
    tax_id              text,
    legacy_code         text,

    -- address (owned value object)
    street_address      text,
    city                text,
    country_id          uuid        references app.country (id) on delete restrict on update restrict,
    state_id            uuid        references app.state (id) on delete restrict on update restrict,
    municipality_id     uuid        references app.municipality (id) on delete restrict on update restrict,
    postal_code         text,

    -- primary contact (owned value object)
    contact_name        text,
    contact_position    text,
    contact_phone       text,
    contact_email       text,

    -- classification
    sector_id           uuid        references app.company_sector (id) on delete restrict on update restrict,
    type_id             uuid        references app.company_type (id) on delete restrict on update restrict,
    category_id         uuid        references app.company_category (id) on delete restrict on update restrict,
    activity_id         uuid        references app.economic_activity (id) on delete restrict on update restrict,

    -- workforce
    worker_count        integer,
    employee_count      integer,
    size_band           text,

    logo_document_id    uuid        references platform.document (id) on delete set null on update restrict,
    archived_at         timestamptz,

    created_at          timestamptz not null default now(),
    created_by          uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at          timestamptz not null default now(),
    updated_by          uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint company_name_not_blank      check (btrim(name) <> ''),
    constraint company_worker_count_valid  check (worker_count is null or worker_count >= 0),
    constraint company_employee_count_valid check (employee_count is null or employee_count >= 0),
    constraint company_contact_email_valid check (contact_email is null or contact_email ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$'),

    -- Geography consistency enforced by composite FKs: the state must belong to the given
    -- country, and the municipality must belong to the given state. Individual FKs alone would
    -- permit a company in Venezuela, state=Trujillo, municipality belonging to Zulia.
    constraint company_state_country_fk foreign key (state_id, country_id)
        references app.state (id, country_id) on delete restrict on update restrict,
    constraint company_municipality_state_fk foreign key (municipality_id, state_id)
        references app.municipality (id, state_id) on delete restrict on update restrict
);

create unique index company_tax_id_key      on app.company (upper(tax_id))      where tax_id is not null;
create unique index company_legacy_code_key on app.company (legacy_code)        where legacy_code is not null;

create index company_name_idx         on app.company (lower(name));
create index company_sector_idx       on app.company (sector_id);
create index company_type_idx         on app.company (type_id);
create index company_country_idx      on app.company (country_id);
create index company_archived_idx     on app.company (archived_at) where archived_at is null;

create trigger company_set_updated_at
    before update on app.company
    for each row execute function platform.set_updated_at();

comment on column app.company.legacy_code is
  'The legacy codigo_empresa, retained as data only. Unique when present, never a join key '
  '(approval item A-42).';
comment on column app.company.archived_at is
  'Archive instead of delete: agreements and negotiation cases must remain attributable to a '
  'company. Hard delete is blocked by ON DELETE RESTRICT from dependent tables.';
comment on column app.company.size_band is
  'Free text band as the business describes it; worker_count/employee_count carry the numbers.';

-- Deferred FK from platform.user_profile (declared without the reference in migration 00100 to
-- keep schema creation acyclic: platform -> app -> platform).
alter table platform.user_profile
    add constraint user_profile_company_fk
    foreign key (company_id) references app.company (id) on delete set null on update restrict;

comment on column platform.user_profile.company_id is
  'Scopes a negotiator to their own company. The legacy system applied this as a list filter in '
  'one screen only (discussion list), not as an authorisation control.';
