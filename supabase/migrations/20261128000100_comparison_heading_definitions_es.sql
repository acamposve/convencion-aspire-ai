-- =====================================================================================
-- 20261128000100 — Definiciones de las materias de comparación, en español (#138a)
--
-- `app.comparison_heading_group.description` existe desde 20260101000400 y el seed la llenó con
-- descripciones cortas en inglés. La interfaz es en español y el cliente pide ver la definición de la
-- materia al comparar (issue #138), así que el dato de referencia se alinea con su vocabulario en el
-- origen: traducirlo en el cliente crearía una segunda fuente de verdad que se desincroniza en el
-- primer cambio de texto.
--
-- Los textos son los del sistema original que el cliente reconoce (demo CCCOL, pantalla
-- «Definiciones»). No son una redacción nueva: son las palabras con las que él agrupa las cláusulas.
--
-- Emparejamiento por TRIM(name), no por id: el nombre es el identificador estable del seed y un
-- espacio sobrante no debe dejar la migración aplicada a medias en silencio. El bloque final cuenta
-- las filas actualizadas y falla si, habiendo taxonomía, no son exactamente 5: un nombre mal escrito
-- rompe el CI en vez de dejar descripciones en inglés.
--
-- Arranque en frío: el migrador aplica **todas** las migraciones y después `supabase/seed.sql`, así que
-- en una base nueva esta migración ve la tabla vacía. Ese caso no es un error y no lanza: las filas y
-- sus definiciones en español las escribe el seed, que ya las lleva. La aserción se aplica cuando la
-- taxonomía existe (staging y cualquier entorno ya desplegado), que es donde una actualización parcial
-- sería silenciosa y dañina.
--
-- Rollback exacto (valores previos del seed):
--   update app.comparison_heading_group set description = 'General clauses applicable across the agreement'
--     where trim(name) = 'GENERALES';
--   update app.comparison_heading_group set description = 'Wage and benefit clauses with a comparable monetary value'
--     where trim(name) = 'ECONOMICO';
--   update app.comparison_heading_group set description = 'Social and welfare benefits'
--     where trim(name) = 'SOCIOECONÓMICAS';
--   update app.comparison_heading_group set description = 'Union rights and contributions'
--     where trim(name) = 'SINDICALES';
--   update app.comparison_heading_group set description = 'Occupational health and safety'
--     where trim(name) = 'SEGURIDAD OCUPACIONAL';
-- =====================================================================================

do $$
declare
    target      record;
    updated     integer := 0;
    step        integer;
    existing    integer;
begin
    select count(*) into existing from app.comparison_heading_group;

    for target in
        select * from (values
            ('ECONOMICO',
             'Se agrupan cláusulas que establecen pagos en dinero, especie o tiempo remunerado, que afectan o impactan Prestaciones Sociales y otros beneficios del trabajador.'),
            ('GENERALES',
             'Se agrupan cláusulas que establecen condiciones y definiciones entre las partes.'),
            ('SEGURIDAD OCUPACIONAL',
             'Se agrupan cláusulas orientadas a la salud y protección del trabajador en el ambiente laboral.'),
            ('SINDICALES',
             'Se agrupan cláusulas que implican pagos en dinero o especie, entregadas a la Institución (Federaciones, Confederaciones y/o Sindicatos).'),
            ('SOCIOECONÓMICAS',
             'Se agrupan cláusulas que establecen pagos en dinero o especie que no afectan o no inciden en Prestaciones Sociales u otros beneficios.')
        ) as definitions(name, description)
    loop
        update app.comparison_heading_group
           set description = target.description
         where trim(name) = target.name;

        get diagnostics step = row_count;
        updated := updated + step;
    end loop;

    -- A brand-new database has no taxonomy yet (the seed comes after every migration), so there is
    -- nothing to update and the seed is the one that writes these definitions.
    if existing > 0 and updated <> 5 then
        raise exception
            'comparison heading definitions: expected exactly 5 rows to be updated, got % out of % '
            'rows in the table. Check the seeded names (TRIM) before re-running.', updated, existing;
    end if;
end $$;
