-- =====================================================================================
-- 20260103000000 — documents are PDF only, enforced by the database
--
-- The API rejects every other format with 415 (T4). This makes the database refuse them too: the
-- constraint previously permitted application/pdf, image/png and image/jpeg, which was looser than
-- the rule and would have accepted a row written by a bug.
--
-- BEFORE APPLYING, this was run against every environment:
--
--     select content_type, count(*)
--     from platform.document
--     group by content_type
--     order by 2 desc;
--
--     content_type | count
--     -------------+-------
--     (0 rows)
--
-- No environment holds a document row, so no data is modified or deleted by this migration. If a
-- database ever did hold a non-PDF row, adding the constraint would FAIL rather than delete it:
-- PostgreSQL validates existing rows. That is the intended behaviour — a migration must not silently
-- discard data to satisfy a new rule.
--
-- REVERSE (there is no down-migration runner; this is the exact statement to revert):
--
--     alter table platform.document drop constraint if exists document_content_type_pdf_only;
--     alter table platform.document add constraint document_content_type_allowed
--         check (content_type in ('application/pdf', 'image/png', 'image/jpeg'));
--
-- =====================================================================================

alter table platform.document
    drop constraint if exists document_content_type_allowed;

alter table platform.document
    add constraint document_content_type_pdf_only
        check (content_type = 'application/pdf');

comment on column platform.document.content_type is
  'Always application/pdf. The API rejects every other format with 415 before anything is stored '
  '(T4), and this constraint makes the database refuse one written by any other route.';
