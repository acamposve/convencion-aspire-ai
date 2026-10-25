-- =====================================================================================
-- 20260105 — T11: negotiation workflow
--
-- Two changes, both required before the negotiation endpoints can exist:
--
--   1. The promotion guard. The previous version (20260102) fired only on UPDATE, only when the
--      previous link was null, and never checked the previous state. That left four holes: a case
--      could be INSERTed already promoted, re-pointed at a different agreement without the
--      capability, promoted twice, or closed and promoted in one statement. This version closes all
--      four and makes "promotion only from an already-closed case" a database invariant, so the API
--      validation is a convenience rather than the guarantee (T11 AC 2, 4, 6).
--
--   2. Company-scoped RLS for the six negotiation tables. They had capability-only policies, so a
--      future handler bug could read another company's negotiation while still satisfying the
--      capability check. Same pattern as 20260104000100 for agreements.
--
-- No table or column changes; no data migration.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- 1. Promotion guard
--
-- SQLSTATEs are chosen so the API can map them without parsing messages:
--   42501 insufficient_privilege   -> 403 (capability negotiation.promote required)
--   22023 invalid_parameter_value  -> 409 invalid_state (wrong source state / terminal case)
--   23505 unique_violation         -> 409 conflict (already promoted)
-- -------------------------------------------------------------------------------------
create or replace function app.guard_case_promotion()
returns trigger
language plpgsql
as $$
begin
    if tg_op = 'INSERT' then
        if new.promoted_agreement_id is not null then
            raise exception 'a negotiation case cannot be created already promoted'
                using errcode = 'invalid_parameter_value';
        end if;
        return new;
    end if;

    -- The promotion transition itself.
    if new.promoted_agreement_id is distinct from old.promoted_agreement_id
       and new.promoted_agreement_id is not null then
        if not platform.has_capability('negotiation.promote') then
            raise exception
                'capability negotiation.promote is required to promote a negotiation case'
                using errcode = 'insufficient_privilege';
        end if;

        if old.state <> 'Closed' then
            raise exception
                'a negotiation case can only be promoted from Closed, not from %', old.state
                using errcode = 'invalid_parameter_value';
        end if;

        if old.promoted_agreement_id is not null then
            raise exception 'the negotiation case is already promoted'
                using errcode = 'unique_violation';
        end if;
    end if;

    -- Promotion is terminal (A-21 / ADR-014): the link cannot be cleared or re-pointed, and the case
    -- cannot leave Closed once promoted.
    if old.promoted_agreement_id is not null
       and (new.promoted_agreement_id is distinct from old.promoted_agreement_id
            or new.state is distinct from old.state) then
        raise exception 'a promoted negotiation case is terminal'
            using errcode = 'invalid_parameter_value';
    end if;

    return new;
end;
$$;

comment on function app.guard_case_promotion() is
  'Promotion requires negotiation.promote, an already-Closed case, and no previous link; a promoted '
  'case is terminal (A-21). Fires on INSERT as well, so a case cannot be created already promoted.';

drop trigger if exists negotiation_case_promotion_guard on app.negotiation_case;

create trigger negotiation_case_promotion_guard
    before insert or update on app.negotiation_case
    for each row execute function app.guard_case_promotion();

-- -------------------------------------------------------------------------------------
-- 2. Promotion may insert the agreement header
--
-- A negotiator holds negotiation.promote but deliberately not agreement.write (the seeded role is
-- "plus read on agreements"), so the agreement insert policy would refuse the very row promotion
-- creates. This policy admits the promotion path and nothing else, and only as a Draft: a negotiator
-- cannot publish through it.
-- -------------------------------------------------------------------------------------
drop policy if exists agreement_api_promotion_insert on app.agreement;

create policy agreement_api_promotion_insert on app.agreement
    for insert to app_api
    with check (
        platform.is_active_user()
        and platform.has_capability('negotiation.promote')
        and platform.can_access_company(company_id)
        and state = 'Draft'
    );

-- -------------------------------------------------------------------------------------
-- 3. Company scope for the negotiation tables
--
-- The case carries company_id; every child is scoped through it. Anonymous callers keep no
-- access at all (no policies are created for anon).
-- -------------------------------------------------------------------------------------
drop policy if exists negotiation_case_api_read            on app.negotiation_case;
drop policy if exists negotiation_case_authenticated_read  on app.negotiation_case;
drop policy if exists negotiation_case_api_insert          on app.negotiation_case;
drop policy if exists negotiation_case_api_update          on app.negotiation_case;
drop policy if exists negotiation_case_api_delete          on app.negotiation_case;

create policy negotiation_case_api_read on app.negotiation_case
    for select to app_api
    using (platform.is_active_user() and platform.can_access_company(company_id));

create policy negotiation_case_authenticated_read on app.negotiation_case
    for select to authenticated
    using (platform.is_active_user() and platform.can_access_company(company_id));

create policy negotiation_case_api_insert on app.negotiation_case
    for insert to app_api
    with check (
        platform.is_active_user()
        and platform.has_capability('negotiation.write')
        and platform.can_access_company(company_id)
    );

create policy negotiation_case_api_update on app.negotiation_case
    for update to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('negotiation.write')
        and platform.can_access_company(company_id)
    )
    with check (
        platform.is_active_user()
        and platform.has_capability('negotiation.write')
        and platform.can_access_company(company_id)
    );

create policy negotiation_case_api_delete on app.negotiation_case
    for delete to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('negotiation.write')
        and platform.can_access_company(company_id)
    );

-- Children: the parent is the negotiation_case, reached through the column named in each table.
do $$
declare
    t record;
begin
    for t in
        select * from (values
            ('negotiation_party',  'case_id'),
            ('union_petition',     'case_id'),
            ('negotiation_meeting','case_id'),
            ('meeting_agreement',  'case_id')
        ) as v(table_name, parent_column)
    loop
        execute format('drop policy if exists %I on app.%I', t.table_name || '_api_read', t.table_name);
        execute format('drop policy if exists %I on app.%I', t.table_name || '_authenticated_read', t.table_name);
        execute format('drop policy if exists %I on app.%I', t.table_name || '_api_insert', t.table_name);
        execute format('drop policy if exists %I on app.%I', t.table_name || '_api_update', t.table_name);
        execute format('drop policy if exists %I on app.%I', t.table_name || '_api_delete', t.table_name);

        execute format($sql$
            create policy %I on app.%I for select to app_api
            using (platform.is_active_user() and exists (
                select 1 from app.negotiation_case c
                where c.id = %I.%I and platform.can_access_company(c.company_id)
            ))$sql$,
            t.table_name || '_api_read', t.table_name, t.table_name, t.parent_column);

        execute format($sql$
            create policy %I on app.%I for select to authenticated
            using (platform.is_active_user() and exists (
                select 1 from app.negotiation_case c
                where c.id = %I.%I and platform.can_access_company(c.company_id)
            ))$sql$,
            t.table_name || '_authenticated_read', t.table_name, t.table_name, t.parent_column);

        execute format($sql$
            create policy %I on app.%I for insert to app_api
            with check (
                platform.is_active_user()
                and platform.has_capability('negotiation.write')
                and exists (
                    select 1 from app.negotiation_case c
                    where c.id = %I.%I and platform.can_access_company(c.company_id)
                )
            )$sql$,
            t.table_name || '_api_insert', t.table_name, t.table_name, t.parent_column);

        execute format($sql$
            create policy %I on app.%I for update to app_api
            using (
                platform.is_active_user()
                and platform.has_capability('negotiation.write')
                and exists (
                    select 1 from app.negotiation_case c
                    where c.id = %I.%I and platform.can_access_company(c.company_id)
                )
            )
            with check (
                platform.is_active_user()
                and platform.has_capability('negotiation.write')
                and exists (
                    select 1 from app.negotiation_case c
                    where c.id = %I.%I and platform.can_access_company(c.company_id)
                )
            )$sql$,
            t.table_name || '_api_update', t.table_name, t.table_name, t.parent_column,
            t.table_name, t.parent_column);

        execute format($sql$
            create policy %I on app.%I for delete to app_api
            using (
                platform.is_active_user()
                and platform.has_capability('negotiation.write')
                and exists (
                    select 1 from app.negotiation_case c
                    where c.id = %I.%I and platform.can_access_company(c.company_id)
                )
            )$sql$,
            t.table_name || '_api_delete', t.table_name, t.table_name, t.parent_column);
    end loop;
end $$;

-- employer_offer hangs off the petition, which hangs off the case.
drop policy if exists employer_offer_api_read            on app.employer_offer;
drop policy if exists employer_offer_authenticated_read  on app.employer_offer;
drop policy if exists employer_offer_api_insert          on app.employer_offer;
drop policy if exists employer_offer_api_update          on app.employer_offer;
drop policy if exists employer_offer_api_delete          on app.employer_offer;

create policy employer_offer_api_read on app.employer_offer
    for select to app_api
    using (platform.is_active_user() and exists (
        select 1
        from app.union_petition p
        join app.negotiation_case c on c.id = p.case_id
        where p.id = employer_offer.petition_id and platform.can_access_company(c.company_id)
    ));

create policy employer_offer_authenticated_read on app.employer_offer
    for select to authenticated
    using (platform.is_active_user() and exists (
        select 1
        from app.union_petition p
        join app.negotiation_case c on c.id = p.case_id
        where p.id = employer_offer.petition_id and platform.can_access_company(c.company_id)
    ));

create policy employer_offer_api_insert on app.employer_offer
    for insert to app_api
    with check (
        platform.is_active_user()
        and platform.has_capability('negotiation.write')
        and exists (
            select 1
            from app.union_petition p
            join app.negotiation_case c on c.id = p.case_id
            where p.id = employer_offer.petition_id and platform.can_access_company(c.company_id)
        )
    );

create policy employer_offer_api_update on app.employer_offer
    for update to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('negotiation.write')
        and exists (
            select 1
            from app.union_petition p
            join app.negotiation_case c on c.id = p.case_id
            where p.id = employer_offer.petition_id and platform.can_access_company(c.company_id)
        )
    )
    with check (
        platform.is_active_user()
        and platform.has_capability('negotiation.write')
        and exists (
            select 1
            from app.union_petition p
            join app.negotiation_case c on c.id = p.case_id
            where p.id = employer_offer.petition_id and platform.can_access_company(c.company_id)
        )
    );

create policy employer_offer_api_delete on app.employer_offer
    for delete to app_api
    using (
        platform.is_active_user()
        and platform.has_capability('negotiation.write')
        and exists (
            select 1
            from app.union_petition p
            join app.negotiation_case c on c.id = p.case_id
            where p.id = employer_offer.petition_id and platform.can_access_company(c.company_id)
        )
    );

comment on table app.negotiation_case is
  'A negotiation for one company. The state machine is Open -> InProgress -> Pending -> Agreed -> '
  'Closed; promotion is a separate authorised operation available only for a Closed case, and a '
  'promoted case is terminal (A-21, ADR-014). Company-scoped by RLS since T11.';
