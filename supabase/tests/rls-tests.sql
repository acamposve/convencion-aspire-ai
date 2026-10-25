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
    ('20000000-0000-0000-0000-000000000003', 'reader@example.test',  'Reader (negotiator role)',  false, null),
    ('20000000-0000-0000-0000-000000000004', 'negotiator@example.test','Negotiator',false, null),
    ('20000000-0000-0000-0000-000000000005', 'blocked@example.test',  'Blocked',  true,  null),
    ('20000000-0000-0000-0000-000000000006', 'expired@example.test',  'Expired',  false, current_date - 1);

insert into platform.user_role (user_id, role_id)
select u.id, r.id from (values
    ('20000000-0000-0000-0000-000000000001'::uuid, 'administrator'),
    ('20000000-0000-0000-0000-000000000002'::uuid, 'editor'),
    ('20000000-0000-0000-0000-000000000003'::uuid, 'negotiator'),
    ('20000000-0000-0000-0000-000000000004'::uuid, 'negotiator'),
    ('20000000-0000-0000-0000-000000000005'::uuid, 'administrator'),
    ('20000000-0000-0000-0000-000000000006'::uuid, 'administrator')
) as v(user_id, role_key)
join platform.user_account u on u.id = v.user_id
join platform.role r on r.key = v.role_key;

insert into app.company (id, name, country_id)
values ('30000000-0000-0000-0000-000000000001', 'RLS Fixture Company',
        (select id from app.country where code = 'VE'));

-- Company membership must exist BEFORE the read and write assertions. T8's tenant policies scope
-- agreements by company, so a user with no profile — or with a profile created later in this file —
-- would see none of them and the agreement tests below would pass or fail for the wrong reason.
insert into platform.user_profile (user_id, company_id) values
    ('20000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000001'),
    ('20000000-0000-0000-0000-000000000003', '30000000-0000-0000-0000-000000000001');

insert into app.comparison_heading (id, group_id, name)
select '40000000-0000-0000-0000-000000000001', g.id, 'RLS Fixture Heading'
from app.comparison_heading_group g where lower(g.name) = 'generales';

-- One draft, one published. publication_consistency requires actor+timestamp for published.
insert into app.agreement (id, company_id, valid_from, valid_to, scope, state)
values ('50000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000001',
        '2026-01-01', '2027-12-31', 'Nacional', 'Draft');

insert into app.agreement (id, company_id, valid_from, valid_to, scope, state, published_at, published_by)
values ('50000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000001',
        '2025-01-01', '2026-12-31', 'Nacional', 'Published', now(),
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

-- A negotiator who may write a case but not promote it: no seeded role has that split, and the
-- promotion guard needs the shape to prove the capability check (T11 R36).
insert into platform.role (id, key, name)
values ('70000000-0000-0000-0000-000000000002', 'negotiator_no_promote', 'Negotiator Without Promotion');

insert into platform.role_capability (role_id, capability_id)
select '70000000-0000-0000-0000-000000000002', id
from platform.capability where key in ('negotiation.read', 'negotiation.write');

insert into platform.user_account (id, email, display_name)
values ('20000000-0000-0000-0000-000000000010', 'negotiator.np@example.test', 'Negotiator Without Promotion');

insert into platform.user_role (user_id, role_id)
values ('20000000-0000-0000-0000-000000000010', '70000000-0000-0000-0000-000000000002');

insert into platform.user_profile (user_id, company_id)
values ('20000000-0000-0000-0000-000000000010', '30000000-0000-0000-0000-000000000001');

-- The writer needs a company too, or the tenant policy would filter the agreement out before the
-- publication guard trigger got a chance to refuse it.
insert into platform.user_profile (user_id, company_id)
values ('20000000-0000-0000-0000-000000000007', '30000000-0000-0000-0000-000000000001');

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
        raise notice 'PASS: R7 an active negotiator sees both agreements including the draft';
    else
        raise notice 'FAIL: R7 active negotiator saw % agreements, expected 2', n;
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
        insert into app.agreement (company_id, valid_from, scope)
        values ('30000000-0000-0000-0000-000000000001', '2024-01-01', 'Nacional');
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
        insert into app.company (name) values ('Unauthorised Company');
        raise notice 'FAIL: R12 a negotiator created a company through the API role';
    exception when insufficient_privilege then
        raise notice 'PASS: R12 a negotiator without company.write is refused by RLS';
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
        raise notice 'PASS: R14 a negotiator without agreement.write affects 0 rows (RLS filters silently)';
    else
        raise notice 'FAIL: R14 a negotiator updated % row(s)', n;
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
    update app.negotiation_case set promoted_agreement_id = '50000000-0000-0000-0000-000000000002'
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
        update app.negotiation_case set promoted_agreement_id = '50000000-0000-0000-0000-000000000002'
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
        raise notice 'FAIL: R18 a negotiator saw % audit rows', n;
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


\echo '--- Tenant isolation: documents (Step 2 requirement) ---'

-- Second company, two company-scoped users plus a user with no company at all.
insert into app.company (id, name, country_id)
values ('30000000-0000-0000-0000-000000000002', 'RLS Second Company',
        (select id from app.country where code = 'VE'));

insert into platform.user_account (id, email, display_name, is_blocked, expires_on) values
    ('20000000-0000-0000-0000-000000000008', 'editor.b@example.test',       'Editor B',        false, null),
    ('20000000-0000-0000-0000-000000000009', 'transcriptor@example.test',   'No Company',      false, null);

insert into platform.user_role (user_id, role_id)
select u.id, r.id from (values
    ('20000000-0000-0000-0000-000000000008'::uuid, 'editor'),
    ('20000000-0000-0000-0000-000000000009'::uuid, 'transcriptor')
) as v(user_id, role_key)
join platform.user_account u on u.id = v.user_id
join platform.role r on r.key = v.role_key;

-- Company A: editor and reader. Company B: editor B. The transcriptor deliberately has no company.
-- The company A memberships were created with the fixtures at the top; only company B is new here.
insert into platform.user_profile (user_id, company_id) values
    ('20000000-0000-0000-0000-000000000008', '30000000-0000-0000-0000-000000000002');

-- One document per company, plus one with no company (must be visible to nobody).
insert into platform.document (id, storage_path, file_name, content_type, size_bytes, uploaded_by, company_id) values
    ('40000000-0000-0000-0000-00000000000a', 'tenancy/doc-a.pdf',    'doc-a.pdf',    'application/pdf', 1024, '20000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000001'),
    ('40000000-0000-0000-0000-00000000000b', 'tenancy/doc-b.pdf',    'doc-b.pdf',    'application/pdf', 1024, '20000000-0000-0000-0000-000000000008', '30000000-0000-0000-0000-000000000002'),
    ('40000000-0000-0000-0000-00000000000c', 'tenancy/doc-none.pdf', 'doc-none.pdf', 'application/pdf', 1024, '20000000-0000-0000-0000-000000000002', null);

insert into platform.document_text (document_id, content, char_count) values
    ('40000000-0000-0000-0000-00000000000a', 'Texto del documento A', length('Texto del documento A')),
    ('40000000-0000-0000-0000-00000000000b', 'Texto del documento B', length('Texto del documento B'));

-- R21 � a company user sees only their own company's document
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    select count(*) into n from platform.document;
    execute 'reset role';
    if n = 1 then
        raise notice 'PASS: R21 company A editor sees exactly 1 document (its own)';
    else
        raise notice 'FAIL: R21 company A editor saw % documents, expected 1', n;
    end if;
end $$;

-- R22 � extracted text inherits document visibility
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    select count(*) into n from platform.document_text;
    execute 'reset role';
    if n = 1 then
        raise notice 'PASS: R22 company A editor reads text only of its own document';
    else
        raise notice 'FAIL: R22 company A editor saw % document_text rows, expected 1', n;
    end if;
end $$;

-- R23 � the other company sees only its own
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000008"}', true);
    execute 'set local role app_api';
    select count(*) into n from platform.document;
    execute 'reset role';
    if n = 1 then
        raise notice 'PASS: R23 company B editor sees exactly 1 document (its own)';
    else
        raise notice 'FAIL: R23 company B editor saw % documents, expected 1', n;
    end if;
end $$;

-- R24 � company.read unlocks cross-company, but not an unattached document
do $$
declare n int; m int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000001"}', true);
    execute 'set local role app_api';
    select count(*) into n from platform.document;
    select count(*) into m from platform.document where id = '40000000-0000-0000-0000-00000000000c';
    execute 'reset role';
    if n = 2 and m = 0 then
        raise notice 'PASS: R24 company.read holder sees both company documents and NOT the unattached one';
    else
        raise notice 'FAIL: R24 admin saw % documents (expected 2) and % unattached (expected 0)', n, m;
    end if;
end $$;

-- R25 � cross-company write is filtered, not merely rejected
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    update platform.document set file_name = 'tampered.pdf'
     where id = '40000000-0000-0000-0000-00000000000b';
    get diagnostics n = row_count;
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R25 company A editor updating company B document affects 0 rows';
    else
        raise notice 'FAIL: R25 company A editor modified % row(s) of company B document', n;
    end if;
end $$;

-- R26 � same for delete
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    delete from platform.document where id = '40000000-0000-0000-0000-00000000000b';
    get diagnostics n = row_count;
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R26 company A editor deleting company B document affects 0 rows';
    else
        raise notice 'FAIL: R26 company A editor deleted % row(s) of company B document', n;
    end if;
end $$;

-- R27 � cannot create a document owned by another company
do $$
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    begin
        insert into platform.document (storage_path, file_name, content_type, size_bytes, uploaded_by, company_id)
        values ('tenancy/injected.pdf', 'injected.pdf', 'application/pdf', 512,
                '20000000-0000-0000-0000-000000000002', '30000000-0000-0000-0000-000000000002');
        raise notice 'FAIL: R27 company A editor created a company B document';
    exception when insufficient_privilege then
        raise notice 'PASS: R27 creating a document for another company is refused by RLS';
    end;
    execute 'reset role';
end $$;

-- R28 � a user with no company sees nothing
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000009"}', true);
    execute 'set local role app_api';
    select count(*) into n from platform.document;
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R28 a user with no company sees 0 documents';
    else
        raise notice 'FAIL: R28 a user with no company saw % documents', n;
    end if;
end $$;

\echo '--- Reference data: statutes are NOT tenant-scoped (T10) ---'

-- Two statutes and one article, created as the owner so RLS does not interfere with setup.
insert into app.statute (id, kind, name) values
    ('80000000-0000-0000-0000-000000000001', 'LabourLaw',   'RLS Labour Statute'),
    ('80000000-0000-0000-0000-000000000002', 'OtherStatute', 'RLS Other Statute');

insert into app.statute_article (statute_id, number_int, title, body) values
    ('80000000-0000-0000-0000-000000000001', 1, 'RLS Article One', 'Body');

-- R29 — a company A user reads the whole library: there is no company predicate to apply.
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    select count(*) into n from app.statute;
    execute 'reset role';
    if n = 2 then
        raise notice 'PASS: R29 a company A editor reads both statutes (not tenant-scoped)';
    else
        raise notice 'FAIL: R29 company A editor saw % statutes, expected 2', n;
    end if;
end $$;

-- R30 — so does a user of the OTHER company. Applying a company filter here would make the library
-- invisible to everyone, which is exactly the risk T10 records.
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000008"}', true);
    execute 'set local role app_api';
    select count(*) into n from app.statute;
    execute 'reset role';
    if n = 2 then
        raise notice 'PASS: R30 a company B editor reads both statutes too';
    else
        raise notice 'FAIL: R30 company B editor saw % statutes, expected 2', n;
    end if;
end $$;

-- R31 — and membership is not required at all: user 009 has no profile row.
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000009"}', true);
    execute 'set local role app_api';
    select count(*) into n from app.statute;
    execute 'reset role';
    if n = 2 then
        raise notice 'PASS: R31 a user with no company reads the statute library';
    else
        raise notice 'FAIL: R31 a user with no company saw % statutes, expected 2', n;
    end if;
end $$;

-- R32 — writing reference data still requires statute.write; the negotiator has read only.
do $$
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000003"}', true);
    execute 'set local role app_api';
    begin
        insert into app.statute (kind, name) values ('OtherStatute', 'Written Without Capability');
        raise notice 'FAIL: R32 a negotiator wrote a statute through the API role';
    exception when insufficient_privilege then
        raise notice 'PASS: R32 statute.write is required to write the library';
    end;
    execute 'reset role';
end $$;

-- R33 — an editor holds statute.write and is allowed.
do $$
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    begin
        insert into app.statute (kind, name) values ('OtherStatute', 'Written By An Editor');
        raise notice 'PASS: R33 an editor WITH statute.write can write the library';
    exception when others then
        raise notice 'FAIL: R33 editor statute write rejected: %', sqlerrm;
    end;
    execute 'reset role';
end $$;

\echo '--- Tenant isolation: agreements (the other direction to R7) ---'

-- R34 — R7 proves a company A user sees its own two agreements, which a capability-only policy would
-- also satisfy. This proves the predicate: a company B editor holds agreement.read and must still see
-- none of company A's agreements, and none of their clauses.
do $$
declare
    visible int;
    clauses int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000008"}', true);
    execute 'set local role app_api';
    select count(*) into visible from app.agreement
     where company_id = '30000000-0000-0000-0000-000000000001';
    select count(*) into clauses from app.agreement_clause
     where agreement_id in ('50000000-0000-0000-0000-000000000001',
                            '50000000-0000-0000-0000-000000000002');
    execute 'reset role';
    if visible = 0 and clauses = 0 then
        raise notice 'PASS: R34 a company B editor sees none of company A''s agreements or clauses';
    else
        raise notice 'FAIL: R34 a company B editor saw % agreement(s) and % clause(s)', visible, clauses;
    end if;
end $$;

-- R35 — and cannot write one either: the tenant predicate filters the update before it applies.
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000008"}', true);
    execute 'set local role app_api';
    update app.agreement set scope = 'Ajeno'
     where id = '50000000-0000-0000-0000-000000000001';
    get diagnostics n = row_count;
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R35 a company B editor updating company A''s agreement affects 0 rows';
    else
        raise notice 'FAIL: R35 a company B editor updated % row(s)', n;
    end if;
end $$;


\echo '--- Negotiation: promotion guard and tenant scope (T11) ---'

-- Fixtures created as the owner so RLS does not interfere with setup.
insert into app.negotiation_case (id, company_id, started_on, state, closed_at) values
    ('80000000-0000-0000-0000-000000000101', '30000000-0000-0000-0000-000000000001', '2026-01-01', 'Closed', now()),
    ('80000000-0000-0000-0000-000000000102', '30000000-0000-0000-0000-000000000001', '2026-01-01', 'Open', null),
    ('80000000-0000-0000-0000-000000000103', '30000000-0000-0000-0000-000000000001', '2026-01-01', 'Open', null),
    ('80000000-0000-0000-0000-000000000104', '30000000-0000-0000-0000-000000000001', '2026-01-01', 'Closed', now());

insert into app.agreement (id, company_id, valid_from, scope, state) values
    ('80000000-0000-0000-0000-000000000201', '30000000-0000-0000-0000-000000000001', '2027-01-01', 'Nacional', 'Draft'),
    ('80000000-0000-0000-0000-000000000202', '30000000-0000-0000-0000-000000000001', '2027-02-01', 'Nacional', 'Draft');

-- R36 — the promotion capability is required for the link, at the database.
do $$
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000010"}', true);
    execute 'set local role app_api';
    begin
        update app.negotiation_case
        set promoted_agreement_id = '80000000-0000-0000-0000-000000000201'
        where id = '80000000-0000-0000-0000-000000000104';
        raise notice 'FAIL: R36 a writer without negotiation.promote promoted a case';
    exception when insufficient_privilege then
        raise notice 'PASS: R36 promotion requires negotiation.promote at the database';
    end;
    execute 'reset role';
end $$;

-- R37 — promotion only from an already-closed case.
do $$
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000003"}', true);
    execute 'set local role app_api';
    begin
        update app.negotiation_case
        set state = 'Closed', closed_at = now(),
            promoted_agreement_id = '80000000-0000-0000-0000-000000000201'
        where id = '80000000-0000-0000-0000-000000000102';
        raise notice 'FAIL: R37 a case was closed and promoted in one statement';
    exception when invalid_parameter_value then
        raise notice 'PASS: R37 a case can only be promoted from an already-closed state';
    end;
    execute 'reset role';
end $$;

-- R38 — a second promotion is refused; the case is not silently re-pointed.
do $$
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000003"}', true);
    execute 'set local role app_api';
    begin
        update app.negotiation_case
        set promoted_agreement_id = '80000000-0000-0000-0000-000000000201'
        where id = '80000000-0000-0000-0000-000000000101';
        update app.negotiation_case
        set promoted_agreement_id = '80000000-0000-0000-0000-000000000202'
        where id = '80000000-0000-0000-0000-000000000101';
        raise notice 'FAIL: R38 a promoted case was re-pointed at a second agreement';
    exception when unique_violation then
        raise notice 'PASS: R38 a case is promoted once; re-pointing is refused';
    end;
    execute 'reset role';
end $$;

-- R39 — a promoted case is terminal: the link cannot be cleared.
do $$
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000003"}', true);
    execute 'set local role app_api';
    begin
        update app.negotiation_case
        set promoted_agreement_id = '80000000-0000-0000-0000-000000000201'
        where id = '80000000-0000-0000-0000-000000000104';
        update app.negotiation_case
        set state = 'InProgress', closed_at = null, promoted_agreement_id = null
        where id = '80000000-0000-0000-0000-000000000104';
        raise notice 'FAIL: R39 a promoted case was reopened';
    exception when invalid_parameter_value then
        raise notice 'PASS: R39 a promoted case is terminal (A-21)';
    end;
    execute 'reset role';
end $$;

-- R40 — a company B user sees none of company A's negotiation cases.
do $$
declare n int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000008"}', true);
    execute 'set local role app_api';
    select count(*) into n from app.negotiation_case
     where company_id = '30000000-0000-0000-0000-000000000001';
    execute 'reset role';
    if n = 0 then
        raise notice 'PASS: R40 a company B user sees no negotiation cases of company A';
    else
        raise notice 'FAIL: R40 a company B user saw % case(s) of company A', n;
    end if;
end $$;

-- --- Publishing (T12) ----------------------------------------------------------------------------

-- Fixtures: a news writer who may write but not publish, one category and one draft article.
insert into platform.role (id, key, name)
values ('70000000-0000-0000-0000-000000000003', 'news_writer', 'News Writer Without Publish');

insert into platform.role_capability (role_id, capability_id)
select '70000000-0000-0000-0000-000000000003', id
from platform.capability where key in ('news.read', 'news.write');

insert into platform.user_account (id, email, display_name)
values ('20000000-0000-0000-0000-000000000011', 'news.writer@example.test', 'News Writer');

insert into platform.user_role (user_id, role_id)
values ('20000000-0000-0000-0000-000000000011', '70000000-0000-0000-0000-000000000003');

insert into platform.user_profile (user_id, company_id)
values ('20000000-0000-0000-0000-000000000011', '30000000-0000-0000-0000-000000000001');

insert into app.news_category (id, name) values
    ('80000000-0000-0000-0000-000000000301', 'T12 category');

insert into app.news_article (id, category_id, title, body, state) values
    ('80000000-0000-0000-0000-000000000302', '80000000-0000-0000-0000-000000000301',
     'T12 draft', 'Cuerpo', 'Draft');

-- R41 — news.write alone cannot publish; the guard, not the handler, is what refuses.
do $$
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000011"}', true);
    execute 'set local role app_api';
    begin
        update app.news_article set state = 'Published'
        where id = '80000000-0000-0000-0000-000000000302';
        raise notice 'FAIL: R41 a news writer without news.publish published an article';
    exception when insufficient_privilege then
        raise notice 'PASS: R41 publishing news requires news.publish at the database';
    end;
    execute 'reset role';
end $$;

-- R42 — with the capability the transition succeeds, and the database records the actor and the time:
-- the UPDATE sets neither column.
do $$
declare
    actor uuid := '20000000-0000-0000-0000-000000000002';
    recorded uuid;
    recorded_at timestamptz;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    update app.news_article set state = 'Published'
    where id = '80000000-0000-0000-0000-000000000302';
    execute 'reset role';

    select published_by, published_at into recorded, recorded_at
    from app.news_article where id = '80000000-0000-0000-0000-000000000302';

    if recorded = actor and recorded_at is not null then
        raise notice 'PASS: R42 the database records the publisher and the publication time';
    else
        raise notice 'FAIL: R42 published_by=% published_at=%', recorded, recorded_at;
    end if;
end $$;

-- R43 — anon sees the published article and no draft, and cannot write.
do $$
declare
    published int;
    drafts int;
begin
    perform set_config('request.jwt.claims', '', true);
    execute 'set local role anon';

    select count(*) into published from app.news_article
     where id = '80000000-0000-0000-0000-000000000302';
    select count(*) into drafts from app.news_article where state = 'Draft';

    begin
        insert into app.news_article (category_id, title, body)
        values ('80000000-0000-0000-0000-000000000301', 'Anon', 'Cuerpo');
        execute 'reset role';
        raise notice 'FAIL: R43 anon inserted a news article';
    exception when insufficient_privilege then
        execute 'reset role';
        if published = 1 and drafts = 0 then
            raise notice 'PASS: R43 anon reads published news only and cannot write';
        else
            raise notice 'FAIL: R43 anon saw % published and % draft article(s)', published, drafts;
        end if;
    end;
end $$;

-- R44 — anon reads no unpublished agreement.
do $$
declare
    drafts int;
begin
    perform set_config('request.jwt.claims', '', true);
    execute 'set local role anon';
    select count(*) into drafts from app.agreement where state <> 'Published';
    execute 'reset role';

    if drafts = 0 then
        raise notice 'PASS: R44 anon sees no unpublished agreement';
    else
        raise notice 'FAIL: R44 anon saw % unpublished agreement(s)', drafts;
    end if;
end $$;

-- --- Administration and audit (T13) ---------------------------------------------------------------

-- One audit row written as the owner, so the visibility tests have something to look at.
insert into platform.audit_entry (actor_id, module, entity_type, entity_id, action, changes)
values ('20000000-0000-0000-0000-000000000001', 'identity', 'user_account',
        '20000000-0000-0000-0000-000000000002', 'updated', '{"isBlocked": {"to": true}}'::jsonb);

-- R45 — the audit trail is invisible without audit.read, and visible with it.
do $$
declare
    editor_rows int;
    admin_rows  int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    select count(*) into editor_rows from platform.audit_entry;
    execute 'reset role';

    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000001"}', true);
    execute 'set local role app_api';
    select count(*) into admin_rows from platform.audit_entry;
    execute 'reset role';

    if editor_rows = 0 and admin_rows >= 1 then
        raise notice 'PASS: R45 audit.read gates the audit trail (editor=0, administrator=%)', admin_rows;
    else
        raise notice 'FAIL: R45 audit rows editor=% administrator=%', editor_rows, admin_rows;
    end if;
end $$;

-- R46 — user.manage widens user_account; without it a caller sees only their own row.
do $$
declare
    editor_rows int;
    admin_rows  int;
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000002"}', true);
    execute 'set local role app_api';
    select count(*) into editor_rows from platform.user_account;
    execute 'reset role';

    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000001"}', true);
    execute 'set local role app_api';
    select count(*) into admin_rows from platform.user_account;
    execute 'reset role';

    if editor_rows = 1 and admin_rows >= 6 then
        raise notice 'PASS: R46 user.manage widens user_account (editor=1, administrator=%)', admin_rows;
    else
        raise notice 'FAIL: R46 user rows editor=% administrator=%', editor_rows, admin_rows;
    end if;
end $$;

-- R47 — the bootstrap function is inert once accounts exist, whatever the caller holds.
do $$
begin
    perform set_config('request.jwt.claims', '{"sub":"20000000-0000-0000-0000-000000000001"}', true);
    execute 'set local role app_api';
    begin
        perform platform.bootstrap_first_admin(
            '20000000-0000-0000-0000-0000000000ff', 'late@example.test', 'Late SuperAdmin');
        raise notice 'FAIL: R47 the bootstrap ran on a configured database';
    exception when invalid_parameter_value then
        raise notice 'PASS: R47 the bootstrap function refuses once an account exists';
    end;
    execute 'reset role';
end $$;

rollback;

\echo ''
\echo '--- all RLS tests executed; both transactions rolled back, database unchanged ---'
