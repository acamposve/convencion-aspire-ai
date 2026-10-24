-- =====================================================================================
-- 20260102000000 — align the schema with the Product Owner's decisions
--
-- Implements the outstanding deltas from Task 6 (recorded in docs/architecture/decisions.md §7).
-- Forward-only: the eleven merged migrations are never edited (constitution Art. VIII.6).
--
--   Q-2  clause numbers are plain integers      -> number_suffix dropped from five tables
--   Q-4  five negotiation stages                -> enum replaced; "Promoted" is no longer a state
--   Q-1  agreement title composed from company + period -> title and number columns dropped
--
-- Q-9 (five roles) is reference data and therefore lives in seed.sql, which runs after migrations.
--
-- No data migration is required: no environment has ever held a business row outside a disposable
-- container, and the negotiation tables have always been empty.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- Q-2 — clause numbers are integers
--
-- The unique and ordering indexes are dropped before the column so that PostgreSQL never has to
-- rebuild an index over a column being removed.
-- -------------------------------------------------------------------------------------

-- agreement_clause
drop index if exists app.agreement_clause_number_key;
drop index if exists app.agreement_clause_order_idx;
alter table app.agreement_clause drop constraint if exists agreement_clause_suffix_format;
alter table app.agreement_clause drop column number_suffix;

create unique index agreement_clause_number_key
    on app.agreement_clause (agreement_id, number_int);
create index agreement_clause_order_idx
    on app.agreement_clause (agreement_id, number_int);

-- statute_article
drop index if exists app.statute_article_number_key;
drop index if exists app.statute_article_order_idx;
alter table app.statute_article drop constraint if exists statute_article_suffix_format;
alter table app.statute_article drop column number_suffix;

create unique index statute_article_number_key
    on app.statute_article (statute_id, number_int);
create index statute_article_order_idx
    on app.statute_article (statute_id, number_int);

-- union_petition
drop index if exists app.union_petition_number_key;
alter table app.union_petition drop constraint if exists union_petition_suffix_format;
alter table app.union_petition drop column number_suffix;

create unique index union_petition_number_key
    on app.union_petition (case_id, number_int);

-- employer_offer
drop index if exists app.employer_offer_number_key;
alter table app.employer_offer drop constraint if exists employer_offer_suffix_format;
alter table app.employer_offer drop column number_suffix;

create unique index employer_offer_number_key
    on app.employer_offer (petition_id, number_int);

-- meeting_agreement
drop index if exists app.meeting_agreement_number_key;
alter table app.meeting_agreement drop constraint if exists meeting_agreement_suffix_format;
alter table app.meeting_agreement drop column number_suffix;

create unique index meeting_agreement_number_key
    on app.meeting_agreement (case_id, number_int);

comment on column app.agreement_clause.number_int is
  'Clause number, integer only (decision Q-2). Uniqueness is (agreement_id, number_int); ordering '
  'is numeric, which the legacy varchar column made impossible.';

-- -------------------------------------------------------------------------------------
-- Q-4 — five negotiation stages: Open, InProgress, Pending, Agreed, Closed
--
-- The two CHECK constraints must be dropped before the column type changes, because they compare
-- the column against the literal 'Promoted', which the new type does not contain. Triggers are not
-- re-validated on ALTER, but the guard function body is replaced below for the same reason.
-- -------------------------------------------------------------------------------------

alter table app.negotiation_case drop constraint if exists negotiation_case_closed_consistency;
alter table app.negotiation_case drop constraint if exists negotiation_case_promoted_consistency;
drop trigger if exists negotiation_case_promotion_guard on app.negotiation_case;

alter table app.negotiation_case alter column state drop default;

alter type app.negotiation_case_state rename to negotiation_case_state_legacy;

create type app.negotiation_case_state as enum
    ('Open', 'InProgress', 'Pending', 'Agreed', 'Closed');

alter table app.negotiation_case
    alter column state type app.negotiation_case_state
    using (
        case state::text
            when 'Draft'    then 'Open'
            when 'Open'     then 'Open'
            when 'Closed'   then 'Closed'
            when 'Promoted' then 'Closed'
        end
    )::app.negotiation_case_state;

drop type app.negotiation_case_state_legacy;

alter table app.negotiation_case alter column state set default 'Open';

comment on type app.negotiation_case_state is
  'Open -> InProgress -> Pending -> Agreed -> Closed (decision Q-4). "Promoted" is no longer a '
  'state: promotion is recorded by promoted_agreement_id while the case rests in Closed.';

alter table app.negotiation_case
    add constraint negotiation_case_closed_consistency check (
        (state = 'Closed') = (closed_at is not null)
    ),
    add constraint negotiation_case_promoted_consistency check (
        promoted_agreement_id is null or state = 'Closed'
    );

comment on column app.negotiation_case.promoted_agreement_id is
  'The agreement produced by this case. Setting it requires negotiation.promote (see the guard '
  'trigger) and implies state = Closed.';

-- The guard now watches the promotion outcome rather than a state name, because there is no
-- "Promoted" state any more.
create or replace function app.guard_case_promotion()
returns trigger
language plpgsql
as $$
begin
    if new.promoted_agreement_id is not null
       and old.promoted_agreement_id is null
       and not platform.has_capability('negotiation.promote') then
        raise exception
            'capability negotiation.promote is required to promote a negotiation case'
            using errcode = 'insufficient_privilege';
    end if;
    return new;
end;
$$;

create trigger negotiation_case_promotion_guard
    before update of promoted_agreement_id on app.negotiation_case
    for each row execute function app.guard_case_promotion();

comment on function app.guard_case_promotion() is
  'Promotion requires negotiation.promote at the database layer, not only in the API.';

-- -------------------------------------------------------------------------------------
-- Q-1 — agreement title is composed from company + validity period
--
-- The title was stored, NOT NULL and user-entered. It is now derived on read, so it cannot drift
-- from the company and dates it claims to describe. Identity is unchanged and still enforced by
-- UNIQUE (company_id, valid_from).
-- -------------------------------------------------------------------------------------

alter table app.agreement drop constraint if exists agreement_title_not_blank;
alter table app.agreement drop column title;
alter table app.agreement drop column number;

comment on table app.agreement is
  'An agreement is identified by (company_id, valid_from) — decision Q-1: the display title is '
  'composed on read from the company name and the validity period, never stored.';
