-- =====================================================================================
-- 20261121000600 — Restrict the document-deletion helpers to app_api (P1-004 follow-up)
--
-- The two SECURITY DEFINER helpers created in 20261121000500 kept PostgreSQL's default EXECUTE grant
-- to PUBLIC. `authenticated` has USAGE on schema platform, so any signed-in user could call them:
--   * claim_document_object_deletions returns storage_path for every tenant's pending deletions and
--     leases those rows (cross-tenant disclosure, and it can delay real deletions);
--   * record_document_object_deletion can mark any document's deletion Completed without the object
--     being removed, silently suppressing the retry.
--
-- Execution is now limited to the API role. The function definitions and their
-- `set search_path = platform, pg_temp` are unchanged.
-- =====================================================================================

revoke all on function platform.claim_document_object_deletions(integer, integer) from public;
revoke all on function platform.record_document_object_deletion(uuid, boolean, text) from public;

grant execute on function platform.claim_document_object_deletions(integer, integer) to app_api;
grant execute on function platform.record_document_object_deletion(uuid, boolean, text) to app_api;
