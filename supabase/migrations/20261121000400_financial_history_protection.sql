-- =====================================================================================
-- 20261121000400 — Financial history protection (P1-005 PR D)
--
-- Processing retention deletes attempts and runs. Those two foreign keys were `on delete cascade`, so a
-- retention sweep silently destroyed the financial ledger while the monthly aggregate survived. Financial
-- history is now protected: a run or attempt that has usage records cannot be deleted until a dedicated,
-- audited financial archival step removes them first.
-- =====================================================================================

alter table platform.ai_usage_record drop constraint ai_usage_record_run_id_fkey;
alter table platform.ai_usage_record
    add constraint ai_usage_record_run_id_fkey
    foreign key (run_id) references platform.processing_run (id) on delete restrict on update restrict;

alter table platform.ai_usage_record drop constraint ai_usage_record_attempt_id_fkey;
alter table platform.ai_usage_record
    add constraint ai_usage_record_attempt_id_fkey
    foreign key (attempt_id) references platform.processing_attempt (id) on delete restrict on update restrict;

comment on constraint ai_usage_record_run_id_fkey on platform.ai_usage_record is
    'Restrict: financial ledger rows are never removed by processing retention or a run cascade.';

comment on constraint ai_usage_record_attempt_id_fkey on platform.ai_usage_record is
    'Restrict: financial ledger rows are never removed by processing retention or an attempt cascade.';
