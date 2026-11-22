-- =====================================================================================
-- seed.sql — essential reference data only
--
-- What is here: capabilities, the four roles and their grants, the comparison heading
-- classification seed, and one country.
--
-- What is deliberately NOT here:
--   * any user account. Per constitution Art. VI.6 the single SuperAdmin is created once through
--     POST /auth/bootstrap; no seeded user of any kind exists, in any environment.
--   * any legacy record. The legacy dump is no longer in the repository (T16, docs/operations/legacy-removal.md); no row from it is reproduced (constitution Art. V.2).
--   * the full 64-heading taxonomy, 237 countries, 23 states or 410 municipalities of the legacy
--     data. Reproducing those would be a data migration wearing a seed's clothes. The heading
--     groups are seeded because they are the vocabulary the product cannot operate without, and
--     the business must curate the headings themselves.
--
-- Every statement is idempotent so the file can be re-applied.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- Capabilities (resource.action). Keys are code-owned: the API's endpoint policies name them.
-- -------------------------------------------------------------------------------------
insert into platform.capability (key, description)
select v.key, v.description
from (values
    ('company.read',        'Read companies and their classification'),
    ('company.write',       'Create and modify companies'),
    ('company.read_all',    'Read data belonging to ANY company (cross-company access)'),
    ('agreement.read',      'Read collective agreements and their clauses'),
    ('agreement.write',     'Create and modify collective agreements, clauses and annexes'),
    ('agreement.publish',   'Publish or unpublish a collective agreement'),
    ('negotiation.read',    'Read negotiation cases and their content'),
    ('negotiation.write',   'Create and modify negotiation cases, petitions, offers and meetings'),
    ('negotiation.promote', 'Promote a closed negotiation case to a collective agreement'),
    ('comparison.read',     'Use the comparison workspace'),
    ('statute.read',        'Read statutes and their articles'),
    ('statute.write',       'Create and modify statutes and their articles'),
    ('news.read',           'Read news articles, including drafts'),
    ('news.write',          'Create and modify news articles'),
    ('news.publish',        'Publish or unpublish a news article'),
    ('catalog.read',        'Read catalogues and reference data'),
    ('catalog.write',       'Create and modify catalogues, headings and reference data'),
    ('document.read',       'Download documents'),
    ('document.write',      'Upload and delete documents'),
    ('user.manage',         'Create and manage user accounts'),
    ('role.manage',         'Create and modify roles and their capabilities'),
    ('audit.read',          'Read the audit trail'),
    ('system.superadmin',   'Satisfies every capability check. Granted only to the bootstrap account')
) as v(key, description)
where not exists (
    select 1 from platform.capability c where c.key = v.key
);

-- -------------------------------------------------------------------------------------
-- Roles. Five work-based roles per decision Q-9: SuperAdmin, Admin, Editor, Transcriptor,
-- Negotiator. They replace the legacy six, which mixed work functions with feature bundles and
-- contradicted their own names (BR-19).
--
-- SuperAdmin is the only role holding system.superadmin, and it is granted ONLY to the bootstrap
-- account at first run. This seed creates no user (constitution Art. VI.6), so it also assigns no
-- one to SuperAdmin.
-- -------------------------------------------------------------------------------------
insert into platform.role (key, name, description, is_system)
select v.key, v.name, v.description, v.is_system
from (values
    ('superadmin',    'SuperAdmin',    'Unrestricted access; held only by the bootstrap account', true),
    ('administrator', 'Admin',         'Manages users, roles and all business data', false),
    ('editor',        'Editor',        'Authors, classifies and publishes agreements and statutes', false),
    ('transcriptor',  'Transcriptor',  'Transcribes and classifies clause text; cannot publish', false),
    ('negotiator',    'Negotiator',    'Runs negotiation cases and promotes them to agreements', false)
) as v(key, name, description, is_system)
where not exists (
    select 1 from platform.role r where r.key = v.key
);

-- -------------------------------------------------------------------------------------
-- Role capability grants
-- -------------------------------------------------------------------------------------
insert into platform.role_capability (role_id, capability_id)
select r.id, c.id
from (values
    -- SuperAdmin: every capability, including system.superadmin. Assigned to the bootstrap account
    -- only; this seed grants the role's capabilities but attaches nobody to it (Art. VI.6).
    ('superadmin', 'company.read'), ('superadmin', 'company.write'), ('superadmin', 'company.read_all'),
    ('superadmin', 'agreement.read'), ('superadmin', 'agreement.write'), ('superadmin', 'agreement.publish'),
    ('superadmin', 'negotiation.read'), ('superadmin', 'negotiation.write'), ('superadmin', 'negotiation.promote'),
    ('superadmin', 'comparison.read'),
    ('superadmin', 'statute.read'), ('superadmin', 'statute.write'),
    ('superadmin', 'news.read'), ('superadmin', 'news.write'), ('superadmin', 'news.publish'),
    ('superadmin', 'catalog.read'), ('superadmin', 'catalog.write'),
    ('superadmin', 'document.read'), ('superadmin', 'document.write'),
    ('superadmin', 'clause.read'), ('superadmin', 'clause.process'),
    ('superadmin', 'clause.review'), ('superadmin', 'clause.manage'),
    ('superadmin', 'user.manage'), ('superadmin', 'role.manage'), ('superadmin', 'audit.read'),
    ('superadmin', 'system.superadmin'),

    -- Transcriptor: transcribes and classifies clause text. Deliberately has no *.publish and no
    -- user/role management — the legacy "Transcriptor" held 45 grants including delete on companies
    -- and statutes, which contradicted the role's own name (BR-19).
    ('transcriptor', 'company.read'),
    ('transcriptor', 'agreement.read'), ('transcriptor', 'agreement.write'),
    ('transcriptor', 'comparison.read'),
    ('transcriptor', 'statute.read'), ('transcriptor', 'statute.write'),
    ('transcriptor', 'catalog.read'),
    ('transcriptor', 'document.read'), ('transcriptor', 'document.write'),
    ('transcriptor', 'clause.read'), ('transcriptor', 'clause.process'), ('transcriptor', 'clause.review'),

    -- Administrator: everything except system.superadmin, which is granted only at bootstrap
    ('administrator', 'company.read'), ('administrator', 'company.write'), ('administrator', 'company.read_all'),
    ('administrator', 'agreement.read'), ('administrator', 'agreement.write'), ('administrator', 'agreement.publish'),
    ('administrator', 'negotiation.read'), ('administrator', 'negotiation.write'), ('administrator', 'negotiation.promote'),
    ('administrator', 'comparison.read'),
    ('administrator', 'statute.read'), ('administrator', 'statute.write'),
    ('administrator', 'news.read'), ('administrator', 'news.write'), ('administrator', 'news.publish'),
    ('administrator', 'catalog.read'), ('administrator', 'catalog.write'),
    ('administrator', 'document.read'), ('administrator', 'document.write'),
    ('administrator', 'clause.read'), ('administrator', 'clause.process'), ('administrator', 'clause.review'),
    ('administrator', 'user.manage'), ('administrator', 'role.manage'), ('administrator', 'audit.read'),

    -- Editor: authors and publishes content
    ('editor', 'company.read'), ('editor', 'company.write'),
    ('editor', 'agreement.read'), ('editor', 'agreement.write'), ('editor', 'agreement.publish'),
    ('editor', 'comparison.read'),
    ('editor', 'statute.read'), ('editor', 'statute.write'),
    ('editor', 'news.read'), ('editor', 'news.write'), ('editor', 'news.publish'),
    ('editor', 'catalog.read'), ('editor', 'catalog.write'),
    ('editor', 'document.read'), ('editor', 'document.write'),
    ('editor', 'clause.read'), ('editor', 'clause.process'), ('editor', 'clause.review'),

    -- Analyst: read-only, including the audit trail

    -- Negotiator: runs cases, reads what cases need
    ('negotiator', 'company.read'),
    ('negotiator', 'agreement.read'),
    ('negotiator', 'negotiation.read'), ('negotiator', 'negotiation.write'), ('negotiator', 'negotiation.promote'),
    ('negotiator', 'comparison.read'),
    ('negotiator', 'statute.read'),
    ('negotiator', 'document.read')
) as v(role_key, capability_key)
join platform.role r       on r.key = v.role_key
join platform.capability c on c.key = v.capability_key
where not exists (
    select 1 from platform.role_capability rc
    where rc.role_id = r.id and rc.capability_id = c.id
);

-- -------------------------------------------------------------------------------------
-- Comparison heading groups
--
-- The five blocks are the legacy taxonomy (categorias_titulos, 5 rows) with the economic-comparison
-- flag preserved as a real boolean. Sorting is explicit rather than alphabetical: the legacy read
-- order GENERALES -> ECONOMICO -> SOCIOECONOMICAS -> SINDICALES -> SEGURIDAD OCUPACIONAL is
-- meaningful and was lost by `order by nombre_categoria`.
-- -------------------------------------------------------------------------------------
insert into app.comparison_heading_group (name, description, requires_economic_comparison, sort_order)
select v.name, v.description, v.requires_economic_comparison, v.sort_order
from (values
    ('GENERALES',             'General clauses applicable across the agreement',           false, 10),
    ('ECONOMICO',             'Wage and benefit clauses with a comparable monetary value',  true,  20),
    ('SOCIOECONÓMICAS',       'Social and welfare benefits',                                true,  30),
    ('SINDICALES',            'Union rights and contributions',                             true,  40),
    ('SEGURIDAD OCUPACIONAL', 'Occupational health and safety',                             true,  50)
) as v(name, description, requires_economic_comparison, sort_order)
where not exists (
    select 1 from app.comparison_heading_group g where lower(g.name) = lower(v.name)
);

-- -------------------------------------------------------------------------------------
-- One heading: "No Compara"
--
-- The legacy taxonomy contained a heading literally named "No Compara" which held 853 clauses
-- (15.8% of all clauses). Here it exists as an explicitly excluded heading so that the concept is
-- representable from day one AND modelled in the right place: a property of the taxonomy, not an
-- entry masquerading as a topic.
-- -------------------------------------------------------------------------------------
insert into app.comparison_heading (group_id, name, description, is_excluded, sort_order)
select g.id, 'No Compara', 'Clauses deliberately excluded from the comparison workspace', true, 0
from app.comparison_heading_group g
where lower(g.name) = lower('GENERALES')
  and not exists (
      select 1 from app.comparison_heading h
      where h.group_id = g.id and lower(h.name) = lower('No Compara')
  );

-- -------------------------------------------------------------------------------------
-- Curated headings: the minimum taxonomy the product needs to work end to end
--
-- Until these rows exist, a clause cannot be authored against a comparable heading and the comparison
-- workspace has nothing to show: "No Compara" is deliberately excluded, so it cannot carry comparable
-- content. Found by the T19 release smoke gate on staging (2026-11-22).
--
-- This is a starting point, not the final catalog: the business curates the reference taxonomy
-- (constitution Art. V.3). It lives in the seed rather than in a migration because it is reference data,
-- and because a restore applies migrations only: reference rows travel inside the backup set instead.
-- Names are kept clear of the ones the test fixtures create for themselves.
-- -------------------------------------------------------------------------------------
insert into app.comparison_heading (group_id, name, description, is_excluded, sort_order)
select g.id, v.name, v.description, false, v.sort_order
from (values
    (10, 'Jornada y horario',              'Working time, calendar and its distribution',              10),
    (10, 'Vacaciones anuales',             'Annual leave and how it is scheduled',                     20),
    (10, 'Permisos y licencias',           'Paid and unpaid leave',                                   30),
    (10, 'Régimen disciplinario',          'Disciplinary regime and sanctions',                        40),
    (10, 'Subrogación',                    'Subrogation of the workforce when the contractor changes', 50),
    (20, 'Estructura salarial',            'Salary tables and pay structure',                          10),
    (20, 'Pagas extraordinarias',          'Extra payments and their accrual',                         20),
    (20, 'Complementos y pluses',          'Allowances, bonuses and supplements',                      30),
    (20, 'Gastos y dietas',                'Travel expenses and per diems',                            40),
    (30, 'Mejoras sociales',               'Social improvements and benefits',                          10),
    (30, 'Seguros y previsión social',     'Insurance and social protection',                           20),
    (40, 'Derechos sindicales',            'Union rights and guarantees',                              10),
    (40, 'Comité de empresa y delegados',  'Works council and union delegates',                        20),
    (50, 'Prevención de riesgos laborales', 'Occupational risk prevention',                            10),
    (50, 'Vigilancia de la salud',         'Health surveillance',                                      20)
) as v(group_sort, name, description, sort_order)
join app.comparison_heading_group g on g.sort_order = v.group_sort
where not exists (
    select 1 from app.comparison_heading h where h.group_id = g.id and lower(h.name) = lower(v.name)
);

-- -------------------------------------------------------------------------------------
-- Country
--
-- One row only. The legacy data had 237 countries, 23 states and 410 municipalities; importing
-- them would be a data migration. The business must curate geography in the application.
-- -------------------------------------------------------------------------------------
insert into app.country (code, name)
select 'VE', 'Venezuela'
where not exists (
    select 1 from app.country c where c.code = 'VE'
);
