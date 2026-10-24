# Legacy schema issues

Defects in the legacy MySQL schema, each with the evidence that establishes it and the consequence
for the PostgreSQL design. Severity reflects **impact on the replacement**, not on the legacy
system.

Severity scale: **Critical** — would corrupt or block the new system if carried over.
**High** — forces a redesign of the affected area. **Medium** — data quality or maintainability.
**Low** — cosmetic or historical.

Cross-references: [legacy-schema-inventory.md](legacy-schema-inventory.md) ·
[legacy-relationships.md](legacy-relationships.md) · [schema-mapping.md](schema-mapping.md) ·
Phase 1 [../legacy/](../legacy/).

---

## A. Referential integrity

### DS-01 — Zero foreign keys, zero enforcement — **Critical**

No `FOREIGN KEY` anywhere; MyISAM would ignore them regardless. **87 orphan rows across 10
relationships** were measured (see `legacy-relationships.md` §5), including 57 clauses pointing at a
deleted agreement and 11 grants pointing at a nonexistent role.

*Consequence:* every relationship must be declared as a real FK in PostgreSQL. This is not a
migration detail — it is the main reason the old schema cannot be translated mechanically.

### DS-02 — Cascades implemented as untransacted multi-statement code — **Critical**

Deleting a company/agreement/country issues several `DELETE` statements in sequence from PHP with
no transaction (`eliminarpais.php:10-12`, `borrarContrato*`, `Discusion::delete*`). MyISAM cannot
roll back, so any interruption strands children — the mechanism that produced the 57 orphan clauses.

*Consequence:* deletion semantics must be decided explicitly per relationship
(`RESTRICT` vs `CASCADE`) and enforced by the database. See Phase 1 Q-10.

### DS-03 — Four tables have no primary key — **High**

`ley_trabajo`, `reuniones_peticiones`, `reuniones_ofertas`, `reuniones_acuerdos`.

Two distinct problems: `ley_trabajo` cannot be referenced at all (which is why its 556 articles have
no link to their statute), and the three junction tables can accumulate duplicate rows with nothing
to prevent it.

### DS-04 — Only one unique constraint in the entire schema, and it is redundant — **High**

`UNIQUE KEY nro_articulo (nro_articulo)` on `articulos_ley_trabajo`, where `nro_articulo` is
already the primary key.

Nothing enforces uniqueness where it matters:

| Should be unique | Actual data |
|---|---|
| `usuarios.Login_usuario` | 4 distinct — currently clean, nothing prevents duplicates |
| `pantallas.url_pantalla` | **duplicated**: `tbl_pantalla_edit.php` on ids 1 and 65 |
| `titulos.nombre_titulo` | 64 distinct — clean by luck |
| `paises.Nombre_pais` | 237 distinct — clean by luck |
| `seguridades(Id_tipo_Usuario, id_pantalla)` | no constraint; duplicate grants possible |
| `reuniones_*` junction pairs | no constraint; duplicate links possible |

The `pantallas` collision is not cosmetic: that column is the permission lookup key
(`pantalla.php:54`), so a duplicate makes authorisation ambiguous.

### DS-05 — No index on any column the application joins or filters on — **High**

The four secondary indexes cover geography and two lookup tables. Missing: `contratos.codigo_empresa`,
`articulos_contratos.codigo_contratos`, `articulos_contratos.codigo_titulo_comparativo`,
`bitacoras.codigo_empresa`, and every `LIKE '%term%'` search column.

*Consequence:* every list screen is a full scan, and the comparison feature — which joins 5,402
clauses to 64 headings per request — has no supporting index.

---

## B. Types

### DS-06 — Dates stored as `text`/`varchar` — **High**

| Column | Declared | Format found in data |
|---|---|---|
| `bitacoras.fecha_inicio_disc` | `text` | `dd/mm/yyyy` |
| `reuniones.fecha_reunion` | `text` | `yyyy-mm-dd` (a *different* format from the above) |
| `acuerdos.fecha_acuerdo` | `text` | `dd-mm-yyyy` |
| `otras_leyes.fecha_publicacion_ley` | `text` | `dd-mm-yyyy` **and** `dd/mm/yyyy` mixed in the same column |
| `ley_trabajo.fecha_publicacion` | `varchar(11)` | truncated free text — one row reads `'9 de mayo d'` |

**Three different date formats coexist**, two of them in the same column, plus one value truncated
mid-word by the `varchar(11)` width.

*Consequence:* `date` columns in PostgreSQL, parsed at the boundary. The application already has
conversion helpers (`MenejoFechas.php`) whose existence is evidence that the format mismatch was a
constant source of work.

### DS-07 — Zero-date sentinels — **High**

Seven columns default to a zero date: `bitacoras.fecha_ultima_reunion`,
`bitacoras.fecha_proxima_reunion`, `usuarios.fech_venc` (`'0000-00-00'`); `estados.Fecha_cre`,
`estados.Fecha_mod`, `links_interes.lit_FechaCrea`, `links_interes.lit_FechaMod`
(`'0000-00-00 00:00:00'`).

Present in the data at scale: **8 zero `fecha_de_inicio` + 10 zero `fecha_de_termino`** of 73
agreements; 2 of 2 negotiation dates; 1 of 4 user expiry dates.

`'0000-00-00'` is not a valid PostgreSQL date and cannot be stored in a `date` column at all.
*Consequence:* nullable `date` columns with a real `NULL`.

### DS-08 — No numeric type capable of money or percentage — **High**

The schema contains zero `decimal`, `numeric`, `float`, or `double` columns. The economic comparison
value (`campo_comparativo`) is `varchar(250)`/`text`; the "requires economic comparison" flag is
`char(2)` holding `'si'`/`'no'`.

*Consequence:* if economic comparison is a real feature (Phase 1 Q-3), it needs a numeric type plus a
unit, which is a **new** design decision with no legacy precedent to copy.

### DS-09 — No boolean type; booleans encoded six different ways — **Medium**

`''`/`'0'`/`'1'` (`articulo_cerrado`), `'si'`/`'no'` (`requiere_campo_comparacion_economica`),
`'PU'`/`'PE'`/`'Pe'` (`status_publicacion`), `'Pendiente'`/`'Publicar'` (`estatus_noticia`),
`'BLO'`/`''` (`usuarios.estatus`), `'1'`/`'10'`/`''` (`estatus_bitacora`).

*Consequence:* real `boolean` where the domain is boolean; enums where it is a state; `CHECK`
constraints so the database itself rejects unknown values.

### DS-10 — `int(N)` display widths with no meaning — **Low**

`int(11)` ×83, plus `int(5)`, `int(9)`, `int(20)`. In MySQL the width is cosmetic, so
`usuarios.Codigo_tipo_usuario int(20)` carries no more range than its target
`tipos_de_usuarios.Id_tipo_Usuario int(11)`. PostgreSQL has no equivalent and needs none.

### DS-11 — Files stored as `text` — **Medium**

`pdf_auto_acta`, `pdf_anexo`, `pdf_asociado`, `pdf_ley`, `imagen_1`, `imagen_2`, `logo` are all
`text`, holding filenames. `categorias.Imagen` is `varchar(50)` — a *different* width for the same
concept.

`text` permits unbounded values for what should be a constrained identifier. *Consequence:* a
`varchar` with a length limit if filenames stay in the database at all — or, better, a storage
reference (Supabase Storage) since the replacement mandates it.

### DS-12 — `Noticias` and other `text` columns used for short scalars — **Low**

`otras_leyes.nombre_ley text` and `sectores_empresas.nombre_sector text` are entity *names* stored
in a type with no length bound and no indexability. Sibling tables use `varchar(50)`–`varchar(250)`
for the same concept.

---

## C. Absence encoding

### DS-13 — Half the columns default to `''` or `'0'` — **Critical**

**117 of 238 columns (49%)** default to an empty string (64) or a literal zero (53). The schema
therefore has no way to express "absent": a column is either populated or holds a value that looks
like data.

Concrete consequences visible in the dump:

| Column | Rows at default | Meaning collision |
|---|---|---|
| `empresas.codigo_actividad` | 17 of 80 | "no activity" vs "activity 0" |
| `empresas.codigo_localidad` | 15 of 80 | "unknown location" vs "locality 0" |
| `empresas.codigo_estado` | 13 of 80 | as above |
| `empresas.codigo_tipo` | 12 of 80 | as above |
| `empresas.codigo_categoria` | 10 of 80 | as above |
| `contratos.codigo_empresa` | 2 of 73 | "no company" vs "company 0" |
| `articulos_contratos.codigo_titulo_comparativo` | **899 of 5,402** | "unclassified" vs "heading 0" |

*Consequence:* `NULL` must mean absent, and `NOT NULL` must be used for genuine business
mandatories. This single decision removes most of the ambiguity in the legacy model.

### DS-14 — Nullability is inverted relative to the domain — **High**

Mandatory business facts are nullable (`contratos.fecha_de_inicio`, `fecha_de_termino`,
`usuarios.Nombre_usuario`, `usuarios.Codigo_tipo_usuario`, `paises.Nombre_pais`,
`estados.Nombre_estado`, `localidades.Nombre_localidad`, `modulos.Nombre_modulo`,
`seguridades.id_pantalla`) while optional facts are `NOT NULL` (`empresas.fax_empresa`,
`empresas.nit_empresa`, every description column).

A country with no name, a screen grant with no screen, and a user with no type are all representable.

---

## D. States, roles and types

### DS-15 — A two-state flag holds three values — **High**

`contratos.status_publicacion char(2) DEFAULT 'PE'` contains, in the data:

| Value | Rows | What the code does with it |
|---|---|---|
| `'PU'` | 70 | matches every `='PU'` predicate → published |
| `'Pe'` | **2** | matches **nothing** — matches neither `='PU'` nor `='PE'` |
| `'PE'` | 1 | the schema default |

The code compares `=="Pe"` (`publicar.php:12`) while the default is `'PE'`, producing the two-click
publish defect; the two `'Pe'` rows are in a state the application cannot reason about — invisible to
the comparison query, which filters `='PU'`.

*Consequence:* an enum with a `CHECK`, and the database rejects any value outside it.

### DS-16 — Two parallel vocabularies for one concept — **Medium**

An agreement's publication state is `char(2)` `'PE'`/`'PU'`; an annex's is `varchar(15)` holding
`'Pendiente'`. Both mean "pending vs published". All 29 annexes are `'Pendiente'` — the annex state
was never advanced, and nothing reads it.

### DS-17 — State codes stored as numbers in character columns — **Medium**

`estatus_bitacora char(3)` holds `'1'` and `'10'`; `estatus_peticion char(1)` is written as `9`.
Numeric ordering of `'1'` vs `'10'` is lexicographic, and `char(3)` can hold `'abc'` — nothing
prevents it.

*Consequence:* a PostgreSQL enum (or a lookup table) with an explicit state machine. Phase 1 Q-4
must define the states first.

### DS-18 — Three state columns are never advanced — **Medium**

Measured distributions:

| Column | Values in data |
|---|---|
| `status_articulo` | **5,402 of 5,402 = `'Pendiente'`** |
| `acuerdos.estatus_acuerdo` | 1 of 1 empty |
| `ofertas_patrono.estatus_oferta` | 4 of 4 empty |
| `peticiones_sindicato.estatus_peticion` | 9 of 9 empty (the `9` that code writes never occurs) |
| `bitacoras.estatu_pase_contratos` | 2 of 2 empty (never written by any statement) |

Four state columns are **dead weight in the model** — and `status_articulo` in particular suggests an
editorial workflow that was never built.

### DS-19 — Role identifiers include an orphan type — **High**

`seguridades.Id_tipo_Usuario` references type `5` in **11 of 146** grant rows, but
`tipos_de_usuarios` has no row `5` (its rows are 1, 2, 3, 25, 27, 28). No FK, so nothing objected.

The role *names* also mix two axes — work functions (`Master`, `Operador`, `Transcriptor`) and
feature bundles (`Perfil 2`, `Perfil 3`, `DEMO`) — which is a modelling question, not a schema
defect, but the schema cannot distinguish the two. See Phase 1 Q-9.

### DS-20 — Permission model keys on a UI filename — **High**

`pantallas.url_pantalla varchar(150)` holds bare PHP filenames
(`tbl_paises_list.php`), and the permission check compares the currently-requested filename
(`pantalla.php:54`). The permission model is therefore coupled to the **file layout of a PHP
application**, which has no analogue in a React SPA.

*Consequence:* a capability-based permission model is a new design. This is the strongest argument
that the security schema must not be translated.

---

## E. Naming and duplication

### DS-21 — Two casing conventions in one schema — **Medium**

186 columns `snake_case` (`codigo_empresa`, `id_pantalla`), **44 columns `Pascal_Case`**
(`Id_pais`, `Nombre_pais`, `Usuario_cre`, `Fecha_cre`, `Id_localidad`, `Clave_usuario`,
`Login_usuario`, `Nombre_tipo`, `lit_IdLink`). Consistent *within* a table is not guaranteed either:
`usuarios` mixes `Login_usuario`/`Nombre_usuario` (Pascal-ish) with `id`/`estatus`/`fech_venc`.

PostgreSQL folds unquoted identifiers to lower case, so `Id_pais` and `id_pais` become the same
column — a powerful reason to standardise rather than translate.

### DS-22 — Spanish and English mix for the same concept — **Low**

`status_publicacion` / `status_articulo` (English) beside `estatus_bitacora` /
`estatus_peticion` / `estatus_acuerdo` / `estatus_noticia` / `estatus` (Spanish). Also
`fech_venc` (abbreviated Spanish) beside `fecha_de_inicio` (full Spanish).

### DS-23 — The same concept has three names and two types — **High**

Article number:

| Table | Column | Type |
|---|---|---|
| `articulos_contratos` | `nro_articulos` (plural) | `varchar(11)` |
| `articulos_otras_leyes` | `nro_articulo` | `varchar(11)` |
| `articulos_ley_trabajo` | `nro_articulo` | **`int(11)`** |

The same happens with the body column: `texto_completo_articulo` (singular) in three tables versus
`texto_completo_articulos` (plural) in `articulos_ley_trabajo`.

*Consequence:* one shared representation for an article number across agreements and statutes —
which is also what makes cross-document comparison possible at all.

### DS-24 — Join columns whose names do not match their parent keys — **Medium**

| Junction column | Parent PK | Match |
|---|---|---|
| `codigo_reuniones` | `reuniones.codigo_reunion` | ✗ |
| `codigo_peticiones` | `peticiones_sindicato.codigo_peticion` | ✗ |
| `codigo_ofertas` (in `reuniones_ofertas`) | `ofertas_patrono.codigo_ofertas` | ✓ |
| `codigo_oferta` (in `acuerdos`) | `ofertas_patrono.codigo_ofertas` | ✗ |

Even with a database that enforced FKs, these names would have to be corrected before an FK could be
declared.

### DS-25 — Three unrelated tables called "category" — **Medium**

| Table | Key | Purpose |
|---|---|---|
| `categorias` | `Id_categoria` | UI module categories (menu grouping) |
| `categorias_titulos` | `codigo_categoria_titulos` | Comparison heading **blocks** |
| `categoria_sector` | `codigo_categoria` | Company size/segment classification |

Three different key names, three different casings, three different concepts — all "category".

### DS-26 — Four near-identical article tables — **Medium**

`articulos_contratos`, `articulos_otras_leyes`, `articulos_ley_trabajo`, and `acuerdos` all carry
`texto_completo_articulo` + `resumen_articulo` + `titulo_articulo` + `codigo_titulo_comparativo` +
`campo_comparativo`. The first three differ only in which parent they point at; `acuerdos` is the
same shape plus four sibling pointers.

*Consequence:* this is the strongest case in the schema for a shared `clause` abstraction
(`schema-mapping.md` §Clause), which is a **design decision**, not a translation.

### DS-27 — A triplicated contact block — **High**

`bitacoras` holds three copies of the same four columns (representative name, phone, email, position)
for three parties: company, union, Labour Ministry. 12 columns of 21.

Adding a fourth party, or a second representative per party (which real negotiations have), requires
a schema change.

### DS-28 — Denormalised case id in the junction tables — **Medium**

Each `reuniones_*` table carries `codigo_bitacora` in addition to the meeting id — data already
implied by the meeting. Nothing prevents a row from linking a meeting in case A to an item in case B.

### DS-29 — `total_articulos` counters — **Medium**

`ley_trabajo.total_articulos` and `otras_leyes.total_articulos_ley` store a derived count,
maintained by application code on add/delete only, with one implementation
(`ley_trabajo.php:148`) matching on the *old* value so it succeeds only by accident of read order.

Derived data stored as state is a correctness liability; it should be computed.

---

## F. Audit and security

### DS-30 — No audit trail and almost no audit columns — **High**

No audit table exists. Only **4 of 35 tables** carry creation/modification metadata
(`paises`, `estados`, `localidades`, `links_interes`) — the geography and links tables, i.e. the
least business-critical ones. Every table holding the domain (`contratos`, `articulos_contratos`,
`bitacoras`, `acuerdos`, `titulos`, `usuarios`) has **none**.

The four that do are inconsistent: `paises`/`localidades` default to `NULL`, `estados`/`links_interes`
default to zero sentinels.

### DS-31 — Plaintext password storage — **Critical** (security)

`usuarios.Clave_usuario varchar(20)` holds plaintext. No salt, no hash, no algorithm anywhere in the
schema or the code (Phase 1 §S3). `varchar(20)` is also too short for any modern hash, so the column
must be replaced — not widened — in any design.

### DS-32 — A login-audit table that does not exist — **Medium**

Both audit writers (`lib/objetos/user.php:177`, `admin/modulos/settings.php:46`) insert into
`usuarios_login`, which **is not in the schema** (35 tables, verified). Every login-audit write has
always failed silently.

### DS-33 — `RIF` / `NIT` are unpopulated placeholders — **Medium**

`empresas.rif_empresa`: 80 rows, **3 distinct values, one of them shared by 78 rows** (an empty or
dash-like placeholder). The Venezuelan corporate tax identifier — the one field that would uniquely
identify a company — is effectively unused, which explains why the schema's own uniqueness story is
so weak.

### DS-34 — No row-level security concept — **High** (design)

There is no tenancy, no ownership, and no row-level rule in the schema. The single company-scoped
filter in the application (`discusion_contratos_listar.php:8`) is applied in PHP, not enforced.
The replacement mandates Supabase RLS, so row-level policy is a **new** design surface.

---

## G. Data-profile findings that inform the design

These are not schema defects but facts about the data that change what the new schema must support.

| Finding | Value | Implication |
|---|---|---|
| Clauses per agreement | 5,402 clauses over 73 agreements (~74 each) | Clause table is the volume driver |
| Clauses with no comparison heading | **899 / 5,402 = 16.6%** | A heading is *optional* in practice |
| Clauses under heading 62 "No Compara" | **853** | The opt-out is real and heavily used (see Q-15) |
| Headings defined vs used | 64 defined, 63 used, 1 never used | Taxonomy is stable |
| Statute articles with no heading | 2,672 / 2,982 = **89.6%** (other statutes); 199 / 556 = 35.8% (labour law) | Statute-to-clause comparison covers a tenth of the corpus |
| Clauses flagged `articulo_cerrado = 1` | **158** | The only per-clause behaviour is used |
| Clauses `articulo_cerrado = NULL` | 1,192 | Three-valued flag: `'0'`, `'1'`, `NULL` |
| Agreements published | 70 `'PU'` + 2 `'Pe'` + 1 `'PE'` | The `'Pe'` rows are unreachable by any filter |
| Users blocked | **3 of 4 `'BLO'`** | Account administration state |
| Economic-comparison categories | 4 of 5 blocks flagged `'si'` | The concept was configured widely but never enforced |
| Companies with an agreement | 72 of 80 | 8 companies have none |

---

## H. Summary — which defect drives which design decision

| Design decision | Driven by |
|---|---|
| Real FKs with explicit delete policy | DS-01, DS-02, DS-03, DS-04 |
| `NULL` means absent; `NOT NULL` for business mandatories | DS-13, DS-14 |
| `date`/`timestamptz` everywhere | DS-06, DS-07 |
| Enums + `CHECK` for every state | DS-15, DS-16, DS-17, DS-19 |
| One article-number representation | DS-23 |
| Shared clause abstraction across agreements and statutes | DS-26 |
| Parties normalised out of `bitacoras` | DS-27 |
| Capability-based permissions, not filename grants | DS-20, DS-34 |
| Audit columns on every business table | DS-30, DS-32 |
| Hashed credentials, new identity store | DS-31 |
| Derived values computed, not stored | DS-29 |
| Unique constraints on natural keys | DS-04, DS-33 |
