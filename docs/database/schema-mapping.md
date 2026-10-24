# Schema mapping — legacy to proposed target

**Status: proposal for review. Not a migration plan, not DDL, not a final design.**

No migration script, no `CREATE TABLE`, and no EF Core model is produced here. This document maps
each legacy entity to a proposed target entity, states the transformation and the reasoning, and
**flags every mapping that depends on an unanswered question** in
[../legacy/open-questions.md](../legacy/open-questions.md).

Read the ⚠ markers as gates: a mapping marked ⚠ cannot be finalised until its question is answered.
Mappings without ⚠ are determined by the evidence and can be treated as settled.

Naming follows constitution Art. X: English identifiers, `snake_case` in PostgreSQL, Spanish domain
terms translated.

---

## 1. Principles applied

Derived from constitution Art. V and the Phase 1/2 findings:

| Principle | Consequence |
|---|---|
| Design, do not translate | No table survives because it existed; each is justified by a requirement |
| Real constraints | FKs, `NOT NULL`, `UNIQUE`, `CHECK` declared in the database |
| `NULL` means absent | The 117 `''`/`'0'` defaults disappear; absence becomes `NULL` |
| Honest types | `date`, `timestamptz`, `boolean`, enums, `numeric` where the domain needs them |
| No data migration | This mapping defines *shape*, not content. Seed data is reference-only (Art. V.3) |
| Auditability designed in | Uniform created/updated metadata; explicit audit log for state changes |
| Permissions are capabilities | No coupling to a UI filename (DS-20) |

---

## 2. Master mapping — all 35 legacy tables

Disposition legend: **MAP** (1:1 entity, redesigned) · **SPLIT** (one table becomes several) ·
**MERGE** (several tables become one) · **FOLD** (absorbs into a parent concept) ·
**DROP** (no target) · **REPLACE** (superseded by a different mechanism).

| # | Legacy table | Rows | Target entity | Disposition |
|---|---|---|---|---|
| 1 | `empresas` | 80 | `Company` (+ `CompanyAddress`, `CompanyContact`) | SPLIT |
| 2 | `contratos` | 73 | `CollectiveAgreement` | MAP ⚠Q-1 |
| 3 | `articulos_contratos` | 5,402 | `AgreementClause` | MAP ⚠Q-2 |
| 4 | `anexos_contratos` | 29 | `AgreementAnnex` | MAP |
| 5 | `bitacoras` | 2 | `NegotiationCase` + `NegotiationParty` | SPLIT |
| 6 | `peticiones_sindicato` | 9 | `UnionPetition` | MAP ⚠Q-5 |
| 7 | `ofertas_patrono` | 4 | `EmployerOffer` | MAP |
| 8 | `reuniones` | 2 | `NegotiationMeeting` | MAP |
| 9 | `acuerdos` | 1 | `MeetingAgreement` | MAP ⚠Q-4 |
| 10 | `reuniones_peticiones` | 4 | `MeetingPetition` (link) | MERGE ⚠Q-17 |
| 11 | `reuniones_ofertas` | 4 | *(derived)* | MERGE ⚠Q-17 |
| 12 | `reuniones_acuerdos` | 1 | *(derived)* | MERGE ⚠Q-17 |
| 13 | `otras_leyes` | 47 | `Statute` | MERGE ⚠Q-6 |
| 14 | `ley_trabajo` | 1 | `Statute` (kind = LABOUR_LAW) | FOLD ⚠Q-6 |
| 15 | `articulos_otras_leyes` | 2,982 | `StatuteArticle` | MERGE |
| 16 | `articulos_ley_trabajo` | 556 | `StatuteArticle` | MERGE |
| 17 | `categorias_titulos` | 5 | `ComparisonHeadingGroup` | MAP |
| 18 | `titulos` | 64 | `ComparisonHeading` | MAP ⚠Q-15 |
| 19 | `paises` | 237 | `Country` | MAP (seed) |
| 20 | `estados` | 23 | `State` | MAP (seed) |
| 21 | `localidades` | 410 | `Municipality` | MAP (seed) |
| 22 | `ciudades` | **0** | — | DROP |
| 23 | `sectores_empresas` | 3 | `CompanySector` | MAP |
| 24 | `tipo_empresa` | 11 | `CompanyType` | MAP |
| 25 | `categoria_sector` | 18 | `CompanyCategory` | MAP |
| 26 | `actividad_empresa` | 21 | `EconomicActivity` | MAP |
| 27 | `categorias` | 8 | — | REPLACE (menu grouping → React navigation) |
| 28 | `modulos` | 24 | — | REPLACE (menu grouping → React navigation) |
| 29 | `pantallas` | 136 | `Capability` | REPLACE ⚠Q-8 |
| 30 | `seguridades` | 146 | `RoleCapability` | REPLACE ⚠Q-8 |
| 31 | `tipos_de_usuarios` | 6 | `Role` | MAP ⚠Q-9 |
| 32 | `usuarios` | 4 | `UserAccount` (Supabase Auth) + `UserProfile` | SPLIT ⚠Q-9 |
| 33 | `noticias` | 63 | `NewsArticle` | MAP ⚠Q-14 |
| 34 | `categoria_noticias` | 5 | `NewsCategory` | MAP |
| 35 | `links_interes` | 13 | `LinkOfInterest` | MAP ⚠Q-16 |

**Dropped outright:** `ciudades` (0 rows, referenced by nothing, a third geography level that was
never used). **Replaced:** `categorias`, `modulos`, `pantallas`, `seguridades`, `usuarios_login` (the
last one never existed).

**New entities with no legacy counterpart** — required by the constitution or by findings:

| Target entity | Why it must be new |
|---|---|
| `AuditLog` | No audit existed; constitution Art. V.7 requires it where a requirement calls for it |
| `Capability` + `RoleCapability` | Permissions keyed on PHP filenames cannot survive (DS-20) |
| User identity tables | Supabase Auth owns credentials; the legacy `Clave_usuario` is plaintext (DS-31) |
| Storage reference for documents | Constitution Art. III mandates Supabase Storage; legacy stored filenames in `text` (DS-11) |
| Uniform audit columns | Only 4 of 35 legacy tables had any (DS-30) |

---

## 3. Agreements and clauses

### 3.1 `contratos` → `CollectiveAgreement` ⚠Q-1

| Legacy | Target | Transformation |
|---|---|---|
| `codigo_contrato int AI` | `id` | Surrogate key, kept |
| — | `title` / `number` | **New.** No legacy source; depends on Q-1 |
| `codigo_empresa int` | `company_id` | FK to `Company.id`; legacy dual identity collapsed (§3.4) |
| `fecha_de_inicio date NULL` | `valid_from date` | `NOT NULL`; zero dates → `NULL` first |
| `fecha_de_termino date NULL` | `valid_to date` | `NOT NULL`; add `CHECK (valid_to >= valid_from)` |
| `duracion varchar(15)` | *derived* | Compute from the date range; legacy free text (`'24 meses'` and `'24 MESES'` coexist) is not authoritative |
| `ambito_aplicacion varchar(150)` | `scope varchar(200)` | Kept; upper-casing applied in code becomes a display concern |
| `status_publicacion char(2)` | `publication_state` enum | `PENDING` / `PUBLISHED`; `CHECK` rejects anything else (DS-15) |
| — | `published_at`, `published_by` | **New** ⚠Q-7 — publication is a governance act with no legacy record |
| `pdf_auto_acta text` | document reference | Moved to Storage; see §6 |

**Reasoning.** The legacy table has no identity beyond its company, which makes agreements
indistinguishable in lists and unusable as a reference — and comparison shows several per company.
Adding a title/number is the one place where the target schema needs *more* than the legacy, and it
is why Q-1 blocks this entity.

### 3.2 `articulos_contratos` → `AgreementClause` ⚠Q-2

| Legacy | Target | Transformation |
|---|---|---|
| `codigo_articulo int AI` | `id` | |
| `codigo_contratos int` | `agreement_id` | FK `ON DELETE CASCADE` (a clause has no meaning without its agreement) |
| `nro_articulos varchar(11)` | `number` + `sort_key` | Depends on Q-2; `sort_key` makes ordering correct regardless |
| `titulo_articulo text` | `title varchar(300)` | `NOT NULL` |
| `resumen_articulo text` | `summary text NULL` | |
| `texto_completo_articulo longtext` | `body text` | Sanitised rich text (Phase 1 §S8) |
| `codigo_titulo_comparativo int` | `heading_id` | FK to `ComparisonHeading`, **nullable** — 899 clauses (16.6%) have none |
| `campo_comparativo varchar(250)` | ⚠Q-3 | Undecided: text, amount, or amount+unit |
| `status_articulo varchar(12)` | — | **Dropped.** All 5,402 rows are `'Pendiente'`; nothing writes it (DS-18) |
| `articulo_cerrado char(1) NULL` | `excluded_from_compilation boolean` | Named for what it does: excludes the clause from the compiled document (Phase 1 BR-07C) ⚠Q-19 |
| — | unique constraint | `UNIQUE (agreement_id, number)` — legacy enforces nothing (BR-07D) |

**Reasoning.** `status_articulo` is deleted rather than migrated because it is provably dead: 5,402
reads of one value and zero writes. `articulo_cerrado` is *kept but renamed*, because it is the only
per-clause behaviour that exists and its name hides what it does.

### 3.3 `anexos_contratos` → `AgreementAnnex`

Direct map. One change: `status_publicacion varchar(15) DEFAULT 'Pendiente'` is **dropped or
unified** — all 29 rows are `'Pendiente'` and no code reads it (DS-16). Recommendation: drop the
column; if annexes need independent publication later, add it then with the same enum as the
agreement.

### 3.4 Company identity collapse — the critical transformation

| Legacy | Target | Transformation |
|---|---|---|
| `empresas.id` (PK, 116–195) | `Company.id` | Survives as the key |
| `empresas.codigo_empresa` (2–115, `NOT NULL`, unwritten by any INSERT) | `Company.legacy_code` (nullable, unique-if-present) | Demoted to **data**, not identity ⚠Q-18 |

**Reasoning.** Zero of 80 rows have `id = codigo_empresa`; 71 of 73 agreements resolve against
`codigo_empresa` and one user row resolves against `id`. Keeping both as keys reproduces an
ambiguity that is already producing wrong joins today. If the business cites the legacy code (on
documents, in correspondence), it must be preserved *as an attribute*; if not, it disappears. This
is Q-18 and it cannot be inferred.

---

## 4. Negotiation

### 4.1 `bitacoras` → `NegotiationCase` + `NegotiationParty` (SPLIT)

The 21-column table splits into a case and a normalised party list:

| Legacy | Target | Transformation |
|---|---|---|
| `codigo_bitacora` | `NegotiationCase.id` | |
| `codigo_empresa` | `company_id` | FK |
| `fecha_inicio_disc text` (`dd/mm/yyyy`) | `started_on date` | Parse; `NOT NULL` |
| `representante_empresa`, `telefono_…`, `email_…`, `cargo_…` | `NegotiationParty` row, `role = EMPLOYER` | 12 columns → rows |
| `representante_sindicato`, … | `NegotiationParty` row, `role = UNION` | |
| `representante_min_trab`, … | `NegotiationParty` row, `role = LABOUR_MINISTRY` | |
| `fecha_ultima_reunion`, `fecha_proxima_reunion` (zero-date defaults) | *derived* from `NegotiationMeeting` | Storing these duplicates meeting data and rots |
| `pdf_peticiones_sindicato`, `pdf_ofertas_patrono` | document references | Storage |
| `estatus_bitacora char(3)` (`'1'`/`'10'`/`''`) | `state` enum | ⚠Q-4 — the three states have ambiguous meaning |
| `estatu_pase_contratos varchar(50)` | — | **Dropped.** Read by dead JS, written by nothing, empty in all rows (DS-18) |

`NegotiationParty` carries `(case_id, role, full_name, phone, email, position)`. Rationale: the
domain has three parties *today* and real negotiations have more than one representative per party;
12 fixed columns cannot express either (DS-27).

### 4.2 Petitions, offers, meetings and agreements

| Legacy | Target | Transformation |
|---|---|---|
| `peticiones_sindicato` | `UnionPetition` | `nro_peticion` → `number`; `estatus_peticion char(1)` → enum ⚠Q-5; `codigo_titulo_comparativo` → `heading_id` (nullable) |
| `ofertas_patrono` | `EmployerOffer` | `codigo_peticion` → `petition_id` FK `NOT NULL` (3 of 4 legacy offers are orphans) |
| `reuniones` | `NegotiationMeeting` | `fecha_reunion text` (`yyyy-mm-dd`) → `held_on date`; `hora_reunion varchar(8)` → `time`; `asistentes_reunion text` → attendees are text for now, parties are the structured model |
| `acuerdos` | `MeetingAgreement` | `fecha_acuerdo text` (`dd-mm-yyyy`) → `agreed_on date`; four sibling pointers → explicit FKs; `estatus_acuerdo char(1)` → enum or dropped (always empty) |

**Reasoning for `acuerdos` (⚠Q-17).** The legacy row carries `codigo_bitacora`, `codigo_peticion`,
`codigo_oferta` **and** `codigo_reunion` with nothing ensuring they agree. In the target, an
agreement should belong to exactly one petition (the chain petition → offer → agreement is inherent),
with the meeting recorded once. That resolves the redundancy rather than reproducing it.

### 4.3 The three junction tables → one link (MERGE) ⚠Q-17

Legacy has `reuniones_peticiones`, `reuniones_ofertas`, `reuniones_acuerdos` — no PK, no indexes,
each carrying a redundant `codigo_bitacora` (DS-28).

Proposed: **`MeetingPetition(meeting_id, petition_id)`** as the only junction, because a meeting
discusses petitions; offers and agreements derive from the petition and carry their own
`meeting_id`. This replaces three tables with one plus two columns.

⚠Q-17 must confirm that a negotiation cannot legitimately produce a second agreement cycle for the
same petition, because that changes whether the junction is stable or versioned.

---

## 5. Legal library (⚠Q-6 shapes all of this)

The Product Owner decided the library **ships empty**. This mapping is therefore recorded for shape
only — it tells us what the entities must be able to express, not what to load.

### 5.1 `ley_trabajo` + `otras_leyes` → one `Statute` (MERGE)

| Legacy | Target | Transformation |
|---|---|---|
| `ley_trabajo` (1 row, **no PK**) | `Statute` with `kind = LABOUR_LAW` | Folding removes the "table with no identity" problem and makes articles referenceable |
| `otras_leyes.codigo_otra_ley` | `Statute.id` | |
| `nombre_ley text` | `name varchar(300)` | |
| `fecha_publicacion_ley text` (mixed `dd-mm-yyyy` / `dd/mm/yyyy`) | `published_on date` | Parse |
| `ley_trabajo.fecha_publicacion varchar(11)` (`'9 de mayo d'` — truncated) | `published_on date` | The legacy value is unrecoverable; it is not migrated |
| `pdf_ley` / `pdf_asociado` | document reference | Storage |
| `total_articulos` / `total_articulos_ley` | *derived* | Count query, not a column (DS-29) |
| `origen text` | `source text NULL` | Provenance of the statute text |

### 5.2 `articulos_ley_trabajo` + `articulos_otras_leyes` → `StatuteArticle` (MERGE)

The two tables are the same shape with the number typed differently (`int` vs `varchar`) — DS-23.
Merging also gives the labour-law articles the FK they never had.

| Legacy | Target | Transformation |
|---|---|---|
| `nro_articulo int` / `varchar(11)` | `number` + `sort_key` | **One** representation, shared with `AgreementClause` in form |
| `texto_completo_articulos` (plural) / `texto_completo_articulo` | `body text` | Same concept, one name |
| `codigo_titulo_comparativo` | `heading_id` nullable | 89.6% of other-statute articles have none |

**Why not one shared `Clause` table for agreements *and* statutes?** DS-26 argues the shapes are
identical, and they are. But a single polymorphic table needs a nullable `agreement_id` **and** a
nullable `statute_id`, which reintroduces exactly the "absence as a value" defect this whole design
exists to remove (DS-13). Recommendation: **two entities, one shared shape**, enforced by
convention and verified by tests — not by a nullable-parent supertype. This is a deliberate
architectural choice, open to challenge.

### 5.3 `categorias_titulos` + `titulos` → `ComparisonHeadingGroup` + `ComparisonHeading`

| Legacy | Target | Transformation |
|---|---|---|
| `categorias_titulos.requiere_campo_comparacion_economica char(2)` (`'si'`/`'no'`) | `requires_economic_comparison boolean` | `'si'` → `true`, `'no'` → `false`; a real boolean (DS-09) |
| `titulos.codigo_categoria_titulo` | `group_id` FK `NOT NULL` | |
| heading named **"No Compara"** (code 62, 853 clauses) | ⚠Q-15 | Either an explicit `excluded_from_comparison` flag on the heading, or a reserved heading code. **The data proves the concept is real and heavily used** — 15.8% of all clauses |

---

## 6. Documents and files

Legacy stores filenames in `text` columns across six tables (`pdf_auto_acta`, `pdf_anexo`,
`pdf_asociado`, `pdf_ley`, `imagen_1`, `imagen_2`, `logo`) — DS-11.

Target: a single `Document` entity, or direct Storage references, depending on whether documents need
metadata (title, size, content type, uploaded_by, uploaded_at). Recommendation: a `Document` entity,
because agreements, annexes, cases, statutes and news all attach files and each needs an audit trail
of who attached what.

| Legacy | Target |
|---|---|
| `contratos.pdf_auto_acta`, `anexos_contratos.pdf_anexo` | `Document` linked to agreement / annex |
| `bitacoras.pdf_peticiones_sindicato`, `pdf_ofertas_patrono` | `Document` linked to case |
| `otras_leyes.pdf_ley`, `ley_trabajo.pdf_asociado` | `Document` linked to statute |
| `noticias.imagen_1/2`, `empresas.logo`, `categorias.Imagen` | `Document` (image) |

⚠ Depends on the Supabase Storage decision in the architecture task (not this one).

---

## 7. Identity, roles and permissions

| Legacy | Target | Transformation |
|---|---|---|
| `usuarios` | `UserAccount` (Supabase Auth) + `UserProfile` | Credentials leave the schema entirely (DS-31); profile keeps name, phone, address, company |
| `usuarios.Clave_usuario` | — | **Not migrated.** Plaintext; new accounts are provisioned through the app (constitution Art. VI.6) |
| `usuarios.estatus` (`'BLO'`) | `is_blocked boolean` | Enforced on every request, not only at login |
| `usuarios.fech_venc` (zero-date default) | `expires_on date NULL` | ⚠Q-10 — semantics unresolved |
| `tipos_de_usuarios` | `Role` | ⚠Q-9 — the six roles must be redefined, not renamed |
| `pantallas` | `Capability` | REPLACE: filename-keyed grants cannot survive a SPA (DS-20) ⚠Q-8 |
| `seguridades` | `RoleCapability(role_id, capability_id)` | With `UNIQUE(role_id, capability_id)` — legacy has none, and 11 grants point at a nonexistent role |
| `modulos`, `categorias` | — | DROP: these describe the legacy menu, replaced by React routing |
| `usuarios_login` (absent) | `AuditLog` | The login audit that never worked becomes real |

---

## 8. Content and reference data

| Legacy | Target | Transformation |
|---|---|---|
| `noticias` | `NewsArticle` | `estatus_noticia varchar(11)` (`'Pendiente'`/`'Publicar'`) → enum `DRAFT`/`PUBLISHED`; `fecha_publicacion date NULL` → `published_on timestamptz NULL`; images → `Document` ⚠Q-14 |
| `categoria_noticias` | `NewsCategory` | |
| `paises` / `estados` / `localidades` | `Country` / `State` / `Municipality` | Composite legacy keys normalised to surrogate keys with natural-key uniqueness; audit columns dropped and replaced uniformly (DS-30) |
| `sectores_empresas` | `CompanySector` | Rename: the name suggests a junction, the content is a catalogue |
| `tipo_empresa` / `categoria_sector` / `actividad_empresa` | `CompanyType` / `CompanyCategory` / `EconomicActivity` | Direct maps |
| `links_interes` | `LinkOfInterest` | ⚠Q-16 — no stated requirement exists for this feature |

**Reference data seed.** Per constitution Art. V.3 and Product Owner decision D-1, the target may
ship a curated seed of `Country`, `State`, `Municipality`, `CompanySector`, `CompanyType`,
`CompanyCategory`, `EconomicActivity`, and `ComparisonHeading`/`Group`. **The legacy rows are not
that seed** — they are the reference for what values the business expects. Notably the 64 comparison
headings and 5 blocks are business taxonomy worth reproducing by hand, and the five geography levels
contain 670 legacy rows that would need review before any of them is trusted.

---

## 9. Field-level transformations (apply to every mapped table)

| Legacy pattern | Transformation | Scope |
|---|---|---|
| `DEFAULT ''` on `NOT NULL` text | `NULL` + nullable column | 64 columns |
| `DEFAULT '0'` on `int` reference | `NULL` + nullable FK | 53 columns |
| `DEFAULT '0'` on `int` quantity/count | Keep `0` only where zero is meaningful | `cantidad_*` |
| `date DEFAULT '0000-00-00'` | `NULL` | 3 columns |
| `datetime DEFAULT '0000-00-00 00:00:00'` | `NULL` | 4 columns |
| Date stored as `text`/`varchar` | Parse to `date`; reject unparseable | 5 columns, 3 formats |
| State in `char(N)`/`varchar(N)` | Enum + `CHECK` | 6 state columns |
| `'si'`/`'no'` in `char(2)` | `boolean` | 1 column |
| Filename in `text` | Storage reference | 7 columns |
| Derived counter | Compute | 2 columns |
| Mixed-case identifiers (`Id_pais`) | `snake_case` | 44 columns |
| Duplicate/competing identities | Single key + optional business code | `empresas` |
| Manual audit columns (4 tables) | Uniform audit on all tables | 4 tables |

---

## 10. Mappings blocked by open questions

| Question | Blocks | Why it cannot be decided here |
|---|---|---|
| **Q-1** Agreement identity | `CollectiveAgreement` | No legacy column; requires a product decision |
| **Q-2** Clause numbering | `AgreementClause.number`, `sort_key` | `NNN-N` semantics unknown |
| **Q-3** Economic comparison | `Clause.comparison_value` | Type, unit and purpose unknown; may be the product's core value |
| **Q-4** Negotiation states | `NegotiationCase.state` | `''`/`1`/`10` meaning ambiguous; `1` is written only by reopen |
| **Q-5** Petition outcomes | `UnionPetition.state` | Only `''` and `9` exist; rejected/withdrawn unrepresentable |
| **Q-6** Comparison semantics | Scope of `ComparisonHeading` usage | Adjacency vs alignment changes what the schema must store |
| **Q-7** Publication governance | `published_at`/`published_by` | Unknown whether approval is required |
| **Q-8** Permission model | `Capability` | Capability granularity undefined |
| **Q-9** Real roles | `Role` | The six legacy roles mix two axes and contradict their names |
| **Q-10** Account expiry | `UserProfile.expires_on` | Semantics unproven; implementation never worked |
| **Q-14** Portal placement | `NewsArticle` | Same app or separate surface affects the model boundary |
| **Q-15** "No Compara" | `ComparisonHeading` | 853 clauses suggest real meaning; no code explains it |
| **Q-16** Links of interest | `LinkOfInterest` | No requirement exists |
| **Q-17** Promotion semantics | `MeetingAgreement`, `MeetingPetition` | Copy vs move, once vs repeatable |
| **Q-18** Legacy company code | `Company.legacy_code` | External business meaning unknowable from the repo |
| **Q-19** Clause exclusion | `excluded_from_compilation` | Reason for exclusion unknown |

**Settled and safe to design against:** `Company` (except Q-18 attribute), `CompanySector`,
`CompanyType`, `CompanyCategory`, `EconomicActivity`, `Country`, `State`, `Municipality`,
`NewsCategory`, `ComparisonHeadingGroup`, all audit/identity/storage mechanics, and every
field-level transformation in §9.

---

## 11. What this document deliberately does not do

- **No DDL, no column types, no indexes, no migration scripts.** Those belong to the schema design
  task, after approval.
- **No data migration.** Per constitution Art. V.2 no legacy row is imported; the mapping describes
  shape only.
- **No decision on the questions above.** They are listed, not guessed.
- **No claim that the target is complete.** Entities may still be added when the remaining questions
  are answered — in particular around comparison alignment (Q-6), which could require structure that
  nothing in the legacy schema anticipates.
