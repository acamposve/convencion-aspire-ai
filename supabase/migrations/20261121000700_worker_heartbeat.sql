-- =====================================================================================
-- 20261121000700 — Worker heartbeat (P1-009)
--
-- A stopped worker was invisible: the API only knew whether the database answered, and the worker's
-- health file lives inside its own container. This singleton row is written by the worker on startup
-- (including when disabled) and on every poll, so the API can report the age of the last heartbeat.
--
-- Reads are intentionally claim-free: /health/ops is served before any user session exists, so a
-- policy keyed on is_active_user() would make the signal unreadable exactly when it matters. The row
-- carries timestamps and an enabled flag only; no tenant or document data.
-- =====================================================================================

create table platform.worker_heartbeat (
    id           integer     primary key default 1,
    last_seen_at timestamptz not null default now(),
    enabled      boolean     not null,
    updated_at   timestamptz not null default now(),

    constraint worker_heartbeat_singleton check (id = 1)
);

alter table platform.worker_heartbeat enable row level security;

grant select on platform.worker_heartbeat to app_api;

create policy worker_heartbeat_api_read on platform.worker_heartbeat
    for select to app_api
    using (true);

comment on table platform.worker_heartbeat is
    'Singleton liveness row written by the processing worker; read by GET /health/ops.';
