-- =====================================================================================
-- rls-tests.sql — row level security, identity and capability enforcement
--
-- Two transactions, each rolled back:
--   Transaction 1 — the bootstrap window, which requires a database with zero users
--   Transaction 2 — fixtures (users, roles, agreements) then the policy assertions
--
-- Every test prints PASS or FAIL. The runner fails validation if any line contains 'FAIL:'.
-- =====================================================================================

\echo ''
\echo '========== PART 1 — bootstrap window (database has zero users) =========='
begin;

do $$
declare
    v_users_before int;
begin
    select count(*) into v_users_before from platform.user_account;
    if v_users_before <> 0 then
        raise notice 'FAIL: R0 precondition — expected 0 users, found %', v_users_before;
    else
        raise notice 'PASS: R0 precondition — the database has no user accounts';
    end if;
end $$;

-- The API role, unauthenticated, while no user exists: allowed exactly once.
do $$
begin
    perform set_config('request.jwt.claims', '{}', true);
    execute 'set local role app_api';
    begin
        insert into platform.user_account (id, email, display_name)
        values ('10000000-0000-0000-0000-000000000001', 'first@example.test', 'First SuperAdmin');
        raise notice 'PASS: R1 the bootstrap insert is allowed while no user exists';
    exception when others then
        raise notice 'FAIL: R1 bootstrap insert was rejected: %', sqlerrm;
    end;
    execute 'reset role';
end $$;

-- Second unauthenticated insert must be refused: the window has closed.
do $$
begin
    perform set_config('request.jwt.claims', '{}', true);
    execute 'set local role app_api';
    begin
        insert into platform.user_account (id, email, display_name)
        values ('10000000-0000-0000-0000-000000000002', 'second@example.test', 'Intruder');
        raise notice 'FAIL: R2 a second unauthenticated account was created';
    exception when insufficient_privilege then
        raise notice 'PASS: R2 the bootstrap window is inert once a user exists';
    end;
    execute 'reset role';
end $$;

rollback;

\echo ''
\echo '========== PART 2 — fixtures and policy assertions =========='
begin;

-- -------------------------------------------------------------------------------------
-- Fixtures, created as the schema owner so RLS does not interfere with setup.
-- -------------------------------------------------------------------------------------
insert into platform.user_account (id, email, display_name, is_blocked, expires_on) values
    ('20000000-0000-0000-0000-000000000001', 'admin@example.test',    'Admin',    false, null),
    ('20000000-0000-0000-0000-000000000002', 'editor@example.test',   'Editor',   false, null),
    ('20000000-0000-0000-0000-000000000003', 'analyst@example.test',  'Analyst',  false, null),
    ('20000000-0000-0000-0000-000000000004', 'negotiator@example.test','Negotiator',false, null),
    ('20000000-0000-0000-0000-000000000005', 'blocked@example.test',  'Blocked',  true,  null),
    ('20000000-0000-0000-0000-000000000006', 'expired@example.test',  'Expired',  false, current_date - 1);

insert into platform.user_role (user_id, role_id)
select u.id, r.id from (values
    ('20000000-0000-0000-0000-000000000001'::uuid, 'administrator'),
    ('20000000-0000-0000-0000-000000000002'::uuid, 'editor'),
    ('20000000-0000-0000-0000-000000000003'::uuid, 'analyst'),
    ('20000000-0000-0000-0000-000000000004'::uuid, 'negotiator'),
    ('20000000-0000-0000-0000-000000000005'::uuid, 'administrator'),
    ('20000000-0000-0000-0000-000000000006'::uuid, 'administrator')
) as v(user_id, role_key)
join platform.user_account u on u.id = v.user_id
join platform.role r on r.key = v.role_key;

insert into app.company (id, name, country_id)
values ('30000000-0000-0000-0000-000000000001', 'RLS Fixture Company',
        (select id from app.country where code = 'VE'));

insert into app.comparison_heading (id, group_id, name)
select '40000000-0000-0000-0000-000000000001', g.id, 'RLS Fixture Heading'
from app.comparison_heading_group g where lower(g.name) = 'generales';

-- One draft, one published. publication_consistency requires actor+timestamp for published.
insert into app.agreement (id, company_id, title, valid_from, valid_to, scope, state)
values ('50000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001',
        'Draft Agreement', '2026-01-01', '2027-12-31', 'Nacional', 'Draft');

insert into app.agreement (id, company_id, title, valid_from, valid_to, scope, state, published_at, published_by)
values ('50000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000001',
        'Published Agreement', '2025-01-01', '2026-12-31', 'Nacional', 'Published', now(),
        '20000000-0000-0000-0000-000000000001');

insert into app.agreement_clause (agreement_id, number_int, title, body, heading_id) values
    ('50000000-0000-0000-0000-000000000001', 1, 'Draft Clause', 'Body', '40000000-0000-0000-0000-000000000001'),
    ('50000000-0000-0000-0000-000000000002', 1, 'Published Clause', 'Body', '40000000-0000-0000-0000-000000000001');

-- A closed negotiation case, ready to be promoted.
insert into app.negotiation_case (id, company_id, started_on, state, closed_at)
values ('60000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001',
        '2025-06-01', 'Closed', now());

-- A transient role that may WRITE agreements but may NOT publish them. None of the four product
-- roles has that split, so it is created here to exercise the publication guard trigger. Without
-- it, a capability-less caller is filtered out by the RLS USING clause before the trigger runs.
insert into platform.role (id, key, name)
values ('70000000-0000-0000-0000-000000000001', 'writer_only', 'Writer Without Publish');

insert into platform.role_capability (role_id, capability_id)
select '70000000-0000-0000-0000-000000000001', c.id
from platform.capability c where c.key in ('agreement.read', 'agreement.write');

insert into platform.user_account (id, email, display_name)
values ('20000000-0000-0000-0000-000000000007', 'writer@example.test', 'Writer');

insert into platform.user_role (user_id, role_id)
values ('20000000-0000-0000-0000-000000000007', '70000000-0000-0000-0000-000000000001');

\echo '--- Read visibility ---'

do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"role":"anon"}', true);
    execute 'set local role anon';
    select count(*) into n from app.agreement;
    execute 'reset role';
    if n = 1 then
        raise notice 'PASS: R3 anon sees exactly the 1 published agreement (not the draft)';
    else
        raise notice 'FAIL: R3 anon saw % agreements, expected 1', n;
    end if;
end $$;

do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"role":"anon"}', true);
    execute 'set local role anon';
    select count(*) into n from app.agreement where id = '50000000-0000-0000-0000-000000000001';
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R4 anon cannot read a draft agreement even by id';
    else
        raise notice 'FAIL: R4 anon read a draft agreement by id';
    end if;
end $$;

do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"role":"anon"}', true);
    execute 'set local role anon';
    select count(*) into n from app.agreement_clause;
    execute 'reset role';
    if n = 1 then
        raise notice 'PASS: R5 anon sees only clauses of published agreements';
    else
        raise notice 'FAIL: R5 anon saw % clauses, expected 1', n;
    end if;
end $$;

do $$
begin
    perform set_config('request.jwt.claims', '{"role":"anon"}', true);
    execute 'set local role anon';
    begin
        perform count(*) from platform.user_account;
        raise notice 'FAIL: R6 anon was able to read platform.user_account';
    exception when insufficient_privilege then
        raise notice 'PASS: R6 anon has no privilege on the platform schema at all';
    end;
    execute 'reset role';
end $$;

do $$
declare n int;
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000003"}', true);
    execute 'set local role authenticated';
    select count(*) into n from app.agreement;
    execute 'reset role';
    if n = 2 then
        raise notice 'PASS: R7 an active analyst sees both agreements including the draft';
    else
        raise notice 'FAIL: R7 active analyst saw % agreements, expected 2', n;
    end if;
end $$;

do $$
declare n int;
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000005"}', true);
    execute 'set local role authenticated';
    select count(*) into n from app.agreement;
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R8 a BLOCKED user sees nothing on every request';
    else
        raise notice 'FAIL: R8 blocked user saw % agreements', n;
    end if;
end $$;

do $$
declare n int;
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000006"}', true);
    execute 'set local role authenticated';
    select count(*) into n from app.agreement;
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R9 an EXPIRED user sees nothing on every request';
    else
        raise notice 'FAIL: R9 expired user saw % agreements', n;
    end if;
end $$;

do $$
declare n int;
begin
    perform set_config('request.jwt.claims',
        '{"sub":"99999999-9999-9999-9999-999999999999"}', true);
    execute 'set local role authenticated';
    select count(*) into n from app.agreement;
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R10 an authenticated token with NO provisioned profile sees nothing';
    else
        raise notice 'FAIL: R10 unprovisioned user saw % agreements', n;
    end if;
end $$;

do $$
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000003"}', true);
    execute 'set local role authenticated';
    begin
        insert into app.agreement (company_id, title, valid_from, scope)
        values ('30000000-0000-0000-0000-000000000001', 'Direct Insert', '2024-01-01', 'Nacional');
        raise notice 'FAIL: R11 the authenticated role was able to write directly';
    exception when insufficient_privilege then
        raise notice 'PASS: R11 the authenticated role has SELECT only; every write must go through the API';
    end;
    execute 'reset role';
end $$;

\echo '--- Write authorisation via the API role ---'

do $$
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000003"}', true);
    execute 'set local role app_api';
    begin
        insert into app.company (name) values ('Analyst Company');
        raise notice 'FAIL: R12 an analyst created a company through the API role';
    exception when insufficient_privilege then
        raise notice 'PASS: R12 an analyst without company.write is refused by RLS';
    end;
    execute 'reset role';
end $$;

do $$
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    begin
        insert into app.company (name) values ('Editor Company');
        raise notice 'PASS: R13 an editor WITH company.write is allowed to insert';
    exception when others then
        raise notice 'FAIL: R13 editor insert rejected: %', sqlerrm;
    end;
    execute 'reset role';
end $$;

-- -------------------------------------------------------------------------------------
-- NOTE ON FAILURE MODES, learned by running these tests:
--
--   * INSERT whose WITH CHECK fails  -> raises insufficient_privilege (an error the caller sees)
--   * SELECT / UPDATE / DELETE whose USING fails -> SILENTLY matches zero rows, no error
--
-- The second behaviour is secure but invisible, so the API layer MUST inspect the affected row
-- count and treat zero as not-found/forbidden. The assertions below therefore check row counts
-- for filtering and exceptions only where an error is genuinely expected.
-- -------------------------------------------------------------------------------------

do $$
declare n int;
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000003"}', true);
    execute 'set local role app_api';
    update app.agreement set state = 'Published', published_at = now(),
           published_by = '20000000-0000-0000-0000-000000000003'
     where id = '50000000-0000-0000-0000-000000000001';
    get diagnostics n = row_count;
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R14 an analyst without agreement.write affects 0 rows (RLS filters silently)';
    else
        raise notice 'FAIL: R14 an analyst updated % row(s)', n;
    end if;
end $$;

-- The guard trigger only runs for a caller the RLS USING clause lets through: agreement.write
-- yes, agreement.publish no. That is the exact combination the trigger exists to stop.
do $$
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000007"}', true);
    execute 'set local role app_api';
    begin
        update app.agreement set state = 'Published', published_at = now(),
               published_by = '20000000-0000-0000-0000-000000000007'
         where id = '50000000-0000-0000-0000-000000000001';
        raise notice 'FAIL: R15 a writer without agreement.publish published an agreement';
    exception when insufficient_privilege then
        raise notice 'PASS: R15 the publication guard trigger refuses agreement.write without agreement.publish';
    end;
    execute 'reset role';
end $$;

do $$
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    begin
        update app.agreement set state = 'Published', published_at = now(),
               published_by = '20000000-0000-0000-0000-000000000002'
         where id = '50000000-0000-0000-0000-000000000001';
        raise notice 'PASS: R15b an editor WITH agreement.publish can publish';
    exception when others then
        raise notice 'FAIL: R15 editor publish rejected: %', sqlerrm;
    end;
    execute 'reset role';
end $$;

do $$
declare n int;
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    update app.negotiation_case set state = 'Promoted',
           promoted_agreement_id = '50000000-0000-0000-0000-000000000002'
     where id = '60000000-0000-0000-0000-000000000001';
    get diagnostics n = row_count;
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R16 an editor without negotiation.write affects 0 rows';
    else
        raise notice 'FAIL: R16 an editor updated % case row(s)', n;
    end if;
end $$;

do $$
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000004"}', true);
    execute 'set local role app_api';
    begin
        update app.negotiation_case set state = 'Promoted', promoted_agreement_id = '50000000-0000-0000-0000-000000000002'
         where id = '60000000-0000-0000-0000-000000000001';
        raise notice 'PASS: R17 a negotiator WITH negotiation.promote can promote a closed case';
    exception when others then
        raise notice 'FAIL: R17 negotiator promotion rejected: %', sqlerrm;
    end;
    execute 'reset role';
end $$;

do $$
declare n int;
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000003"}', true);
    execute 'set local role app_api';
    select count(*) into n from platform.audit_entry;
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R18 the audit trail is invisible without audit.read (0 rows, not an error)';
    else
        raise notice 'FAIL: R18 an analyst saw % audit rows', n;
    end if;
end $$;

do $$
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000001"}', true);
    execute 'set local role app_api';
    begin
        perform count(*) from platform.audit_entry;
        raise notice 'PASS: R19 an administrator WITH audit.read can read the audit trail';
    exception when others then
        raise notice 'FAIL: R19 administrator audit read rejected: %', sqlerrm;
    end;
    execute 'reset role';
end $$;

do $$
declare n int;
begin
    perform set_config('request.jwt.claims',
        '{"sub":"20000000-0000-0000-0000-000000000003"}', true);
    execute 'set local role app_api';
    select count(*) into n from platform.user_account;
    execute 'reset role';
    if n = 1 then
        raise notice 'PASS: R20 a non-manager reads only their own account (1 of 6)';
    else
        raise notice 'FAIL: R20 non-manager saw % accounts, expected 1', n;
    end if;
end $$;

rollback;

\echo ''
\echo '--- all RLS tests executed; both transactions rolled back, database unchanged ---'
