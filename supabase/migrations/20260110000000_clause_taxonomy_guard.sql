-- =====================================================================================
-- 20260110000000 — clause taxonomy: no parent cycles
--
-- Unique keys and the parent foreign key are already in the schema. A self-parent or a longer
-- cycle would still be accepted by a plain FK, so this trigger walks the ancestors and rejects it.
-- =====================================================================================

create or replace function platform.clause_category_guard_cycle()
returns trigger
language plpgsql
as $$
declare
    v_cycle boolean;
begin
    if new.parent_id is null then
        return new;
    end if;

    if new.parent_id = new.id then
        raise exception 'a clause category cannot be its own parent'
            using errcode = '23514';
    end if;

    with recursive ancestors(id) as (
        select c.parent_id
        from platform.clause_category c
        where c.id = new.parent_id
        union all
        select c.parent_id
        from platform.clause_category c
        join ancestors a on c.id = a.id
        where c.parent_id is not null
    )
    select exists (select 1 from ancestors where id = new.id) into v_cycle;

    if v_cycle then
        raise exception 'a clause category parent cycle is not allowed'
            using errcode = '23514';
    end if;

    return new;
end $$;

create trigger clause_category_guard_cycle
    before insert or update of parent_id on platform.clause_category
    for each row execute function platform.clause_category_guard_cycle();
