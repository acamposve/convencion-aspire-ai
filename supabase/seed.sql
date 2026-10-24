-- =====================================================================================
-- seed.sql — essential reference data only
--
-- What is here: capabilities, the four roles and their grants, the comparison heading
-- classification seed, and one country.
--
-- What is deliberately NOT here:
--   * any user account. Per constitution Art. VI.6 the single SuperAdmin is created once through
--     POST /auth/bootstrap; no seeded user of any kind exists, in any environment.
--   * any legacy record. No row from presenci_cccol.sql is reproduced (constitution Art. V.2).
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
-- Roles. Four work-based roles, replacing the legacy six which mixed work functions with feature
-- bundles and contradicted their own names (BR-19, approval item A-36).
-- -------------------------------------------------------------------------------------
insert into platform.role (key, name, description, is_system)
select v.key, v.name, v.description, v.is_system
from (values
    ('administrator', 'Administrator', 'Manages users, roles and all business data', true),
    ('editor',        'Editor',        'Authors, classifies and publishes agreements and statutes', false),
    ('analyst',       'Analyst',       'Reads agreements, statutes and comparisons', false),
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
    -- Administrator: everything except system.superadmin, which is granted only at bootstrap
    ('administrator', 'company.read'), ('administrator', 'company.write'),
    ('administrator', 'agreement.read'), ('administrator', 'agreement.write'), ('administrator', 'agreement.publish'),
    ('administrator', 'negotiation.read'), ('administrator', 'negotiation.write'), ('administrator', 'negotiation.promote'),
    ('administrator', 'comparison.read'),
    ('administrator', 'statute.read'), ('administrator', 'statute.write'),
    ('administrator', 'news.read'), ('administrator', 'news.write'), ('administrator', 'news.publish'),
    ('administrator', 'catalog.read'), ('administrator', 'catalog.write'),
    ('administrator', 'document.read'), ('administrator', 'document.write'),
    ('administrator', 'user.manage'), ('administrator', 'role.manage'), ('administrator', 'audit.read'),

    -- Editor: authors and publishes content
    ('editor', 'company.read'), ('editor', 'company.write'),
    ('editor', 'agreement.read'), ('editor', 'agreement.write'), ('editor', 'agreement.publish'),
    ('editor', 'comparison.read'),
    ('editor', 'statute.read'), ('editor', 'statute.write'),
    ('editor', 'news.read'), ('editor', 'news.write'), ('editor', 'news.publish'),
    ('editor', 'catalog.read'), ('editor', 'catalog.write'),
    ('editor', 'document.read'), ('editor', 'document.write'),

    -- Analyst: read-only, including the audit trail
    ('analyst', 'company.read'),
    ('analyst', 'agreement.read'),
    ('analyst', 'negotiation.read'),
    ('analyst', 'comparison.read'),
    ('analyst', 'statute.read'),
    ('analyst', 'news.read'),
    ('analyst', 'catalog.read'),
    ('analyst', 'document.read'),
    ('analyst', 'audit.read'),

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
