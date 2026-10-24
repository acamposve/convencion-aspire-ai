-- =====================================================================================
-- 20260101000800 — app: publishing (public portal news)
--
-- The legacy system published news with estatus_noticia varchar(11) holding 'Pendiente' or
-- 'Publicar', and the create handler hardcoded 'Pendiente' regardless of the submitted value, so
-- publishing required a create followed by an edit (BR-25). A typed state and a consistency CHECK
-- remove that failure mode.
-- =====================================================================================

create table app.news_category (
    id          uuid        primary key default gen_random_uuid(),
    name        text        not null,
    description text,
    sort_order  integer     not null default 0,
    is_active   boolean     not null default true,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint news_category_name_not_blank check (btrim(name) <> '')
);

create unique index news_category_name_key on app.news_category (lower(name));
create index news_category_order_idx on app.news_category (sort_order, name);

create trigger news_category_set_updated_at
    before update on app.news_category
    for each row execute function platform.set_updated_at();

create table app.news_article (
    id                          uuid        primary key default gen_random_uuid(),
    category_id                 uuid        not null references app.news_category (id) on delete restrict on update restrict,
    title                       text        not null,
    summary                     text,
    body                        text        not null,
    state                       app.publication_state not null default 'Draft',
    published_at                timestamptz,
    published_by                uuid        references platform.user_account (id) on delete restrict on update restrict,
    primary_image_document_id   uuid        references platform.document (id) on delete set null on update restrict,
    secondary_image_document_id uuid        references platform.document (id) on delete set null on update restrict,

    created_at                  timestamptz not null default now(),
    created_by                  uuid        references platform.user_account (id) on delete set null on update restrict,
    updated_at                  timestamptz not null default now(),
    updated_by                  uuid        references platform.user_account (id) on delete set null on update restrict,

    constraint news_article_title_not_blank check (btrim(title) <> ''),
    constraint news_article_body_not_blank  check (btrim(body) <> ''),

    constraint news_article_publication_consistency check (
        (state = 'Published' and published_at is not null and published_by is not null)
        or
        (state = 'Draft'     and published_at is null     and published_by is null)
    )
);

create index news_article_published_idx
    on app.news_article (published_at desc) where state = 'Published';
create index news_article_category_idx on app.news_article (category_id);

create trigger news_article_set_updated_at
    before update on app.news_article
    for each row execute function platform.set_updated_at();

comment on column app.news_article.body is
  'Sanitised rich text, as with agreement clauses.';

-- =====================================================================================
-- Comparison query support
--
-- The comparison workspace is the product's read surface: given a heading and a set of published
-- agreements, project their clauses in clause order. This materialised view is not used by the
-- MVP (the API projects with EF Core), but the index that query needs is declared above
-- (agreement_clause_heading_idx) and the published filter is covered by agreement_published_idx.
-- No additional index is added without a measured need.
-- =====================================================================================

comment on column app.agreement.state is
  'Filtering on this column plus agreement_clause.heading_id is the comparison query shape; both '
  'are indexed. A composite partial index was considered and rejected as unmeasured.';
