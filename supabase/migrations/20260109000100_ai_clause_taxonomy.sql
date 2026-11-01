-- =====================================================================================
-- 20260109000100 — initial clause taxonomy
--
-- Design §4.5: 14 flat keys. Configuration data, not business rows; idempotent so it can run
-- against an environment where an operator already added or renamed a category.
-- =====================================================================================

insert into platform.clause_category (key, name, description, sort_order)
values
    ('parties',         'Partes y definiciones',        'Partes contratantes, definiciones y ámbito del convenio', 10),
    ('scope',           'Alcance y obligaciones',       'Objeto, alcance y obligaciones de las partes', 20),
    ('pricing',         'Precio y pago',                'Salarios, aumentos, pagos y condiciones económicas', 30),
    ('duration',        'Duración y renovación',        'Vigencia, prórroga y renovación', 40),
    ('termination',     'Terminación',                  'Causas y efectos de la terminación', 50),
    ('confidentiality', 'Confidencialidad',             'Reserva y manejo de información', 60),
    ('data_protection', 'Protección de datos',          'Tratamiento de datos personales', 70),
    ('ip',              'Propiedad intelectual',        'Titularidad y uso de propiedad intelectual', 80),
    ('liability',       'Responsabilidad e indemnización', 'Responsabilidad, daños e indemnización', 90),
    ('warranties',      'Garantías',                    'Garantías y declaraciones', 100),
    ('disputes',        'Resolución de disputas',       'Procedimientos de solución de controversias', 110),
    ('governing_law',   'Ley aplicable',                'Ley y jurisdicción aplicables', 120),
    ('force_majeure',   'Fuerza mayor',                 'Eventos de fuerza mayor y sus efectos', 130),
    ('other',           'Otros / Sin clasificar',       'Cláusulas sin categoría identificable', 999)
on conflict (key) do nothing;
