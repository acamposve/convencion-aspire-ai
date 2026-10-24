-- =====================================================================================
-- verify.sql — structural verification of the target schema
--
-- Read-only. Reports what exists; asserts nothing that requires application behaviour.
-- Run against a freshly migrated database: psql -v ON_ERROR_STOP=1 -f verify.sql
-- =====================================================================================

\pset footer off
\timing off

\echo ''
\echo '=== 1. Schemas ==='
select nspname as schema, pg_get_userbyid(nspowner) as owner
from pg_namespace
where nspname in ('app', 'platform', 'public')
order by nspname;

\echo ''
\echo '=== 2. Table inventory (31 expected: app=23, platform=8) ==='
select table_schema, count(*) as tables
from information_schema.tables
where table_type = 'BASE TABLE' and table_schema in ('app', 'platform')
group by table_schema
order by table_schema;

\echo ''
\echo '=== 3. Every table, with column count and RLS state ==='
select c.relnamespace::regnamespace::text as schema,
       c.relname                          as table_name,
       (select count(*) from information_schema.columns col
         where col.table_schema = c.relnamespace::regnamespace::text
           and col.table_name   = c.relname)          as columns,
       c.relrowsecurity                   as rls_enabled,
       c.relforcerowsecurity              as rls_forced
from pg_class c
where c.relkind = 'r'
  and c.relnamespace::regnamespace::text in ('app', 'platform')
order by 1, 2;

\echo ''
\echo '=== 4. Foreign keys: count, and every one with its ON DELETE / ON UPDATE rule ==='
select count(*) as foreign_key_count
from information_schema.table_constraints
where constraint_type = 'FOREIGN KEY'
  and table_schema in ('app', 'platform');

select con.conname                                          as constraint_name,
       con.conrelid::regclass::text                          as child,
       con.confrelid::regclass::text                         as parent,
       case con.confdeltype when 'a' then 'NO ACTION' when 'r' then 'RESTRICT'
                            when 'c' then 'CASCADE'  when 'n' then 'SET NULL'
                            when 'd' then 'SET DEFAULT' end as on_delete,
       case con.confupdtype when 'a' then 'NO ACTION' when 'r' then 'RESTRICT'
                            when 'c' then 'CASCADE'  when 'n' then 'SET NULL'
                            when 'd' then 'SET DEFAULT' end as on_update
from pg_constraint con
where con.contype = 'f'
order by 2, 1;

\echo ''
\echo '=== 5. Any FK without an explicit delete rule (must be 0) ==='
select count(*) as fks_with_default_action
from pg_constraint
where contype = 'f' and (confdeltype = 'a' or confupdtype = 'a');

\echo ''
\echo '=== 6. Unique constraints and unique indexes ==='
select schemaname, tablename, indexname
from pg_indexes
where schemaname in ('app', 'platform')
  and indexdef like '%UNIQUE%'
order by 1, 2, 3;

\echo ''
\echo '=== 7. CHECK constraints ==='
select conrelid::regclass::text as table_name, conname
from pg_constraint
where contype = 'c'
  and conrelid::regnamespace::text in ('app', 'platform')
order by 1, 2;

\echo ''
\echo '=== 8. Enum types ==='
select t.typnamespace::regnamespace::text as schema, t.typname, string_agg(e.enumlabel, ' | ' order by e.enumsortorder) as values
from pg_type t
join pg_enum e on e.enumtypid = t.oid
where t.typnamespace::regnamespace::text in ('app', 'platform')
group by 1, 2
order by 1, 2;

\echo ''
\echo '=== 9. RLS policies per table ==='
select schemaname, tablename, count(*) as policies
from pg_policies
where schemaname in ('app', 'platform')
group by 1, 2
order by 1, 2;

\echo ''
\echo '=== 10. Tables WITHOUT row level security (must be empty) ==='
select c.relnamespace::regnamespace::text as schema, c.relname
from pg_class c
where c.relkind = 'r'
  and c.relnamespace::regnamespace::text in ('app', 'platform')
  and not c.relrowsecurity;

\echo ''
\echo '=== 11. Legacy table names must NOT exist (no mechanical copy) ==='
select count(*) as legacy_named_tables
from information_schema.tables
where table_schema in ('app', 'platform')
  and table_name in ('empresas','contratos','articulos_contratos','anexos_contratos','bitacoras',
                     'peticiones_sindicato','ofertas_patrono','reuniones','acuerdos','otras_leyes',
                     'ley_trabajo','articulos_ley_trabajo','articulos_otras_leyes','titulos',
                     'categorias_titulos','paises','estados','localidades','ciudades','usuarios',
                     'pantallas','seguridades','modulos','noticias','links_interes');

\echo ''
\echo '=== 12. Legacy column names must NOT exist (no mechanical copy) ==='
select count(*) as legacy_named_columns
from information_schema.columns
where table_schema in ('app', 'platform')
  and column_name in ('codigo_empresa','codigo_contrato','codigo_bitacora','status_publicacion',
                      'estatus_bitacora','status_articulo','articulo_cerrado','campo_comparativo',
                      'nro_articulos','total_articulos','Clave_usuario');

\echo ''
\echo '=== 13. Row counts (all must be 0 except seeded reference data) ==='
select table_schema || '.' || table_name as table_name,
       (xpath('/row/c/text()',
              query_to_xml(format('select count(*) as c from %I.%I', table_schema, table_name),
                           false, true, '')))[1]::text::int as row_count
from information_schema.tables
where table_type = 'BASE TABLE' and table_schema in ('app', 'platform')
order by 1;

\echo ''
\echo '=== 14. Triggers ==='
select c.relnamespace::regnamespace::text as schema,
       c.relname as table_name,
       t.tgname as trigger_name,
       case when t.tgtype & 2 = 2 then 'BEFORE' else 'AFTER' end as timing
from pg_trigger t
join pg_class c on c.oid = t.tgrelid
where not t.tgisinternal
  and c.relnamespace::regnamespace::text in ('app', 'platform')
order by 1, 2, 3;
