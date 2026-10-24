-- =====================================================================================
-- 20260102000100 — document ownership, tenant isolation and processing state
--
-- Implements the approved proposal docs/proposals/document-workflow-schema.md §B.1, §B.2, §B.3,
-- with the tenant model the Product Owner chose:
--
--   company membership constrains every user; a dedicated capability unlocks cross-company access.
--   It is an explicit, auditable grant — never a consequence of lacking a company.
--
--   WHY `company.read_all` AND NOT `company.read`. The approved decision named `company.read` as
--   the unlock. That capability already exists and means "read the company catalogue": the seed
--   grants it to ALL five roles. Using it here made the predicate always true, and the very first
--   run of the cross-tenant tests caught every company user reading every other company's
--   documents (7 assertions failed). `company.read_all` was therefore introduced as a distinct
--   capability, granted only to SuperAdmin and Admin. This is a semantic correction to the
--   approved decision, recorded for review.
--
-- The predicate fails closed: a document with no company is readable by nobody through the API.
-- That is deliberate. Attaching a document to a company is what makes it visible at all, so an
-- unattributed upload cannot become a shared-space leak.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- Tenant helpers
--
-- SECURITY DEFINER with a pinned search_path: they read identity tables from inside RLS policies
-- on those same tables, so definer rights are required to avoid recursive policy evaluation.
-- -------------------------------------------------------------------------------------

create or replace function platform.current_company_id()
returns uuid
language sql
stable
security definer
set search_path = platform, pg_temp
as $$
  select up.company_id
  from platform.user_profile up
  where up.user_id = platform.current_user_id();
$$;

comment on function platform.current_company_id() is
  'The company of the caller''s profile, or NULL when the caller has none.';

create or replace function platform.can_access_company(p_company_id uuid)
returns boolean
language sql
stable
security definer
set search_path = platform, pg_temp
as $$
  select coalesce(
      p_company_id is not null
      and (
          p_company_id = (
              select up.company_id
              from platform.user_profile up
              where up.user_id = platform.current_user_id()
          )
          or platform.has_capability('company.read_all')
      ),
      false);
$$;

comment on function platform.can_access_company(uuid) is
  'Tenant predicate. True only when the company is non-null AND (it is the caller''s own company OR '
  'the caller holds company.read_all). A null company is denied, so an unattached document is '
  'visible to nobody through the API.';

grant execute on function platform.current_company_id()          to authenticated, app_api;
grant execute on function platform.can_access_company(uuid)      to authenticated, app_api;

-- -------------------------------------------------------------------------------------
-- B.1 — a document belongs to a company
-- -------------------------------------------------------------------------------------

alter table platform.document
    add column company_id uuid;

alter table platform.document
    add constraint document_company_fk
        foreign key (company_id) references app.company (id) on delete restrict on update restrict;

create index document_company_idx
    on platform.document (company_id) where deleted_at is null;

comment on column platform.document.company_id is
  'Owning company. Nullable at the schema level for non-tenant artefacts (statute PDFs, news '
  'images), but the API requires it for contract documents and every read/write policy denies a '
  'null company.';

-- -------------------------------------------------------------------------------------
-- B.2 — processing state must be auditable
--
-- Without these, "controlled retries" and traceability are impossible and a failed extraction is
-- indistinguishable from one that never ran.
-- -------------------------------------------------------------------------------------

alter table platform.document
    add column extraction_attempts     integer     not null default 0,
    add column extraction_started_at   timestamptz,
    add column extraction_completed_at timestamptz,
    add column extraction_error        text;

alter table platform.document
    add constraint document_extraction_attempts_non_negative
        check (extraction_attempts >= 0),
    add constraint document_extraction_error_only_on_failure
        check (extraction_error is null or extraction_status = 'Failed'),
    add constraint document_extraction_completion_requires_start
        check (extraction_completed_at is null or extraction_started_at is not null);

comment on column platform.document.extraction_attempts is
  'Number of extraction attempts made. Bounded retries are the caller''s policy; this column makes '
  'the count observable and prevents an unbounded loop from being invisible.';
comment on column platform.document.extraction_error is
  'Operator-readable failure reason. Only permitted while extraction_status = Failed, so a success '
  'cannot retain a stale error.';

-- -------------------------------------------------------------------------------------
-- B.3 — extracted text lives beside the metadata, not on it
--
-- A document listing selects metadata for every row; storing hundreds of kilobytes of text on
-- platform.document would make every listing pull the text whether or not it is needed.
-- -------------------------------------------------------------------------------------

create table platform.document_text (
    document_id  uuid        primary key
                             references platform.document (id) on delete cascade on update restrict,
    content      text        not null,
    char_count   integer     not null,
    extracted_at timestamptz not null default now(),

    constraint document_text_content_not_blank check (length(content) > 0),
    -- char_count is stored AND constrained to agree with the content, so it cannot drift. The legacy
    -- schema stored counters maintained only by application code, and they went wrong (DS-29).
    constraint document_text_char_count_matches check (char_count = length(content))
);

comment on table platform.document_text is
  'Normalised extracted text, one row per document. Absence of a row means no text was extracted — '
  'which is not the same as a failed extraction, and the two are reported differently.';

grant select on platform.document_text to authenticated;
grant select, insert, update, delete on platform.document_text to app_api;

alter table platform.document_text enable row level security;

-- -------------------------------------------------------------------------------------
-- RLS — replace the document policies with tenant-scoped ones
--
-- The previous policies scoped by role and account state only, so any two active users could read
-- each other's documents. These scopes by company as well.
-- -------------------------------------------------------------------------------------

drop policy if exists document_api_read            on platform.document;
drop policy if exists document_authenticated_read  on platform.document;
drop policy if exists document_api_insert          on platform.document;
drop policy if exists document_api_update          on platform.document;
drop policy if exists document_api_delete          on platform.document;

create policy document_api_read on platform.document
    for select to app_api
    using (platform.is_active_user() and platform.can_access_company(company_id));

create policy document_authenticated_read on platform.document
    for select to authenticated
    using (platform.is_active_user() and platform.can_access_company(company_id));

create policy document_api_insert on platform.document
    for insert to app_api
    with check (
        platform.is_active_user()
        and platform.has_capability('document.write')
        and platform.can_access_company(company_id)
    );

create policy document_api_update on platform.document
    for update to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('document.write')
        and platform.can_access_company(company_id)
    )
    with check (
        platform.is_active_user()
        and platform.has_capability('document.write')
        and platform.can_access_company(company_id)
    );

create policy document_api_delete on platform.document
    for delete to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('document.write')
        and platform.can_access_company(company_id)
    );

-- Extracted text inherits the visibility of its document. The EXISTS subquery is itself filtered by
-- the policy above, so a caller who cannot see the document cannot see its text either.
create policy document_text_api_read on platform.document_text
    for select to app_api
    using (
        platform.is_active_user()
        and exists (
            select 1 from platform.document d
            where d.id = document_text.document_id
              and platform.can_access_company(d.company_id)
        )
    );

create policy document_text_authenticated_read on platform.document_text
    for select to authenticated
    using (
        platform.is_active_user()
        and exists (
            select 1 from platform.document d
            where d.id = document_text.document_id
              and platform.can_access_company(d.company_id)
        )
    );

create policy document_text_api_write on platform.document_text
    for all to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('document.write')
        and exists (
            select 1 from platform.document d
            where d.id = document_text.document_id
              and platform.can_access_company(d.company_id)
        )
    )
    with check (
        platform.is_active_user()
        and platform.has_capability('document.write')
        and exists (
            select 1 from platform.document d
            where d.id = document_text.document_id
              and platform.can_access_company(d.company_id)
        )
    );
