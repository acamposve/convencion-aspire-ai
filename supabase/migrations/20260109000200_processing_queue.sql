-- =====================================================================================
-- 20260109000200 — processing queue metadata
--
-- The worker claims Queued runs with FOR UPDATE SKIP LOCKED. A retry needs a future
-- availability time, and crash recovery needs a lease so a dead worker's run can be reclaimed.
-- =====================================================================================

alter table platform.processing_run
    add column available_at     timestamptz not null default now(),
    add column lease_expires_at timestamptz;

create index processing_run_queue_idx
    on platform.processing_run (available_at, created_at)
    where status = 'Queued';

comment on column platform.processing_run.available_at is
  'Earliest time the worker may claim this Queued run; retries push it forward.';
comment on column platform.processing_run.lease_expires_at is
  'Set while Running. Recovery requeues (or fails) a running row whose lease has expired.';
