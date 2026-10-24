-- =====================================================================================
-- 20260101000700 — app: negotiation cases
--
-- The domain model collapsed three legacy defects structurally:
--   * bitacoras stored three parties as 12 fixed columns  -> negotiation_party rows
--   * acuerdos carried four sibling pointers with nothing tying them together
--     -> meeting_agreement with composite FKs that force one case
--   * three junction tables with no primary key, no index and a denormalised case id
--     -> a petition belongs to a case, a meeting belongs to a case, and the agreement names both
-- =====================================================================================

create table app.negotiation_case (
    id                     uuid        primary key default gen_random_uuid(),
    company_id             uuid        not null references app.company (id) on delete restrict on update restrict,
    started_on             date        not null,
    state                  app.negotiation_case_state not null default 'Draft',
    closed_at              timestamptz,
    promoted_agreement_id  uuid        references app.agreement (id) on delete restrict on update restrict,

    created_at             timestamptz not null default now(),
    created_by             uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at             timestamptz not null default now(),
    updated_by             uuid        references platform.user_account (id) on delete set null on update restrict,

    -- Closed and Promoted are both "closed"; Draft and Open are not.
    constraint negotiation_case_closed_consistency check (
        (state in ('Closed', 'Promoted')) = (closed_at is not null)
    ),
    -- Promotion always names its result (AD-014 / approval item A-21).
    constraint negotiation_case_promoted_consistency check (
        (state = 'Promoted') = (promoted_agreement_id is not null)
    )
);

-- An agreement can be the product of at most one case. This is the database-level guarantee
-- behind idempotent promotion: a second attempt cannot create a second link.
create unique index negotiation_case_promoted_agreement_key
    on app.negotiation_case (promoted_agreement_id) where promoted_agreement_id is not null;

create index negotiation_case_company_idx on app.negotiation_case (company_id, started_on desc);
create index negotiation_case_state_idx   on app.negotiation_case (state);

create trigger negotiation_case_set_updated_at
    before update on app.negotiation_case
    for each row execute function platform.set_updated_at();

comment on column app.negotiation_case.state is
  'Draft -> Open -> Closed -> Promoted. Reopen moves Closed back to Open; Promoted is terminal '
  '(ADR-014). The legacy three values ('''', ''1'', ''10'') were ambiguous: ''1'' was written only '
  'by reopen and no case in the data ever reached ''10''.';
comment on column app.negotiation_case.promoted_agreement_id is
  'The agreement produced by this case. ON DELETE RESTRICT: the promoting case cannot silently '
  'lose its result.';
comment on table app.negotiation_case is
  'Invariants not expressible as CHECK constraints — at least one Union and one Employer party '
  'before Open, at least one meeting agreement before Close — are enforced by the domain aggregate '
  '(INV-N1, INV-N2) because they span child rows.';

-- -------------------------------------------------------------------------------------
-- Negotiation parties
--
-- Replaces twelve fixed columns in bitacoras (representative name/phone/email/position for
-- company, union and Labour Ministry). As rows, a fourth party or two representatives for one
-- party need no schema change.
-- -------------------------------------------------------------------------------------
create table app.negotiation_party (
    id          uuid        primary key default gen_random_uuid(),
    case_id     uuid        not null references app.negotiation_case (id) on delete cascade on update restrict,
    role        app.party_role not null,
    name        text        not null,
    phone       text,
    email       text,
    position    text,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint negotiation_party_name_not_blank  check (btrim(name) <> ''),
    constraint negotiation_party_email_valid     check (email is null or email ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$')
);

create index negotiation_party_case_idx on app.negotiation_party (case_id, role);

create trigger negotiation_party_set_updated_at
    before update on app.negotiation_party
    for each row execute function platform.set_updated_at();

comment on table app.negotiation_party is
  'Personal data: representative names, emails and phones. Never projected to the public portal '
  '(security-model §5.5).';

-- -------------------------------------------------------------------------------------
-- Petitions
-- -------------------------------------------------------------------------------------
create table app.union_petition (
    id             uuid        primary key default gen_random_uuid(),
    case_id        uuid        not null references app.negotiation_case (id) on delete cascade on update restrict,
    number_int     integer     not null,
    number_suffix  text,
    title          text        not null,
    body           text        not null,
    heading_id     uuid        references app.comparison_heading (id) on delete restrict on update restrict,
    state          app.petition_state not null default 'Open',

    created_at     timestamptz not null default now(),
    created_by     uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at     timestamptz not null default now(),
    updated_by     uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint union_petition_number_positive check (number_int >= 1),
    constraint union_petition_suffix_format   check (number_suffix is null or number_suffix ~ '^[A-Z]{1,2}$'),
    constraint union_petition_title_not_blank check (btrim(title) <> ''),
    constraint union_petition_body_not_blank  check (btrim(body) <> ''),

    -- Composite FK target so that a meeting agreement can be constrained to the same case.
    constraint union_petition_id_case_id_key unique (id, case_id)
);

create unique index union_petition_number_key
    on app.union_petition (case_id, number_int, coalesce(number_suffix, ''));

create index union_petition_case_state_idx on app.union_petition (case_id, state);

create trigger union_petition_set_updated_at
    before update on app.union_petition
    for each row execute function platform.set_updated_at();

comment on column app.union_petition.heading_id is
  'Nullable: a petition may not yet be classified. Clause classification is mandatory, petition '
  'classification is not — every legacy petition carried 0.';

-- -------------------------------------------------------------------------------------
-- Employer offers
-- -------------------------------------------------------------------------------------
create table app.employer_offer (
    id             uuid        primary key default gen_random_uuid(),
    petition_id    uuid        not null references app.union_petition (id) on delete cascade on update restrict,
    number_int     integer     not null,
    number_suffix  text,
    title          text        not null,
    body           text        not null,
    created_at     timestamptz not null default now(),
    created_by     uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at     timestamptz not null default now(),
    updated_by     uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint employer_offer_number_positive check (number_int >= 1),
    constraint employer_offer_suffix_format   check (number_suffix is null or number_suffix ~ '^[A-Z]{1,2}$'),
    constraint employer_offer_title_not_blank check (btrim(title) <> ''),
    constraint employer_offer_body_not_blank  check (btrim(body) <> '')
);

create unique index employer_offer_number_key
    on app.employer_offer (petition_id, number_int, coalesce(number_suffix, ''));

create index employer_offer_petition_idx on app.employer_offer (petition_id);

create trigger employer_offer_set_updated_at
    before update on app.employer_offer
    for each row execute function platform.set_updated_at();

comment on table app.employer_offer is
  'An offer responds to exactly one petition (approval item A-20). The legacy codigo_peticion was '
  'nullable and 3 of 4 values pointed at petitions that did not exist.';

-- -------------------------------------------------------------------------------------
-- Meetings
-- -------------------------------------------------------------------------------------
create table app.negotiation_meeting (
    id          uuid        primary key default gen_random_uuid(),
    case_id     uuid        not null references app.negotiation_case (id) on delete cascade on update restrict,
    held_on     date        not null,
    start_time  time,
    duration    text,
    summary     text,
    details     text,
    attendees   text,
    created_at  timestamptz not null default now(),
    created_by  uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at  timestamptz not null default now(),
    updated_by  uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint negotiation_meeting_id_case_id_key unique (id, case_id)
);

create index negotiation_meeting_case_date_idx on app.negotiation_meeting (case_id, held_on desc);

create trigger negotiation_meeting_set_updated_at
    before update on app.negotiation_meeting
    for each row execute function platform.set_updated_at();

comment on column app.negotiation_meeting.held_on is
  'A real date. Legacy fecha_reunion was TEXT holding yyyy-mm-dd, while fecha_acuerdo in the same '
  'module was TEXT holding dd-mm-yyyy.';
comment on column app.negotiation_meeting.attendees is
  'Free text. Structured attendees would be a new requirement; the parties are modelled separately.';

-- -------------------------------------------------------------------------------------
-- Meeting agreements
--
-- An agreement reached in a meeting about a petition. The composite foreign keys are the
-- important part: they make it impossible to record an agreement whose petition belongs to a
-- different case than the meeting, which no legacy constraint prevented.
-- -------------------------------------------------------------------------------------
create table app.meeting_agreement (
    id             uuid        primary key default gen_random_uuid(),
    case_id        uuid        not null references app.negotiation_case (id) on delete cascade on update restrict,
    petition_id    uuid        not null,
    meeting_id     uuid        not null,
    agreed_on      date        not null,
    number_int     integer     not null,
    number_suffix  text,
    title          text        not null,
    body           text        not null,
    heading_id     uuid        references app.comparison_heading (id) on delete restrict on update restrict,

    created_at     timestamptz not null default now(),
    created_by     uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at     timestamptz not null default now(),
    updated_by     uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint meeting_agreement_number_positive check (number_int >= 1),
    constraint meeting_agreement_suffix_format   check (number_suffix is null or number_suffix ~ '^[A-Z]{1,2}$'),
    constraint meeting_agreement_title_not_blank check (btrim(title) <> ''),
    constraint meeting_agreement_body_not_blank  check (btrim(body) <> ''),

    constraint meeting_agreement_petition_fk foreign key (petition_id, case_id)
        references app.union_petition (id, case_id) on delete cascade on update restrict,
    constraint meeting_agreement_meeting_fk foreign key (meeting_id, case_id)
        references app.negotiation_meeting (id, case_id) on delete cascade on update restrict
);

create unique index meeting_agreement_number_key
    on app.meeting_agreement (case_id, number_int, coalesce(number_suffix, ''));

create index meeting_agreement_petition_idx on app.meeting_agreement (petition_id);
create index meeting_agreement_meeting_idx  on app.meeting_agreement (meeting_id);

create trigger meeting_agreement_set_updated_at
    before update on app.meeting_agreement
    for each row execute function platform.set_updated_at();

comment on table app.meeting_agreement is
  'Agreements reached in a meeting. Distinct from app.agreement (a collective agreement): the '
  'Spanish copy must distinguish "acuerdo de reunión" from "convención". The legacy acuerdos table '
  'also carried four sibling pointers with no constraint that they agreed with each other.';
comment on column app.meeting_agreement.heading_id is
  'Nullable. The legacy caller hardcoded this to 0 for every agreement it generated, so the '
  'comparison heading had to be set afterwards, if at all.';
