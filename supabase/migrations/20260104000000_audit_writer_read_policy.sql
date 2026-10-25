-- =====================================================================================
-- 20260104000000 — let the audit writer return its own row
--
-- The audit interceptor inserts through EF Core, which uses INSERT ... RETURNING id, occurred_at.
-- PostgreSQL applies SELECT policies to RETURNING, so an editor without `audit.read` could not write
-- the audit entry for their own change: the INSERT was rejected with
--     42501: new row violates row-level security policy for table "audit_entry"
-- even though the INSERT policy (is_active_user) passed.
--
-- The writer must be allowed to read back the row it just inserted. The actor is the caller, so the
-- narrow fix is "audit.read OR the row is mine". It does not give anyone the audit trail: the API's
-- GET /audit still requires audit.read, and this only exposes a caller's own entry at the database
-- layer.
--
-- Reversible:
--   drop policy if exists audit_entry_api_read on platform.audit_entry;
--   create policy audit_entry_api_read on platform.audit_entry
--       for select to app_api using (platform.has_capability('audit.read'));
-- =====================================================================================

drop policy if exists audit_entry_api_read on platform.audit_entry;

create policy audit_entry_api_read on platform.audit_entry
    for select to app_api
    using (
        platform.has_capability('audit.read')
        or actor_id = platform.current_user_id()
    );

comment on policy audit_entry_api_read on platform.audit_entry is
  'The API may read the audit trail with audit.read, and may read back a row it just wrote for the '
  'caller (INSERT ... RETURNING). A caller''s own entry is not the audit trail; GET /audit still '
  'requires audit.read.';
