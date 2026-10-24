-- =====================================================================================
-- 20260101000600 — app: collective agreements, clauses and annexes
--
-- This is the primary workflow's aggregate (W1). The legacy contratos table had no title, no
-- number and no identity beyond its company (BR-01), so two agreements for one company were
-- indistinguishable; identity is added here (approval item A-15).
-- =====================================================================================

create table app.agreement (
    id              uuid        primary key default gen_random_uuid(),
    company_id      uuid        not null references app.company (id) on delete restrict on update restrict,
    title           text        not null,
    number          text,
    valid_from      date        not null,
    valid_to        date,
    scope           text        not null,
    state           app.publication_state not null default 'Draft',
    published_at    timestamptz,
    published_by    uuid        references platform.user_account (id) on delete restrict on update restrict,

    created_at      timestamptz not null default now(),
    created_by      uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at      timestamptz not null default now(),
    updated_by      uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint agreement_title_not_blank  check (btrim(title) <> ''),
    constraint agreement_scope_not_blank  check (btrim(scope) <> ''),
    constraint agreement_validity_order   check (valid_to is null or valid_to >= valid_from),

    -- Publication is all-or-nothing. The legacy flag was a bare char(2) with three values in the
    -- data for two states ('PU', 'Pe', 'PE'), so 2 rows matched no predicate in the application.
    constraint agreement_publication_consistency check (
        (state = 'Published' and published_at is not null and published_by is not null)
        or
        (state = 'Draft'     and published_at is null     and published_by is null)
    )
);

-- One agreement per company per start date. Not a legacy rule — the legacy had no uniqueness at
-- all — but without it "which agreement is the 2026 one for company X" has no answer.
create unique index agreement_company_valid_from_key on app.agreement (company_id, valid_from);

create index agreement_company_idx   on app.agreement (company_id, valid_from desc);
create index agreement_state_idx     on app.agreement (state);
create index agreement_published_idx on app.agreement (valid_from desc) where state = 'Published';

create trigger agreement_set_updated_at
    before update on app.agreement
    for each row execute function platform.set_updated_at();

comment on column app.agreement.published_by is
  'Publication is a recorded act with an actor and a timestamp (ADR-013). The legacy system '
  'recorded neither.';
comment on column app.agreement.state is
  'Draft or Published. There is no approval workflow in the MVP (approval item A-41).';

-- -------------------------------------------------------------------------------------
-- agreement_clause
-- -------------------------------------------------------------------------------------
create table app.agreement_clause (
    id                            uuid        primary key default gen_random_uuid(),
    agreement_id                  uuid        not null references app.agreement (id) on delete cascade on update restrict,
    number_int                    integer     not null,
    number_suffix                 text,
    title                         text        not null,
    summary                       text,
    body                          text        not null,
    heading_id                    uuid        not null references app.comparison_heading (id) on delete restrict on update restrict,
    economic_comparison_note      text,
    is_excluded_from_compilation  boolean     not null default false,
    exclusion_note                text,

    created_at                    timestamptz not null default now(),
    created_by                    uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at                    timestamptz not null default now(),
    updated_by                    uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint agreement_clause_number_positive  check (number_int >= 1),
    constraint agreement_clause_suffix_format    check (number_suffix is null or number_suffix ~ '^[A-Z]{1,2}$'),
    constraint agreement_clause_title_not_blank  check (btrim(title) <> ''),
    constraint agreement_clause_body_not_blank   check (btrim(body) <> ''),

    -- A note explains an exclusion; an exclusion without a note is allowed, a note on a
    -- non-excluded clause is a contradiction.
    constraint agreement_clause_exclusion_note_consistency
        check (is_excluded_from_compilation or exclusion_note is null)
);

-- Clause number unique within its agreement. The legacy system had NO duplicate check for
-- agreement clauses (unlike statute articles), so two clauses numbered 005 could coexist.
create unique index agreement_clause_number_key
    on app.agreement_clause (agreement_id, number_int, coalesce(number_suffix, ''));

-- Correct numeric ordering for the clause workspace and the compiled document.
create index agreement_clause_order_idx
    on app.agreement_clause (agreement_id, number_int, number_suffix);

-- The comparison workspace reads all clauses for a heading across agreements.
create index agreement_clause_heading_idx
    on app.agreement_clause (heading_id);

create trigger agreement_clause_set_updated_at
    before update on app.agreement_clause
    for each row execute function platform.set_updated_at();

comment on column app.agreement_clause.heading_id is
  'NOT NULL: every clause is classified (approval item A-16). The legacy column was nullable and '
  '899 of 5402 clauses were unclassified.';
comment on column app.agreement_clause.is_excluded_from_compilation is
  'Replaces legacy articulo_cerrado char(1), which held ''0'' (4052), ''1'' (158) and NULL (1192) '
  'and was read only by the print view. The name states the behaviour; the exclusion reason is '
  'unknown and must be supplied by the business (approval item A-17).';
comment on column app.agreement_clause.economic_comparison_note is
  'Free text in the MVP. The legacy campo_comparativo was varchar(250)/text and no code ever read '
  'it. Structuring it would invent domain semantics (approval item A-18).';
comment on column app.agreement_clause.body is
  'Sanitised rich text. Sanitisation happens in the API on write (security-model §5.2); the '
  'database stores what it is given.';

-- -------------------------------------------------------------------------------------
-- agreement_annex
-- -------------------------------------------------------------------------------------
create table app.agreement_annex (
    id              uuid        primary key default gen_random_uuid(),
    agreement_id    uuid        not null references app.agreement (id) on delete cascade on update restrict,
    name            text        not null,
    description     text,
    body            text,
    document_id     uuid        references platform.document (id) on delete set null on update restrict,
    sort_order      integer     not null default 0,
    created_at      timestamptz not null default now(),
    created_by      uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at      timestamptz not null default now(),
    updated_by      uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint agreement_annex_name_not_blank check (btrim(name) <> '')
);

create unique index agreement_annex_name_key on app.agreement_annex (agreement_id, lower(name));
create index agreement_annex_order_idx on app.agreement_annex (agreement_id, sort_order);

create trigger agreement_annex_set_updated_at
    before update on app.agreement_annex
    for each row execute function platform.set_updated_at();

comment on table app.agreement_annex is
  'The legacy annexes had their own status_publicacion varchar(15) — all 29 rows ''Pendiente'', '
  'read by no code (DS-16). No publication state is carried over; if annexes need one it will be '
  'the same enum as the agreement.';
