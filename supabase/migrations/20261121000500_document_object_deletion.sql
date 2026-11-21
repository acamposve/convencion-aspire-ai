-- =====================================================================================
-- 20261121000500 â€” Durable document object deletion (P1-004)
--
-- The API committed the soft delete and only then asked storage to remove the object. A storage
-- failure left a hidden document with a private object still stored, and nothing to retry. This table
-- is the durable intent: it is written in the same transaction as the soft delete, carries the retry
-- state, and is completed only when the object is actually gone (or storage reported 404).
--
-- Reads are tenant-scoped. The mutation path used by the API request and by the JWT-less sweeper is a
-- pair of SECURITY DEFINER functions: the sweeper has no user claims, so an RLS policy keyed on
-- is_active_user() would block it, and widening the policy itself would be worse than granting execute
-- on two narrow functions.
-- =====================================================================================

create table platform.document_object_deletion (
    document_id     uuid        primary key references platform.document (id) on delete cascade on update restrict,
    company_id      uuid        not null references app.company (id) on delete restrict on update restrict,
    storage_path    text        not null,
    state           text        not null default 'Pending',
    attempts        integer     not null default 0,
    next_attempt_at timestamptz not null default now(),
    last_error      text,
    requested_by    uuid        references platform.user_account (id) on delete set null on update restrict,
    created_at      timestamptz not null default now(),
    completed_at    timestamptz,

    constraint document_object_deletion_state_valid     check (state in ('Pending', 'Completed')),
    constraint document_object_deletion_completed_valid check ((state = 'Completed') = (completed_at is not null)),
    constraint document_object_deletion_attempts_valid  check (attempts >= 0),
    constraint document_object_deletion_path_not_blank  check (btrim(storage_path) <> '')
);

create index document_object_deletion_due
    on platform.document_object_deletion (next_attempt_at)
    where state = 'Pending';

-- Claims up to p_batch due rows for one sweeper pass and leases them for p_lease_seconds, so two
-- instances cannot process the same row inside the same window.
create function platform.claim_document_object_deletions(p_batch integer, p_lease_seconds integer)
returns table (document_id uuid, storage_path text, attempts integer)
language plpgsql
security definer
set search_path = platform, pg_temp
as $$
begin
    return query
    with due as (
        select d.document_id
        from platform.document_object_deletion d
        where d.state = 'Pending' and d.next_attempt_at <= now()
        order by d.next_attempt_at
        limit greatest(p_batch, 1)
        for update skip locked
    )
    update platform.document_object_deletion target
    set next_attempt_at = now() + make_interval(secs => greatest(p_lease_seconds, 30))
    from due
    where target.document_id = due.document_id
    returning target.document_id, target.storage_path, target.attempts;
end;
$$;

-- Records the outcome. Success completes the row; failure increments attempts and schedules the next
-- attempt with exponential backoff: 1 minute, 2, 4 â€¦ capped at 60 minutes.
create function platform.record_document_object_deletion(
    p_document_id uuid,
    p_succeeded boolean,
    p_error text)
returns void
language plpgsql
security definer
set search_path = platform, pg_temp
as $$
begin
    if p_succeeded then
        update platform.document_object_deletion
        set state = 'Completed',
            attempts = attempts + 1,
            completed_at = now(),
            last_error = null
        where document_id = p_document_id;
        return;
    end if;

    update platform.document_object_deletion
    set attempts = attempts + 1,
        last_error = nullif(btrim(coalesce(p_error, '')), ''),
        next_attempt_at = now() + make_interval(
            mins => least(power(2, attempts + 1)::integer, 60))
    where document_id = p_document_id;
end;
$$;

alter table platform.document_object_deletion enable row level security;

grant select, insert, update on platform.document_object_deletion to app_api;
grant select on platform.document_object_deletion to authenticated;
grant execute on function platform.claim_document_object_deletions(integer, integer) to app_api;
grant execute on function platform.record_document_object_deletion(uuid, boolean, text) to app_api;

create policy document_object_deletion_api_read on platform.document_object_deletion
    for select to app_api
    using (platform.is_active_user()
    and platform.can_access_company(company_id)
    and (platform.has_capability('usage.read') or platform.has_capability('document.write')));

create policy document_object_deletion_authenticated_read on platform.document_object_deletion
    for select to authenticated
    using (platform.is_active_user()
    and platform.can_access_company(company_id)
    and (platform.has_capability('usage.read') or platform.has_capability('document.write')));

create policy document_object_deletion_api_insert on platform.document_object_deletion
    for insert to app_api
    with check (platform.is_active_user()
    and platform.can_access_company(company_id)
    and platform.has_capability('document.write'));

-- The API request path updates through this policy; the sweeper uses the SECURITY DEFINER functions.
create policy document_object_deletion_api_update on platform.document_object_deletion
    for update to app_api
    using (platform.is_active_user()
    and platform.can_access_company(company_id)
    and platform.has_capability('document.write'))
    with check (platform.is_active_user()
    and platform.can_access_company(company_id)
    and platform.has_capability('document.write'));
