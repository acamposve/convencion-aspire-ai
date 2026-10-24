-- =====================================================================================
-- 20260101000200 — platform: document metadata and audit trail
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- document
--
-- Files live in Supabase Storage (approved architecture: ADR-017). This table holds metadata
-- only. The legacy system stored the client-supplied filename as the path, accepted the
-- client-declared MIME type, chmod 0777'd the file and served it from a web-reachable
-- directory (security-findings §S7). Every one of those is closed here:
--   * storage_path is server-generated and unique, and is NOT the client file name
--   * content_type is constrained to a three-value allow-list (the API must byte-sniff it)
--   * size is bounded in the database as well as in the API
--   * nothing here is web-reachable
-- -------------------------------------------------------------------------------------
create table platform.document (
    id                 uuid        primary key default gen_random_uuid(),
    storage_path       text        not null,
    file_name          text        not null,
    content_type       text        not null,
    size_bytes         bigint      not null,
    checksum_sha256    char(64),
    extraction_status  platform.extraction_status not null default 'NotRequested',
    uploaded_by        uuid        not null references platform.user_account (id) on delete restrict on update restrict,
    uploaded_at        timestamptz not null default now(),
    deleted_at         timestamptz,
    created_at         timestamptz not null default now(),
    updated_at         timestamptz not null default now(),

    constraint document_storage_path_not_blank check (btrim(storage_path) <> ''),
    constraint document_file_name_not_blank    check (btrim(file_name) <> ''),
    constraint document_content_type_allowed   check (content_type in ('application/pdf', 'image/png', 'image/jpeg')),
    -- 20 MiB, matching the API limit (approval item A-28) and supabase/config.toml file_size_limit
    constraint document_size_within_limit      check (size_bytes > 0 and size_bytes <= 20971520),
    constraint document_checksum_format        check (checksum_sha256 is null or checksum_sha256 ~ '^[0-9a-f]{64}$')
);

create unique index document_storage_path_key on platform.document (storage_path);

-- Partial index: active documents are the ones queried; soft-deleted ones are only for audit.
create index document_active_idx on platform.document (uploaded_at desc) where deleted_at is null;
create index document_uploaded_by_idx on platform.document (uploaded_by);

create trigger document_set_updated_at
    before update on platform.document
    for each row execute function platform.set_updated_at();

comment on column platform.document.extraction_status is
  'Reserved seam for future PDF text extraction (ADR-018). Always NotRequested in the MVP; it '
  'exists so adding the state later is not a schema migration.';
comment on column platform.document.deleted_at is
  'Soft delete, justified here only: audit entries and agreement history may reference a document '
  'after it is removed from storage. Business tables use hard constraints instead.';
comment on column platform.document.content_type is
  'Must be derived from file bytes by the API, never from the client-declared Content-Type or '
  'extension. The CHECK is a second line of defence, not the control.';

-- -------------------------------------------------------------------------------------
-- audit_entry
--
-- Append-only event log of meaningful state changes. Two mechanisms exist for auditing:
--   1. created_at/created_by/updated_at/updated_by columns on every business table
--   2. this table, for events worth reading later (publish, state transitions, grants)
--
-- The legacy system wrote a login audit into usuarios_login, a table that did not exist, so
-- every write failed silently (DS-32). Here the table exists and the writes cannot be undone.
-- -------------------------------------------------------------------------------------
create table platform.audit_entry (
    id              bigint      generated always as identity primary key,
    occurred_at     timestamptz not null default now(),
    actor_id        uuid        references platform.user_account (id) on delete set null on update restrict,
    module          text        not null,
    entity_type     text        not null,
    entity_id       text        not null,
    action          text        not null,
    from_state      text,
    to_state        text,
    changes         jsonb,
    correlation_id  text,

    constraint audit_entry_module_allowed check (module in
        ('catalog', 'agreements', 'negotiation', 'legal_library', 'comparison',
         'publishing', 'identity', 'documents', 'audit')),
    constraint audit_entry_entity_type_not_blank check (btrim(entity_type) <> ''),
    constraint audit_entry_entity_id_not_blank   check (btrim(entity_id) <> ''),
    constraint audit_entry_action_not_blank      check (btrim(action) <> ''),
    constraint audit_entry_changes_is_object     check (changes is null or jsonb_typeof(changes) = 'object')
);

create index audit_entry_entity_idx   on platform.audit_entry (entity_type, entity_id, occurred_at desc);
create index audit_entry_actor_idx    on platform.audit_entry (actor_id, occurred_at desc);
create index audit_entry_occurred_idx on platform.audit_entry (occurred_at desc);
create index audit_entry_module_idx   on platform.audit_entry (module, occurred_at desc);

-- Append-only is enforced by the database, not by the absence of a handler.
create trigger audit_entry_append_only
    before update or delete on platform.audit_entry
    for each row execute function platform.reject_mutation();

comment on table platform.audit_entry is
  'Append-only. INSERT is part of the same transaction as the change it records, so a rolled-back '
  'operation leaves no audit row. No update or delete path exists, including for administrators.';
