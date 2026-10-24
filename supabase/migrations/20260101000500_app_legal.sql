-- =====================================================================================
-- 20260101000500 — app: legal library
--
-- The legacy system had two parallel statute tables — ley_trabajo (one row, NO primary key) and
-- otras_leyes — with two parallel article tables whose article-number columns had different
-- types (int vs varchar). Folding them into one entity with a discriminator gives labour-law
-- articles the foreign key they never had (legacy-schema-issues DS-03, DS-23).
-- =====================================================================================

create table app.statute (
    id              uuid        primary key default gen_random_uuid(),
    kind            app.statute_kind not null,
    name            text        not null,
    published_on    date,
    source          text,
    document_id     uuid        references platform.document (id) on delete set null on update restrict,
    created_at      timestamptz not null default now(),
    created_by      uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at      timestamptz not null default now(),
    updated_by      uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint statute_name_not_blank check (btrim(name) <> '')
);

create unique index statute_name_key on app.statute (lower(name));

-- Exactly one labour statute may exist. The legacy module loaded it with `realcount() == 1` and
-- silently returned false when a second row appeared, breaking the whole screen.
create unique index statute_single_labour_law_key
    on app.statute (kind) where kind = 'LabourLaw';

create index statute_kind_idx on app.statute (kind, lower(name));

create trigger statute_set_updated_at
    before update on app.statute
    for each row execute function platform.set_updated_at();

comment on column app.statute.source is
  'Provenance of the statute text (gazette reference, URL). The legacy origen column, kept.';
comment on table app.statute is
  'Article counts are computed, never stored. The legacy total_articulos column was maintained by '
  'application code and updated with WHERE on the old value, so it silently drifted (DS-29).';

create table app.statute_article (
    id                       uuid        primary key default gen_random_uuid(),
    statute_id               uuid        not null references app.statute (id) on delete cascade on update restrict,
    number_int               integer     not null,
    number_suffix            text,
    title                    text        not null,
    summary                  text,
    body                     text        not null,
    heading_id               uuid        references app.comparison_heading (id) on delete restrict on update restrict,
    economic_comparison_note text,
    created_at               timestamptz not null default now(),
    created_by               uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at               timestamptz not null default now(),
    updated_by               uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint statute_article_number_positive      check (number_int >= 1),
    constraint statute_article_suffix_format        check (number_suffix is null or number_suffix ~ '^[A-Z]{1,2}$'),
    constraint statute_article_title_not_blank      check (btrim(title) <> ''),
    constraint statute_article_body_not_blank       check (btrim(body) <> '')
);

-- Clause-number uniqueness within a statute. The legacy labour-law table had a single COLUMN
-- primary key on the article number, so two statutes could never share article "1".
create unique index statute_article_number_key
    on app.statute_article (statute_id, number_int, coalesce(number_suffix, ''));

-- Ordering index. The legacy ordered by a varchar, producing 1, 10, 100, 11, 2.
create index statute_article_order_idx
    on app.statute_article (statute_id, number_int, number_suffix);

-- Comparison workspace reads articles by heading.
create index statute_article_heading_idx
    on app.statute_article (heading_id) where heading_id is not null;

create trigger statute_article_set_updated_at
    before update on app.statute_article
    for each row execute function platform.set_updated_at();

comment on column app.statute_article.heading_id is
  'NULLABLE, unlike app.agreement_clause.heading_id. 89.6% of other-statute articles carried no '
  'heading in the legacy data, so requiring classification would block loading the library '
  '(approval item A-12).';
comment on column app.statute_article.number_int is
  'Numeric part of the article number, for correct ordering. The legacy type was int in one table '
  'and varchar(11) in another for the same concept.';
