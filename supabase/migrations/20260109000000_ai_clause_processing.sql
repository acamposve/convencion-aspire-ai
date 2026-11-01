-- =====================================================================================
-- 20260109000000 — AI clause processing: run, attempts, clauses, taxonomy, classifications
--
-- Design: docs/architecture/AI-CLAUSE-EXTRACTION-AND-CLASSIFICATION.md §8.
--
-- Tenant model: every tenant-owned table carries company_id and is guarded by
-- platform.can_access_company(company_id), not only by a capability. A bug in application
-- filtering therefore cannot leak another company's clauses.
-- =====================================================================================

create type platform.processing_status as enum
    ('Queued', 'Running', 'Completed', 'Failed', 'Cancelled');

create type platform.clause_review_status as enum
    ('Pending', 'Accepted', 'Corrected', 'Rejected');

-- -------------------------------------------------------------------------------------
-- processing_run — one click of "Procesar"
-- -------------------------------------------------------------------------------------
create table platform.processing_run (
    id                  uuid primary key default gen_random_uuid(),
    document_id         uuid        not null references platform.document (id) on delete restrict on update restrict,
    company_id          uuid        not null references app.company (id) on delete restrict on update restrict,
    requested_by        uuid        references platform.user_account (id) on delete set null on update restrict,
    status              platform.processing_status not null default 'Queued',
    stage               text        not null default 'Queued',
    provider            text,
    model               text,
    prompt_version      text,
    attempts            integer     not null default 0,
    clause_count        integer     not null default 0,
    classified_count    integer     not null default 0,
    unclassified_count  integer     not null default 0,
    input_tokens        bigint      not null default 0,
    output_tokens       bigint      not null default 0,
    error_code          text,
    error_message       text,
    created_at          timestamptz not null default now(),
    started_at          timestamptz,
    finished_at         timestamptz,
    updated_at          timestamptz not null default now(),

    constraint processing_run_attempts_not_negative check (attempts >= 0),
    constraint processing_run_counts_not_negative
        check (clause_count >= 0 and classified_count >= 0 and unclassified_count >= 0),
    constraint processing_run_tokens_not_negative check (input_tokens >= 0 and output_tokens >= 0),
    constraint processing_run_stage_not_blank check (btrim(stage) <> '')
);

create unique index processing_run_active_key
    on platform.processing_run (document_id)
    where status in ('Queued', 'Running');

create index processing_run_company_status_idx
    on platform.processing_run (company_id, status, created_at desc);

create index processing_run_document_idx
    on platform.processing_run (document_id, created_at desc);

create trigger processing_run_set_updated_at
    before update on platform.processing_run
    for each row execute function platform.set_updated_at();

comment on column platform.processing_run.company_id is
  'Tenant key. The RLS policy checks it through platform.can_access_company.';
comment on column platform.processing_run.stage is
  'Coarse progress label: Queued, Preparing, ExtractingText, ExtractingClauses, ClassifyingClauses, Validating, Completed.';

-- -------------------------------------------------------------------------------------
-- processing_attempt — one execution attempt of a run
-- -------------------------------------------------------------------------------------
create table platform.processing_attempt (
    id                 uuid primary key default gen_random_uuid(),
    run_id             uuid        not null references platform.processing_run (id) on delete cascade on update restrict,
    company_id         uuid        not null references app.company (id) on delete restrict on update restrict,
    attempt_no         integer     not null,
    status             platform.processing_status not null default 'Running',
    error_code         text,
    error_message      text,
    input_tokens       bigint      not null default 0,
    output_tokens      bigint      not null default 0,
    latency_ms         integer,
    provider_request_id text,
    started_at         timestamptz not null default now(),
    finished_at        timestamptz,

    constraint processing_attempt_number_positive check (attempt_no >= 1),
    constraint processing_attempt_tokens_not_negative check (input_tokens >= 0 and output_tokens >= 0),
    constraint processing_attempt_latency_not_negative check (latency_ms is null or latency_ms >= 0),
    constraint processing_attempt_run_number_key unique (run_id, attempt_no)
);

create index processing_attempt_run_idx on platform.processing_attempt (run_id, attempt_no);

-- -------------------------------------------------------------------------------------
-- extracted_clause — one clause, with its source location
-- -------------------------------------------------------------------------------------
create table platform.extracted_clause (
    id                  uuid primary key default gen_random_uuid(),
    run_id              uuid        not null references platform.processing_run (id) on delete cascade on update restrict,
    document_id         uuid        not null references platform.document (id) on delete restrict on update restrict,
    company_id          uuid        not null references app.company (id) on delete restrict on update restrict,
    ordinal             integer     not null,
    clause_number       text,
    heading             text,
    text                text        not null,
    page_from           integer     not null,
    page_to             integer     not null,
    source_char_start   integer,
    source_char_end     integer,
    source_boxes        jsonb,
    confidence          numeric(4,3),
    needs_review        boolean     not null default false,
    review_status       platform.clause_review_status not null default 'Pending',
    created_at          timestamptz not null default now(),

    constraint extracted_clause_ordinal_positive check (ordinal >= 1),
    constraint extracted_clause_text_not_blank check (btrim(text) <> ''),
    constraint extracted_clause_page_from_positive check (page_from >= 1),
    constraint extracted_clause_page_order check (page_to >= page_from),
    constraint extracted_clause_offsets_order
        check (source_char_start is null or source_char_end is null or source_char_end >= source_char_start),
    constraint extracted_clause_offsets_not_negative
        check ((source_char_start is null or source_char_start >= 0)
           and (source_char_end is null or source_char_end >= 0)),
    constraint extracted_clause_confidence_range
        check (confidence is null or (confidence >= 0 and confidence <= 1)),
    constraint extracted_clause_run_ordinal_key unique (run_id, ordinal)
);

create index extracted_clause_document_idx on platform.extracted_clause (document_id, ordinal);
create index extracted_clause_company_idx on platform.extracted_clause (company_id);

-- -------------------------------------------------------------------------------------
-- clause_category — global reference taxonomy (no company)
-- -------------------------------------------------------------------------------------
create table platform.clause_category (
    id          uuid primary key default gen_random_uuid(),
    key         text        not null,
    name        text        not null,
    description text,
    parent_id   uuid        references platform.clause_category (id) on delete restrict on update restrict,
    sort_order  integer     not null default 0,
    is_active   boolean     not null default true,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint clause_category_key_not_blank check (btrim(key) <> ''),
    constraint clause_category_name_not_blank check (btrim(name) <> ''),
    constraint clause_category_key_key unique (key)
);

create index clause_category_parent_idx on platform.clause_category (parent_id);
create index clause_category_sort_idx on platform.clause_category (sort_order, name);

create trigger clause_category_set_updated_at
    before update on platform.clause_category
    for each row execute function platform.set_updated_at();

comment on table platform.clause_category is
  'Configurable reference taxonomy. Global like comparison headings: one taxonomy for the product, managed with clause.manage.';

-- -------------------------------------------------------------------------------------
-- clause_classification — a clause may have several categories
-- -------------------------------------------------------------------------------------
create table platform.clause_classification (
    id             uuid primary key default gen_random_uuid(),
    clause_id      uuid        not null references platform.extracted_clause (id) on delete cascade on update restrict,
    category_id    uuid        not null references platform.clause_category (id) on delete restrict on update restrict,
    company_id     uuid        not null references app.company (id) on delete restrict on update restrict,
    confidence     numeric(4,3),
    is_primary     boolean     not null default false,
    rationale      text,
    model          text,
    prompt_version text,
    created_at     timestamptz not null default now(),

    constraint clause_classification_confidence_range
        check (confidence is null or (confidence >= 0 and confidence <= 1)),
    constraint clause_classification_clause_category_key unique (clause_id, category_id)
);

create index clause_classification_company_idx on platform.clause_classification (company_id);
create index clause_classification_category_idx on platform.clause_classification (category_id);

-- -------------------------------------------------------------------------------------
-- RLS: tenant-scoped reads and capability-gated writes
-- -------------------------------------------------------------------------------------
create policy processing_run_api_read on platform.processing_run
    for select to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy processing_run_authenticated_read on platform.processing_run
    for select to authenticated
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy processing_run_api_insert on platform.processing_run
    for insert to app_api
    with check (platform.is_active_user()
        and platform.can_access_company(company_id)
        and platform.has_capability('clause.process'));

create policy processing_run_api_update on platform.processing_run
    for update to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.process'))
    with check (platform.is_active_user()
        and platform.can_access_company(company_id)
        and platform.has_capability('clause.process'));

create policy processing_attempt_api_read on platform.processing_attempt
    for select to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy processing_attempt_authenticated_read on platform.processing_attempt
    for select to authenticated
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy processing_attempt_api_write on platform.processing_attempt
    for all to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.process'))
    with check (platform.is_active_user()
        and platform.can_access_company(company_id)
        and platform.has_capability('clause.process'));

create policy extracted_clause_api_read on platform.extracted_clause
    for select to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy extracted_clause_authenticated_read on platform.extracted_clause
    for select to authenticated
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy extracted_clause_api_write on platform.extracted_clause
    for all to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and (platform.has_capability('clause.process') or platform.has_capability('clause.review')))
    with check (platform.is_active_user()
        and platform.can_access_company(company_id)
        and (platform.has_capability('clause.process') or platform.has_capability('clause.review')));

create policy clause_category_api_read on platform.clause_category
    for select to app_api
    using (platform.is_active_user());

create policy clause_category_authenticated_read on platform.clause_category
    for select to authenticated
    using (platform.is_active_user());

create policy clause_category_api_write on platform.clause_category
    for all to app_api
    using (platform.is_active_user() and platform.has_capability('clause.manage'))
    with check (platform.is_active_user() and platform.has_capability('clause.manage'));

create policy clause_classification_api_read on platform.clause_classification
    for select to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy clause_classification_authenticated_read on platform.clause_classification
    for select to authenticated
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and platform.has_capability('clause.read'));

create policy clause_classification_api_write on platform.clause_classification
    for all to app_api
    using (platform.is_active_user()
       and platform.can_access_company(company_id)
       and (platform.has_capability('clause.process') or platform.has_capability('clause.review')))
    with check (platform.is_active_user()
        and platform.can_access_company(company_id)
        and (platform.has_capability('clause.process') or platform.has_capability('clause.review')));

-- -------------------------------------------------------------------------------------
-- Grants
-- -------------------------------------------------------------------------------------
grant select, insert, update, delete on
    platform.processing_run,
    platform.processing_attempt,
    platform.extracted_clause,
    platform.clause_category,
    platform.clause_classification
to app_api;

grant select on
    platform.processing_run,
    platform.processing_attempt,
    platform.extracted_clause,
    platform.clause_category,
    platform.clause_classification
to authenticated;

-- -------------------------------------------------------------------------------------
-- Capabilities
-- -------------------------------------------------------------------------------------
insert into platform.capability (key, description)
select v.key, v.description
from (values
    ('clause.read',    'Read processing runs, extracted clauses and classifications'),
    ('clause.process', 'Start and execute clause processing for a document'),
    ('clause.review',  'Correct extracted clauses and classifications'),
    ('clause.manage',  'Manage the clause taxonomy')
) as v(key, description)
where not exists (
    select 1 from platform.capability c where c.key = v.key
);

insert into platform.role_capability (role_id, capability_id)
select r.id, c.id
from (values
    ('superadmin',   'clause.read'),
    ('superadmin',   'clause.process'),
    ('superadmin',   'clause.review'),
    ('superadmin',   'clause.manage'),
    ('administrator','clause.read'),
    ('administrator','clause.process'),
    ('administrator','clause.review'),
    ('editor',       'clause.read'),
    ('editor',       'clause.process'),
    ('editor',       'clause.review'),
    ('transcriptor', 'clause.read'),
    ('transcriptor', 'clause.process'),
    ('transcriptor', 'clause.review')
) as v(role_key, capability_key)
join platform.role r       on r.key = v.role_key
join platform.capability c on c.key = v.capability_key
where not exists (
    select 1 from platform.role_capability rc
    where rc.role_id = r.id and rc.capability_id = c.id
);
