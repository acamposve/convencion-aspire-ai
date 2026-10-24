-- =====================================================================================
-- 20260101000400 — app: comparison taxonomy
--
-- This is the product's classification backbone. Every agreement clause must reference a heading
-- (approval item A-16), and the comparison query groups clauses by heading.
--
-- Two legacy defects are corrected structurally:
--   * the taxonomy was two tables joined by a column that one SELECT forgot to project
--     (titulos.php:257 read a column it never selected)
--   * "not comparable" was expressed as a heading literally named "No Compara" holding 853
--     clauses, i.e. content inside the taxonomy rather than a property of it
-- =====================================================================================

create table app.comparison_heading_group (
    id                            uuid        primary key default gen_random_uuid(),
    name                          text        not null,
    description                   text,
    requires_economic_comparison  boolean     not null default false,
    sort_order                    integer     not null default 0,
    is_active                     boolean     not null default true,
    created_at                    timestamptz not null default now(),
    updated_at                    timestamptz not null default now(),

    constraint comparison_heading_group_name_not_blank check (btrim(name) <> '')
);

create unique index comparison_heading_group_name_key
    on app.comparison_heading_group (lower(name));

create index comparison_heading_group_order_idx
    on app.comparison_heading_group (sort_order, name);

create trigger comparison_heading_group_set_updated_at
    before update on app.comparison_heading_group
    for each row execute function platform.set_updated_at();

comment on column app.comparison_heading_group.requires_economic_comparison is
  'Was char(2) holding ''si''/''no'' in the legacy schema. A real boolean now. Advisory in the MVP: '
  'the legacy system never enforced it and no code read the value it claimed to require (BR-05).';

create table app.comparison_heading (
    id          uuid        primary key default gen_random_uuid(),
    group_id    uuid        not null references app.comparison_heading_group (id) on delete restrict on update restrict,
    name        text        not null,
    description text,
    is_excluded boolean     not null default false,
    sort_order  integer     not null default 0,
    is_active   boolean     not null default true,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),

    constraint comparison_heading_name_not_blank check (btrim(name) <> '')
);

create unique index comparison_heading_name_key
    on app.comparison_heading (group_id, lower(name));

create index comparison_heading_order_idx
    on app.comparison_heading (group_id, sort_order, name);

create trigger comparison_heading_set_updated_at
    before update on app.comparison_heading
    for each row execute function platform.set_updated_at();

comment on column app.comparison_heading.is_excluded is
  'True means clauses under this heading are not offered in the comparison workspace. Replaces the '
  'legacy heading literally named "No Compara" (853 clauses, 15.8% of all clauses) with a property '
  'of the taxonomy instead of an entry in it.';

-- -------------------------------------------------------------------------------------
-- Advisory-flag consistency
--
-- An excluded heading cannot sensibly demand an economic comparison value. This cannot be a
-- CHECK constraint because it spans two tables, so it is a trigger. Deliberately narrow: it
-- enforces only the contradiction, not a workflow.
-- -------------------------------------------------------------------------------------
create or replace function app.enforce_heading_exclusion_consistency()
returns trigger
language plpgsql
as $$
declare
    v_requires boolean;
begin
    if new.is_excluded then
        select g.requires_economic_comparison
          into v_requires
          from app.comparison_heading_group g
         where g.id = new.group_id;

        if coalesce(v_requires, false) then
            raise exception
                'heading % belongs to a group that requires an economic comparison and therefore cannot be excluded',
                new.name
                using errcode = 'check_violation';
        end if;
    end if;
    return new;
end;
$$;

create trigger comparison_heading_exclusion_consistency
    before insert or update of is_excluded, group_id on app.comparison_heading
    for each row execute function app.enforce_heading_exclusion_consistency();
