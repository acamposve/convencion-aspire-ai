-- =====================================================================================
-- constraint-tests.sql — referential integrity and constraint enforcement
--
-- Every test runs inside ONE transaction that is rolled back at the end, so the database is left
-- exactly as it was: zero business rows. Each test prints PASS or FAIL; the runner fails the
-- validation if any line contains 'FAIL:'.
--
-- Convention:
--   must-fail  -> the operation is attempted; PASS only if the database rejects it
--   must-pass  -> the operation is attempted; PASS only if the database accepts it
-- =====================================================================================

begin;

\echo '--- G. the one-time bootstrap function (T13) ---'

-- Runs first, while the transaction still has no accounts: the function is the only path that creates
-- the first user, and it writes the account, profile, role grant and audit row together.
do $$
declare
    v_id         uuid;
    v_created_at timestamptz;
    v_profiles   int;
    v_grants     int;
    v_audited    int;
begin
    select f.id, f.created_at into v_id, v_created_at
    from platform.bootstrap_first_admin(
        'aaaaaaaa-1111-1111-1111-111111111111', 'first@example.test', 'First SuperAdmin') as f;

    select count(*) into v_profiles from platform.user_profile where user_id = v_id;
    select count(*) into v_grants   from platform.user_role    where user_id = v_id;
    select count(*) into v_audited  from platform.audit_entry
     where entity_type = 'user_account' and entity_id = v_id::text and action = 'created';

    if v_id is null or v_created_at is null or v_profiles <> 1 or v_grants <> 1 or v_audited <> 1 then
        raise notice 'FAIL: G1 bootstrap incomplete (profile=%, grants=%, audit=%)', v_profiles, v_grants, v_audited;
    else
        raise notice 'PASS: G1 the bootstrap creates the first SuperAdmin with profile, role and audit atomically';
    end if;

    begin
        perform platform.bootstrap_first_admin(
            'bbbbbbbb-2222-2222-2222-222222222222', 'second@example.test', 'Second');
        raise notice 'FAIL: G2 a second bootstrap succeeded';
    exception when invalid_parameter_value then
        raise notice 'PASS: G2 the bootstrap is inert once an account exists';
    end;
end $$;

\echo ''
\echo '--- fixtures (rolled back at the end) ---'

-- Geography: two countries, a state in each, and two states in the same country.
insert into app.state (id, country_id, name)
select '11111111-1111-1111-1111-111111111111', c.id, 'Trujillo'
from app.country c where c.code = 'VE';

insert into app.state (id, country_id, name)
select '22222222-2222-2222-2222-222222222222', c.id, 'Zulia'
from app.country c where c.code = 'VE';

insert into app.country (id, code, name) values
    ('33333333-3333-3333-3333-333333333333', 'CO', 'Colombia');

insert into app.state (id, country_id, name) values
    ('44444444-4444-4444-4444-444444444444', '33333333-3333-3333-3333-333333333333', 'Antioquia');

insert into app.municipality (id, state_id, name) values
    ('55555555-5555-5555-5555-555555555555', '11111111-1111-1111-1111-111111111111', 'Valera'),
    ('66666666-6666-6666-6666-666666666666', '22222222-2222-2222-2222-222222222222', 'Maracaibo');

insert into app.company (id, name, country_id, state_id, municipality_id)
values ('77777777-7777-7777-7777-777777777777', 'Fixture Company',
        (select id from app.country where code = 'VE'),
        '11111111-1111-1111-1111-111111111111',
        '55555555-5555-5555-5555-555555555555');

insert into app.agreement (id, company_id, valid_from, valid_to, scope)
values ('88888888-8888-8888-8888-888888888888', '77777777-7777-7777-7777-777777777777',
        '2026-01-01', '2028-12-31', 'Nacional');

insert into app.agreement (id, company_id, valid_from, valid_to, scope)
values ('99999999-9999-9999-9999-999999999999', '77777777-7777-7777-7777-777777777777',
        '2029-01-01', '2031-12-31', 'Nacional');

insert into app.comparison_heading (id, group_id, name)
select 'aaaaaaaa-0000-0000-0000-000000000001', g.id, 'Fixture Heading'
from app.comparison_heading_group g where lower(g.name) = 'generales';

insert into app.agreement_clause (agreement_id, number_int, title, body, heading_id)
values ('88888888-8888-8888-8888-888888888888', 1, 'Clause One', 'Body one',
        'aaaaaaaa-0000-0000-0000-000000000001');

\echo '--- A. agreement invariants ---'

do $$
begin
    begin
        insert into app.agreement (company_id, valid_from, valid_to, scope)
        values ('77777777-7777-7777-7777-777777777777', '2028-01-01', '2026-01-01', 'Nacional');
        raise notice 'FAIL: A1 invalid date range accepted';
    exception when check_violation then
        raise notice 'PASS: A1 agreement_validity_order rejects valid_to < valid_from';
    end;
end $$;

do $$
begin
    begin
        insert into app.agreement (company_id, valid_from, scope, state, published_at, published_by)
        values ('77777777-7777-7777-7777-777777777777', '2026-01-01', 'Nacional', 'Published', now(), null);
        raise notice 'FAIL: A2 published agreement without published_by accepted';
    exception when check_violation then
        raise notice 'PASS: A2 agreement_publication_consistency rejects Published without actor/timestamp';
    end;
end $$;

do $$
begin
    begin
        insert into app.agreement (company_id, valid_from, valid_to, scope)
        values ('77777777-7777-7777-7777-777777777777', '2026-01-01', '2027-01-01', 'Nacional');
        raise notice 'FAIL: A3 duplicate (company, valid_from) accepted';
    exception when unique_violation then
        raise notice 'PASS: A3 one agreement per company per start date';
    end;
end $$;

\echo '--- B. clause invariants ---'

do $$
begin
    begin
        insert into app.agreement_clause (agreement_id, number_int, title, body, heading_id)
        values ('88888888-8888-8888-8888-888888888888', 1, 'Duplicate Number', 'Body', 'aaaaaaaa-0000-0000-0000-000000000001');
        raise notice 'FAIL: B1 duplicate clause number accepted';
    exception when unique_violation then
        raise notice 'PASS: B1 clause number unique within an agreement';
    end;
end $$;

do $$
begin
    begin
        insert into app.agreement_clause (agreement_id, number_int, title, body, heading_id)
        values ('99999999-9999-9999-9999-999999999999', 1, 'Clause One', 'Body',
                'aaaaaaaa-0000-0000-0000-000000000001');
        raise notice 'PASS: B2 the same clause number in a DIFFERENT agreement is allowed';
    exception when others then
        raise notice 'FAIL: B2 same number in another agreement was rejected: %', sqlerrm;
    end;
end $$;

do $$
begin
    begin
        insert into app.agreement_clause (agreement_id, number_int, title, body, heading_id)
        values ('88888888-8888-8888-8888-888888888888', 2, 'No Heading', 'Body', null);
        raise notice 'FAIL: B3 clause without a heading accepted';
    exception when not_null_violation then
        raise notice 'PASS: B3 clause classification is mandatory (NOT NULL heading_id)';
    end;
end $$;

do $$
begin
    begin
        insert into app.agreement_clause (agreement_id, number_int, title, body, heading_id)
        values ('88888888-8888-8888-8888-888888888888', 0, 'Zero', 'Body', 'aaaaaaaa-0000-0000-0000-000000000001');
        raise notice 'FAIL: B4 clause number 0 accepted';
    exception when check_violation then
        raise notice 'PASS: B4 clause number must be >= 1';
    end;
end $$;

do $$
begin
    begin
        insert into app.agreement_clause (agreement_id, number_int, title, body, heading_id)
        values ('88888888-8888-8888-8888-888888888888', 3, 'Blank Body', '   ', 'aaaaaaaa-0000-0000-0000-000000000001');
        raise notice 'FAIL: B6 blank clause body accepted';
    exception when check_violation then
        raise notice 'PASS: B6 clause body cannot be blank';
    end;
end $$;

-- Decision Q-2: clause numbers are integers. A distinct number in the same agreement is accepted,
-- which proves the unique index is on the number and not on the row.
do $$
begin
    insert into app.agreement_clause (agreement_id, number_int, title, body, heading_id)
    values ('88888888-8888-8888-8888-888888888888', 2, 'Second Clause', 'Body', 'aaaaaaaa-0000-0000-0000-000000000001');
    raise notice 'PASS: B7 a distinct integer clause number is accepted in the same agreement';
exception when others then
    raise notice 'FAIL: B7 distinct clause number rejected: %', sqlerrm;
end $$;

-- The suffix column was dropped by decision Q-2; referencing it must now be a statement error.
do $$
declare
    v_sql text := $q$insert into app.agreement_clause (agreement_id, number_int, number_suffix, title, body, heading_id)
        values ('88888888-8888-8888-8888-888888888888', 9, 'A', 'Suffix', 'Body', 'aaaaaaaa-0000-0000-0000-000000000001')$q$;
begin
    begin
        execute v_sql;
        raise notice 'FAIL: B8 a number_suffix column still exists';
    exception when undefined_column then
        raise notice 'PASS: B8 number_suffix no longer exists (decision Q-2 applied)';
    end;
end $$;

\echo '--- B2. documents are PDF only (T4/D2) ---'

-- A real uploader is required: without one the insert would fail on the foreign key, and a test that
-- accepts any failure would pass for the wrong reason.
insert into platform.user_account (id, email, display_name, is_blocked, expires_on)
values ('dddddddd-0000-0000-0000-0000000000ff', 'pdf-only@example.test', 'PDF Only', false, null)
on conflict (id) do nothing;

do $$
begin
    begin
        insert into platform.document (storage_path, file_name, content_type, size_bytes, uploaded_by, company_id)
        values ('pdf-only/png.pdf', 'png.pdf', 'image/png', 1024,
                'dddddddd-0000-0000-0000-0000000000ff',
                '77777777-7777-7777-7777-777777777777');
        raise notice 'FAIL: B9 a PNG document was accepted by the database';
    exception when check_violation then
        raise notice 'PASS: B9 the database refuses a non-PDF content type';
    end;
end $$;

do $$
begin
    begin
        insert into platform.document (storage_path, file_name, content_type, size_bytes, uploaded_by, company_id)
        values ('pdf-only/jpeg.pdf', 'jpeg.pdf', 'image/jpeg', 1024,
                'dddddddd-0000-0000-0000-0000000000ff',
                '77777777-7777-7777-7777-777777777777');
        raise notice 'FAIL: B11 a JPEG document was accepted by the database';
    exception when check_violation then
        raise notice 'PASS: B11 the database refuses image/jpeg too';
    end;
end $$;

do $$
begin
    insert into platform.document (storage_path, file_name, content_type, size_bytes, uploaded_by, company_id)
    values ('pdf-only/accepted.pdf', 'accepted.pdf', 'application/pdf', 1024,
            'dddddddd-0000-0000-0000-0000000000ff',
            '77777777-7777-7777-7777-777777777777');
    raise notice 'PASS: B10 application/pdf is accepted by the database';
exception when others then
    raise notice 'FAIL: B10 application/pdf was rejected: %', sqlerrm;
end $$;
\echo '--- C. geography and cross-reference integrity ---'

do $$
begin
    begin
        insert into app.company (name, country_id, state_id)
        values ('Wrong Country State',
                (select id from app.country where code = 'VE'),
                '44444444-4444-4444-4444-444444444444');
        raise notice 'FAIL: C1 state from another country accepted';
    exception when foreign_key_violation then
        raise notice 'PASS: C1 composite FK rejects a state that is not in the given country';
    end;
end $$;

do $$
begin
    begin
        insert into app.company (name, country_id, state_id, municipality_id)
        values ('Wrong Municipality State',
                (select id from app.country where code = 'VE'),
                '11111111-1111-1111-1111-111111111111',
                '66666666-6666-6666-6666-666666666666');
        raise notice 'FAIL: C2 municipality from another state accepted';
    exception when foreign_key_violation then
        raise notice 'PASS: C2 composite FK rejects a municipality that is not in the given state';
    end;
end $$;

do $$
begin
    begin
        insert into app.state (country_id, name) values ('00000000-0000-0000-0000-000000000000', 'Orphan State');
        raise notice 'FAIL: C3 state with unknown country accepted';
    exception when foreign_key_violation then
        raise notice 'PASS: C3 state requires an existing country';
    end;
end $$;

\echo '--- D. negotiation consistency ---'

insert into app.negotiation_case (id, company_id, started_on, state)
values ('bbbbbbbb-0000-0000-0000-000000000001', '77777777-7777-7777-7777-777777777777', '2026-02-01', 'Open');
insert into app.negotiation_case (id, company_id, started_on, state)
values ('bbbbbbbb-0000-0000-0000-000000000002', '77777777-7777-7777-7777-777777777777', '2026-03-01', 'Open');

insert into app.union_petition (id, case_id, number_int, title, body)
values ('cccccccc-0000-0000-0000-000000000001', 'bbbbbbbb-0000-0000-0000-000000000001', 1, 'Petition A', 'Body');
insert into app.union_petition (id, case_id, number_int, title, body)
values ('cccccccc-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-000000000002', 1, 'Petition B', 'Body');

insert into app.negotiation_meeting (id, case_id, held_on)
values ('dddddddd-0000-0000-0000-000000000001', 'bbbbbbbb-0000-0000-0000-000000000001', '2026-02-10');
insert into app.negotiation_meeting (id, case_id, held_on)
values ('dddddddd-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-000000000002', '2026-03-10');

do $$
begin
    begin
        insert into app.meeting_agreement (case_id, petition_id, meeting_id, agreed_on, number_int, title, body)
        values ('bbbbbbbb-0000-0000-0000-000000000001',
                'cccccccc-0000-0000-0000-000000000001',
                'dddddddd-0000-0000-0000-000000000002',
                '2026-02-10', 1, 'Cross Case', 'Body');
        raise notice 'FAIL: D1 meeting from another case accepted';
    exception when foreign_key_violation then
        raise notice 'PASS: D1 composite FK rejects a meeting belonging to a different case';
    end;
end $$;

do $$
begin
    begin
        insert into app.meeting_agreement (case_id, petition_id, meeting_id, agreed_on, number_int, title, body)
        values ('bbbbbbbb-0000-0000-0000-000000000001',
                'cccccccc-0000-0000-0000-000000000002',
                'dddddddd-0000-0000-0000-000000000001',
                '2026-02-10', 1, 'Cross Case', 'Body');
        raise notice 'FAIL: D2 petition from another case accepted';
    exception when foreign_key_violation then
        raise notice 'PASS: D2 composite FK rejects a petition belonging to a different case';
    end;
end $$;

do $$
begin
    insert into app.meeting_agreement (case_id, petition_id, meeting_id, agreed_on, number_int, title, body)
    values ('bbbbbbbb-0000-0000-0000-000000000001',
            'cccccccc-0000-0000-0000-000000000001',
            'dddddddd-0000-0000-0000-000000000001',
            '2026-02-10', 1, 'Consistent', 'Body');
    raise notice 'PASS: D3 a petition and meeting from the same case are accepted';
exception when others then
    raise notice 'FAIL: D3 consistent case linkage rejected: %', sqlerrm;
end $$;

do $$
begin
    begin
        insert into app.negotiation_case (company_id, started_on, state, closed_at)
        values ('77777777-7777-7777-7777-777777777777', '2026-04-01', 'Closed', null);
        raise notice 'FAIL: D4 Closed without closed_at accepted';
    exception when check_violation then
        raise notice 'PASS: D4 Closed requires closed_at';
    end;
end $$;

do $$
begin
    begin
        -- Decision Q-4 removed the "Promoted" state: promotion is now recorded by
        -- promoted_agreement_id while the case rests in Closed. An Open case cannot carry one.
        -- Since T11 the guard (not the CHECK) refuses it first, with invalid_parameter_value.
        insert into app.negotiation_case (company_id, started_on, state, closed_at, promoted_agreement_id)
        values ('77777777-7777-7777-7777-777777777777', '2026-05-01', 'Open', null,
                '88888888-8888-8888-8888-888888888888');
        raise notice 'FAIL: D5 an Open case was allowed to carry a promoted agreement';
    exception when invalid_parameter_value then
        raise notice 'PASS: D5 a case that is not Closed cannot carry a promoted agreement (T11 guard)';
    end;
end $$;

-- Decision Q-4: 'Promoted' must no longer be an accepted state value at all.
do $$
begin
    begin
        insert into app.negotiation_case (company_id, started_on, state)
        values ('77777777-7777-7777-7777-777777777777', '2026-06-01', 'Promoted');
        raise notice 'FAIL: D7 the removed Promoted state was accepted';
    exception when invalid_text_representation then
        raise notice 'PASS: D7 Promoted is no longer a valid negotiation state (decision Q-4 applied)';
    end;
end $$;

-- T11: a case cannot be created already promoted, and the terminal rules hold at the database.
do $$
begin
    begin
        insert into app.negotiation_case (company_id, started_on, state, closed_at, promoted_agreement_id)
        values ('30000000-0000-0000-0000-000000000001', '2026-01-01', 'Closed', now(),
                '50000000-0000-0000-0000-000000000001');
        raise notice 'FAIL: D8 a case was inserted already promoted';
    exception when invalid_parameter_value then
        raise notice 'PASS: D8 a case cannot be created already promoted (T11 guard)';
    end;
end $$;

do $$
begin
    begin
        insert into app.union_petition (case_id, number_int, title, body, state)
        values ('bbbbbbbb-0000-0000-0000-000000000001', 9, 'Bad State', 'Body', 'NotAState');
        raise notice 'FAIL: D6 invalid petition state accepted';
    exception when invalid_text_representation then
        raise notice 'PASS: D6 petition state is a native enum; unknown values are rejected by the type system';
    end;
end $$;

\echo '--- E. legal library ---'

insert into app.statute (id, kind, name) values
    ('eeeeeeee-0000-0000-0000-000000000001', 'LabourLaw', 'Fixture Labour Statute');

do $$
begin
    begin
        insert into app.statute (kind, name) values ('LabourLaw', 'Second Labour Statute');
        raise notice 'FAIL: E1 a second LabourLaw statute was accepted';
    exception when unique_violation then
        raise notice 'PASS: E1 only one LabourLaw statute may exist';
    end;
end $$;

do $$
begin
    insert into app.statute (kind, name) values ('OtherStatute', 'Fixture Other Statute');
    raise notice 'PASS: E2 a second OtherStatute is allowed';
exception when others then
    raise notice 'FAIL: E2 OtherStatute rejected: %', sqlerrm;
end $$;

insert into app.statute_article (statute_id, number_int, title, body)
values ('eeeeeeee-0000-0000-0000-000000000001', 1, 'Article One', 'Body');

do $$
begin
    begin
        insert into app.statute_article (statute_id, number_int, title, body)
        values ('eeeeeeee-0000-0000-0000-000000000001', 1, 'Duplicate Article', 'Body');
        raise notice 'FAIL: E3 duplicate statute article number accepted';
    exception when unique_violation then
        raise notice 'PASS: E3 statute article numbers are unique within a statute';
    end;
end $$;

do $$
begin
    begin
        insert into app.comparison_heading (group_id, name, is_excluded)
        select g.id, 'Excluded In Economic', true
        from app.comparison_heading_group g where lower(g.name) = 'economico';
        raise notice 'FAIL: E4 an excluded heading was accepted in a group requiring economic comparison';
    exception when check_violation then
        raise notice 'PASS: E4 exclusion is contradictory in an economically-compared group and is rejected';
    end;
end $$;

-- T10: the article's own heading is mandatory ...
do $$
begin
    begin
        insert into app.statute_article (statute_id, number_int, title, body)
        values ('eeeeeeee-0000-0000-0000-000000000001', 2, '   ', 'Body');
        raise notice 'FAIL: E5 a statute article with a blank heading was accepted';
    exception when check_violation then
        raise notice 'PASS: E5 statute_article_title_not_blank refuses a blank article heading (T10)';
    end;
end $$;

do $$
begin
    begin
        insert into app.statute (kind, name) values ('OtherStatute', '   ');
        raise notice 'FAIL: E6 a statute with a blank name was accepted';
    exception when check_violation then
        raise notice 'PASS: E6 statute_name_not_blank refuses a blank statute name';
    end;
end $$;

do $$
begin
    begin
        insert into app.statute_article (statute_id, number_int, title, body)
        values ('eeeeeeee-0000-0000-0000-000000000001', 0, 'Article Zero', 'Body');
        raise notice 'FAIL: E7 a statute article numbered 0 was accepted';
    exception when check_violation then
        raise notice 'PASS: E7 statute_article_number_positive refuses article number 0';
    end;
end $$;

-- ... and the CLASSIFICATION is not (approval item A-12): 89.6% of the legacy corpus is
-- unclassified, so requiring a heading would block loading the library.
do $$
begin
    insert into app.statute_article (statute_id, number_int, title, body, heading_id)
    values ('eeeeeeee-0000-0000-0000-000000000001', 3, 'Unclassified Article', 'Body', null);
    raise notice 'PASS: E8 an unclassified statute article is accepted (A-12)';
exception when others then
    raise notice 'FAIL: E8 an unclassified article was rejected: %', sqlerrm;
end $$;

-- The statute name is unique case-insensitively: 'Fixture Other Statute' exists from E2.
do $$
begin
    begin
        insert into app.statute (kind, name) values ('OtherStatute', 'FIXTURE OTHER STATUTE');
        raise notice 'FAIL: E9 a duplicate statute name was accepted';
    exception when unique_violation then
        raise notice 'PASS: E9 statute_name_key refuses a duplicate name, case-insensitively';
    end;
end $$;

do $$
begin
    begin
        insert into app.statute_article (statute_id, number_int, title, body)
        values ('eeeeeeee-0000-0000-0000-000000000001', 4, 'Article Four', '   ');
        raise notice 'FAIL: E10 a blank article body was accepted';
    exception when check_violation then
        raise notice 'PASS: E10 statute_article_body_not_blank refuses a blank article body';
    end;
end $$;

\echo '--- F. documents ---'

do $$
begin
    insert into platform.user_account (id, email, display_name)
    values ('ffffffff-0000-0000-0000-000000000001', 'fixture@example.test', 'Fixture User');

    begin
        insert into platform.document (storage_path, file_name, content_type, size_bytes, uploaded_by)
        values ('fixture/x.exe', 'x.exe', 'application/x-msdownload', 10, 'ffffffff-0000-0000-0000-000000000001');
        raise notice 'FAIL: F1 disallowed content type accepted';
    exception when check_violation then
        raise notice 'PASS: F1 document content type allow-list enforced in the database';
    end;

    begin
        insert into platform.document (storage_path, file_name, content_type, size_bytes, uploaded_by)
        values ('fixture/big.pdf', 'big.pdf', 'application/pdf', 20971521, 'ffffffff-0000-0000-0000-000000000001');
        raise notice 'FAIL: F2 a 20 MiB + 1 byte document was accepted';
    exception when check_violation then
        raise notice 'PASS: F2 document size limit (20 MiB) enforced in the database';
    end;

    insert into platform.document (storage_path, file_name, content_type, size_bytes, uploaded_by)
    values ('fixture/ok.pdf', 'ok.pdf', 'application/pdf', 1024, 'ffffffff-0000-0000-0000-000000000001');
    raise notice 'PASS: F3 a valid PDF document is accepted';
exception when others then
    raise notice 'FAIL: F fixtures failed: %', sqlerrm;
end $$;

\echo '--- G. audit immutability ---'

insert into platform.audit_entry (actor_id, module, entity_type, entity_id, action)
values ('ffffffff-0000-0000-0000-000000000001', 'agreements', 'Agreement', 'fixture', 'Created');

do $$
begin
    begin
        update platform.audit_entry set action = 'Tampered' where entity_id = 'fixture';
        raise notice 'FAIL: G1 audit entry was updatable';
    exception when restrict_violation then
        raise notice 'PASS: G1 audit_entry rejects UPDATE';
    end;
end $$;

do $$
begin
    begin
        delete from platform.audit_entry where entity_id = 'fixture';
        raise notice 'FAIL: G2 audit entry was deletable';
    exception when restrict_violation then
        raise notice 'PASS: G2 audit_entry rejects DELETE';
    end;
end $$;

\echo '--- H. delete behaviour ---'

do $$
begin
    begin
        delete from app.company where id = '77777777-7777-7777-7777-777777777777';
        raise notice 'FAIL: H1 company with agreements was deleted';
    exception when foreign_key_violation then
        raise notice 'PASS: H1 ON DELETE RESTRICT protects a company that has agreements';
    end;
end $$;

do $$
declare
    v_before int;
    v_after  int;
begin
    select count(*) into v_before from app.agreement_clause
     where agreement_id = '99999999-9999-9999-9999-999999999999';
    delete from app.agreement where id = '99999999-9999-9999-9999-999999999999';
    select count(*) into v_after from app.agreement_clause
     where agreement_id = '99999999-9999-9999-9999-999999999999';
    if v_before >= 1 and v_after = 0 then
        raise notice 'PASS: H2 ON DELETE CASCADE removed % clause(s) with their agreement', v_before;
    else
        raise notice 'FAIL: H2 cascade did not behave: before=% after=%', v_before, v_after;
    end if;
end $$;

do $$
begin
    begin
        delete from app.agreement_clause where agreement_id = '88888888-8888-8888-8888-888888888888';
        delete from app.comparison_heading where id = 'aaaaaaaa-0000-0000-0000-000000000001';
        raise notice 'PASS: H3 a heading with no remaining clauses is deletable';
    exception when foreign_key_violation then
        raise notice 'FAIL: H3 heading delete blocked while clauses still referenced it';
    end;
end $$;

-- --- F. publishing (T12) -------------------------------------------------------------------------

-- The news publication guard: an article is created as a draft, and changing the publication state
-- requires news.publish even for the API role.
do $$
declare
    category uuid;
begin
    insert into app.news_category (name) values ('T12 constraint category') returning id into category;

    begin
        insert into app.news_article (category_id, title, body, state, published_at, published_by)
        values (category, 'Título', 'Cuerpo', 'Published', now(),
                '20000000-0000-0000-0000-000000000001');
        raise notice 'FAIL: N1 a news article was inserted already published';
    exception when invalid_parameter_value then
        raise notice 'PASS: N1 a news article is created as a draft (T12 guard)';
    end;

    insert into app.news_article (category_id, title, body) values (category, 'Título', 'Cuerpo');

    begin
        update app.news_article set state = 'Published' where category_id = category;
        raise notice 'FAIL: N2 publishing without news.publish was accepted';
    exception when insufficient_privilege then
        raise notice 'PASS: N2 publishing requires news.publish at the database';
    end;

    begin
        update app.news_article set state = 'Draft' where category_id = category and state = 'Published';
        raise notice 'PASS: N3 a no-op state write on a draft is not a transition';
    exception when insufficient_privilege then
        raise notice 'FAIL: N3 a no-op state write was treated as a transition';
    end;
end $$;

rollback;

\echo ''
\echo '--- all constraint tests executed; transaction rolled back, database unchanged ---'
