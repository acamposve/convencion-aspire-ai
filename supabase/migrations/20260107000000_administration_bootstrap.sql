-- =====================================================================================
-- 20260107 — T13: the one-time bootstrap, as one atomic database function
--
-- The window ("no user exists") is a property of the table, not of the caller. Expressing it as a
-- per-table INSERT policy is possible for user_account, but not for the three rows that must accompany
-- it: once the account is inserted, has_any_user() is already true inside the same transaction, so the
-- profile, the role grant and the audit row would each need a looser rule. A looser rule is exactly what
-- keeps an open registration door.
--
-- So the whole provisioning is one SECURITY DEFINER function: it takes an exclusive lock on the account
-- table (two concurrent bootstraps cannot both see zero), refuses when any account already exists, and
-- writes the account, its empty profile, the SuperAdmin grant and the audit row in one statement. The
-- function is the only path that can create the first user, and it is inert after that.
-- =====================================================================================

create or replace function platform.bootstrap_first_admin(
    p_subject      uuid,
    p_email        text,
    p_display_name text,
    p_role_key     text default 'superadmin')
returns table (id uuid, created_at timestamptz)
language plpgsql
security definer
set search_path = platform, pg_temp
as $$
declare
    v_role     uuid;
    v_existing integer;
    v_created  timestamptz;
begin
    if p_subject is null or btrim(coalesce(p_email, '')) = '' or btrim(coalesce(p_display_name, '')) = '' then
        raise exception 'subject, email and display name are required'
            using errcode = 'invalid_parameter_value';
    end if;

    -- Serialise concurrent bootstrap attempts, then re-check under the lock.
    lock table platform.user_account in exclusive mode;

    select count(*) into v_existing from platform.user_account;

    if v_existing > 0 then
        raise exception 'the system is already configured; the bootstrap window is closed'
            using errcode = 'invalid_parameter_value';
    end if;

    select r.id into v_role from platform.role r where r.key = p_role_key;

    if v_role is null then
        raise exception 'role % does not exist', p_role_key
            using errcode = 'invalid_parameter_value';
    end if;

    insert into platform.user_account (id, email, display_name)
    values (p_subject, p_email, p_display_name)
    returning user_account.created_at into v_created;

    insert into platform.user_profile (user_id) values (p_subject);

    insert into platform.user_role (user_id, role_id) values (p_subject, v_role);

    insert into platform.audit_entry (actor_id, module, entity_type, entity_id, action, changes)
    values (
        null,
        'identity',
        'user_account',
        p_subject::text,
        'created',
        jsonb_build_object('roles', jsonb_build_array(p_role_key), 'bootstrap', true)
    );

    return query select p_subject, v_created;
end;
$$;

comment on function platform.bootstrap_first_admin(uuid, text, text, text) is
  'Creates the single SuperAdmin exactly once (constitution Art. VI.6, T13 AC 1). Exclusive-locks the '
  'account table so two concurrent calls cannot both succeed, and writes the account, profile, role '
  'grant and audit row atomically. Inert after the first account exists.';

revoke all on function platform.bootstrap_first_admin(uuid, text, text, text) from public;
grant execute on function platform.bootstrap_first_admin(uuid, text, text, text) to app_api;

-- The API role still needs to read the role it grants and to report the window. Both were already
-- granted; role_api_read is now exercised with no claims by the bootstrap endpoint, so the window
-- clause is added explicitly instead of relying on is_active_user() (which requires a session).
drop policy if exists role_api_read on platform.role;

create policy role_api_read on platform.role
    for select to app_api
    using (platform.is_active_user() or not platform.has_any_user());
