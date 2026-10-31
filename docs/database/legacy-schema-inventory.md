# Legacy schema inventory — MySQL dump `presenci_cccol`

Static analysis of the legacy MySQL dump (removed from the repository in T16; see
[legacy-removal.md](../operations/legacy-removal.md)). The dump was **not restored**,
**not imported**, and **not modified**. Nothing was executed against any database.

Companion documents: [legacy-relationships.md](legacy-relationships.md) ·
[legacy-schema-issues.md](legacy-schema-issues.md) · [schema-mapping.md](schema-mapping.md).
Cross-references into the application refer to the Phase 1 audit in
[../legacy/](../legacy/).

## Provenance of the dump

| Property | Value |
|---|---|
| Server | MySQL **5.6.30** (`5.6.30-cll-lve`, CloudLinux variant) |
| Database | `presenci_cccol` |
| Dumped | 2016-09-22 21:18:57 |
| Size | 48.87 MB (49,565 KB of it in `articulos_contratos`, `empresas`, and the statute article tables) |
| Encoding | `latin1` throughout; one column overrides to `latin1_bin` |
| Engine | **MyISAM on all 35 tables** — no transactions, no foreign keys, no crash safety |
| Statement terminator | `NO_AUTO_VALUE_ON_ZERO` set at dump time |

**Note on the database name.** The application's own DSN
(`local/configuracion.php`, index 0) targets schema **`cccol`**, and one hardcoded credential set
targets **`CCCOL`**. This dump declares **`presenci_cccol`**. Three different names for what is
presumably one database across environments — or the dump is from a differently-named copy. This
cannot be resolved from the repository; it is recorded in
[../legacy/open-questions.md](../legacy/open-questions.md) §"Confirmed unresolvable".

---

## 1. Aggregate structure

| Metric | Count |
|---|---|
| Tables | **35** |
| Columns | **238** |
| Rows (total, all tables) | **10,634** |
| Primary keys | **31** (4 tables have none) |
| Foreign keys | **0** |
| Unique constraints | **1** — and it is redundant (a `UNIQUE` on the column that is also the PK) |
| Secondary indexes | **4** |
| Triggers / procedures / functions / views | **0 / 0 / 0 / 0** |
| `AUTO_INCREMENT` columns | 30 |
| Tables without a primary key | `ley_trabajo`, `reuniones_acuerdos`, `reuniones_ofertas`, `reuniones_peticiones` |

That last row is the headline: **the schema contains one unique constraint in total, and it is a
duplicate of a primary key.** There is no other uniqueness enforcement anywhere — not on usernames,
not on country names, not on screen URLs (which collide, see §5.4).

### Column type distribution

| Type | Columns | Comment |
|---|---|---|
| `int(11)` / `int(9)` / `int(5)` / `int(20)` | 87 | Display widths vary with no semantic meaning |
| `varchar` | 86 | Lengths 8–250 chosen arbitrarily |
| `text` | 42 | Used for dates, filenames, and short single values |
| `datetime` | 8 | |
| `char` | 6 | All are state/enum columns |
| `date` | 6 | |
| `longtext` | 3 | Clause/annex/news bodies |

**Absent entirely: `decimal`, `numeric`, `float`, `double`, `boolean`, `tinyint`, `enum`, `set`,
`bigint`, `timestamp`.** Two consequences worth stating plainly:

1. **There is no numeric type that can represent money or a percentage.** The economic comparison
   field (`campo_comparativo`) is `varchar(250)`. Counts (`cantidad_obreros_empresa`) are integers.
2. **There is no boolean type.** Boolean-ish facts are `char(1)`/`char(2)` holding `''`, `'0'`,
   `'1'`, `'si'`, `'no'`, `'PU'`, `'PE'`.

### Nullability

200 columns are `NOT NULL`; 38 are nullable (33 declared `NULL`, 5 nullable by omission —
`otras_leyes.descripcion_ley`, `otras_leyes.pdf_ley`, `noticias.noticia_completa`,
`noticias.imagen_1`, `noticias.imagen_2`).

The distribution is inverted from what the domain needs: **mandatory business facts are nullable**
(`contratos.fecha_de_inicio`, `contratos.fecha_de_termino`, `articulos_contratos.articulo_cerrado`,
`usuarios.Nombre_usuario`, `usuarios.Codigo_tipo_usuario`, `paises.Nombre_pais`,
`estados.Nombre_estado`, `localidades.Nombre_localidad`, `tipos_de_usuarios.Nombre_tipo`,
`modulos.Nombre_modulo`, `seguridades.id_pantalla`) while **optional ones are `NOT NULL`**
(`empresas.fax_empresa`, `empresas.nit_empresa`, every `text` description).

### Default values

| Default | Columns | Interpretation |
|---|---|---|
| `''` (empty string) | **64** | "unknown" encoded as a value |
| `'0'` (literal zero, on `int` columns) | **53** | "no relation" encoded as a real id |
| `NULL` | 33 | |
| `'0000-00-00'` | 3 | zero date sentinel |
| `'0000-00-00 00:00:00'` | 4 | zero datetime sentinel |
| `'Pendiente'` | 3 | Spanish state literal as default |
| `'PE'` | 1 | uppercase state code that the code then compares case-sensitively |

**117 of 238 columns (49%) default to `''` or `'0'`.** This is the single most consequential
design fact in the schema: the model has no representation for "absent", so absence is stored as a
value that is indistinguishable from a legitimate one. `codigo_empresa = 0` means both "no company"
and "company 0", and 53 columns make that ambiguity structural.

---

## 2. Table catalogue

Row counts are exact (parsed from the `INSERT` statements). `AI` is the table's
`AUTO_INCREMENT` counter, which frequently exceeds the surviving row count because of deletions.

### 2.1 Agreements and clauses

**`contratos`** — 73 rows, 8 columns, PK `codigo_contrato`, AI 122.
`codigo_contrato int AI` · `pdf_auto_acta text` · `fecha_de_inicio date NULL` ·
`fecha_de_termino date NULL` · `duracion varchar(15)` · `ambito_aplicacion varchar(150)` ·
`codigo_empresa int` · `status_publicacion char(2) DEFAULT 'PE'`
*No name, number, or title column exists — an agreement is identified only by its company.*

**`articulos_contratos`** — 5,402 rows, 10 columns, PK `codigo_articulo`, AI 6426.
`codigo_articulo int AI` · `codigo_contratos int` · `nro_articulos varchar(11)` ·
`texto_completo_articulo longtext` · `resumen_articulo text` · `codigo_titulo_comparativo int` ·
`campo_comparativo varchar(250)` · `status_articulo varchar(12) DEFAULT 'Pendiente'` ·
`titulo_articulo text` · `articulo_cerrado char(1) NULL`
*The dominant table (5,402 of 10,634 rows = 51%). `status_articulo` is written by no code path.*

**`anexos_contratos`** — 29 rows, 7 columns, PK `codigo_anexo`, AI 50.
`codigo_anexo int AI` · `codigo_contrato int` · `nombre_anexo varchar(250)` ·
`descripcion_anexo text` · `texto_completo_anexo longtext` · `pdf_anexo text` ·
`status_publicacion varchar(15) DEFAULT 'Pendiente'`

### 2.2 Negotiation

**`bitacoras`** — 2 rows, 21 columns, PK `codigo_bitacora`, AI 24.
The widest operational table, and almost entirely full of a **triplicated contact block**:

`codigo_bitacora int AI` · `codigo_empresa int` · `fecha_inicio_disc text` ·
then four columns × three parties (company / union / Labour Ministry):
`representante_{empresa|sindicato|min_trab} varchar(100)` ·
`telefono_representante_{…} varchar(50)` · `email_representante_{…} varchar(100)` ·
`cargo_representante_{…} varchar(50)` ·
then `fecha_ultima_reunion date DEFAULT '0000-00-00'` ·
`fecha_proxima_reunion date DEFAULT '0000-00-00'` ·
`pdf_peticiones_sindicato varchar(150)` · `pdf_ofertas_patrono varchar(150)` ·
`estatus_bitacora char(3)` · `estatu_pase_contratos varchar(50)`

Twelve columns encode three occurrences of one concept. This is the clearest case for
normalisation in the whole schema (see `schema-mapping.md` §Party).

**`peticiones_sindicato`** — 9 rows, 7 columns, PK `codigo_peticion`, AI 52.
`codigo_peticion int AI` · `codigo_bitacora int` · `nro_peticion varchar(11)` ·
`texto_completo_peticion text` · `estatus_peticion char(1) CHARACTER SET latin1 COLLATE latin1_bin` ·
`codigo_titulo_comparativo int` · `titulo_peticion varchar(250)`

**`ofertas_patrono`** — 4 rows, 8 columns, PK `codigo_ofertas`, AI 25.
`codigo_ofertas int AI` · `codigo_bitacora int` · `nro_oferta varchar(11)` ·
`texto_completo_oferta text` · `estatus_oferta varchar(15)` · `codigo_titulo_comparativo int` ·
`codigo_peticion int` · `titulo_oferta varchar(250)`

**`reuniones`** — 2 rows, 8 columns, PK `codigo_reunion`, AI 24.
`codigo_reunion int AI` · `codigo_bitacora int` · `fecha_reunion text` · `hora_reunion varchar(8)` ·
`duracion_reunion varchar(15)` · `resumen_reunion text` · `detalles_reunion text` ·
`asistentes_reunion text`

**`acuerdos`** — 1 row, 13 columns, PK `codigo_acuerdos`, AI 14.
`codigo_acuerdos int AI` · `codigo_bitacora int` · `codigo_peticion int` · `codigo_oferta int` ·
`codigo_reunion int` · `fecha_acuerdo text` · `nro_articulo varchar(11)` ·
`texto_completo_articulo text` · `resumen_articulo text` · `codigo_titulo_comparativo int` ·
`campo_comparativo text` · `estatus_acuerdo char(1)` · `titulo_articulo text`
*Contains four sibling pointers (bitácora, petition, offer, meeting) with no constraint that they
agree with each other.*

**`reuniones_peticiones`** / **`reuniones_ofertas`** / **`reuniones_acuerdos`** — 4 / 4 / 1 rows,
3 columns each, **no primary key, no index**. Columns:
`codigo_reuniones int` · `codigo_bitacora int` · `codigo_{peticiones|ofertas|acuerdos} int`.
Each row carries **both** the meeting id and the case id, so the case id is denormalised into the
junction (and could disagree with the meeting's own case).

### 2.3 Legal library

**`ley_trabajo`** — 1 row, 4 columns, **no primary key**.
`fecha_publicacion varchar(11)` · `pdf_asociado text` · `descripcion_resumen text` ·
`total_articulos int`
*Designed to hold exactly one row; a second row makes the application's loader (`realcount()==1`)
fail, so the module silently returns false.*

**`articulos_ley_trabajo`** — 556 rows, 6 columns, PK **and** `UNIQUE` on `nro_articulo`, no AI.
`nro_articulo int` · `texto_completo_articulos text` · `resumen_articulo text` ·
`codigo_titulo_comparativo int` · `campo_comparativo varchar(200)` · `titulo_articulo varchar(200)`
*The only `UNIQUE` index in the schema, on the PK column — redundant. The article number is an
`int` here but a `varchar` in the sibling tables (§5.2).*

**`otras_leyes`** — 47 rows, 7 columns, PK `codigo_otra_ley`, AI 97.
`codigo_otra_ley int AI` · `nombre_ley text` · `fecha_publicacion_ley text` ·
`descripcion_ley text` · `pdf_ley text` · `total_articulos_ley int` · `origen text`

**`articulos_otras_leyes`** — 2,982 rows, 8 columns, PK `codigo_articulo`, AI 4071.
`codigo_articulo int AI` · `codigo_otra_ley int` · `nro_articulo varchar(11)` ·
`texto_completo_articulo text` · `resumen_articulo text` · `codigo_titulo_comparativo int` ·
`campo_comparativo varchar(200)` · `titulo_articulo varchar(200)`

### 2.4 Comparison taxonomy

**`categorias_titulos`** — 5 rows, 4 columns, PK `codigo_categoria_titulos`, AI 20.
`codigo_categoria_titulos int AI` · `nombre_categoria varchar(250)` ·
`descripcion_categoria text` · `requiere_campo_comparacion_economica char(2)`
*The five blocks are: GENERALES (`'no'`), ECONOMICO (`'si'`), SOCIOECONÓMICAS (`'si'`),
SINDICALES (`'si'`), SEGURIDAD OCUPACIONAL (`'si'`). The flag stores Spanish `'si'`/`'no'` — not a
boolean, and not the `'1'`/`'0'` used elsewhere.*

**`titulos`** — 64 rows, 4 columns, PK `codigo_titulo_comparativo`, AI 88.
`codigo_titulo_comparativo int AI` · `nombre_titulo varchar(250)` · `descripcion_titulo text` ·
`codigo_categoria_titulo int`
*The comparison headings. One of them, code 62, is literally named **"No Compara"** and holds 853
clauses — an explicit opt-out mechanism implemented as data (see `schema-mapping.md` §Q-15).*

### 2.5 Organisation and geography

**`empresas`** — 80 rows, **26 columns** (widest table), PK `id`, AI 196.
`id int AI` · `codigo_empresa int NOT NULL` · `nombre_empresa varchar(200)` ·
`direccion_empresa text` · `codigo_pais int` · `codigo_estado int` · `codigo_localidad int` ·
`telefonos_empresa varchar(20)` · `fax_empresa varchar(20)` · `email_empresa varchar(60)` ·
`url_empresa varchar(150)` · `rif_empresa varchar(50)` · `nit_empresa varchar(50)` ·
`contacto_empresa varchar(150)` · `cargo_contacto varchar(100)` · `telefonos_contacto varchar(200)` ·
`email_contacto varchar(60)` · `codigo_sector int` · `codigo_tipo int` · `codigo_categoria int` ·
`codigo_actividad int` · `cantidad_obreros_empresa int` · `cantidad_empleados_empresa int` ·
`tamano_empresa varchar(100)` · `origen text` · `logo text`
*Two full identities: `id` (PK) and `codigo_empresa` (`NOT NULL`, no default, not AI). References
resolve against `codigo_empresa` in 71 of 73 agreements and against `id` for one user row — proven
in [legacy-relationships.md](legacy-relationships.md) §3.*

**`paises`** — 237 rows, 6 columns, PK `Id_pais`, AI 259. `Id_pais int(5) AI` ·
`Nombre_pais varchar(50) NULL` · `Usuario_cre varchar(50) NULL` · `Fecha_cre datetime NULL` ·
`Usuario_mod varchar(50) NULL` · `Fecha_mod datetime NULL`

**`estados`** — 23 rows, 7 columns, PK (`Id_estado`, `Id_pais`), AI 36. `Id_estado int AI` ·
`Id_pais int` · `Nombre_estado varchar(50) NULL` · audit columns as above.
*Composite PK with an AI first column: the AI is global while the PK is per-country.*

**`localidades`** — 410 rows, 8 columns, PK (`Id_localidad`, `Id_estado`, `Id_pais`), AI 448.
`Id_localidad int AI` · `Id_estado int` · `Id_pais int` · `Nombre_localidad varchar(100) NULL` ·
audit columns (all nullable here, `NOT NULL` in `estados`).

**`ciudades`** — **0 rows**, 3 columns, PK (`Id_ciudad`, `Id_pais`).
`Id_ciudad int AI` · `Id_pais int` · `Nombre_ciudad varchar(20) NULL`.
*A third geography level that was never populated and is referenced by nothing.*

**`actividad_empresa`** — 21 rows, 3 cols, PK `codigo_actividad`.
`tipo_empresa` — 11 rows, PK `codigo_tipo`, AI 24.
`categoria_sector` — 18 rows, PK `codigo_categoria`, AI 31.
`sectores_empresas` — 3 rows, PK `codigo_sector`, AI 14.
*Four small catalogues. Despite the plural-contracted name, `sectores_empresas` is **not** a
junction table: it holds `codigo_sector`, `nombre_sector text`, `descripcion_sector text`.*

### 2.6 Security and UI

**`usuarios`** — 4 rows, 11 columns, PK `id`, AI 41.
`id int AI` · `Login_usuario varchar(50)` · `Nombre_usuario varchar(100) NULL` ·
`Codigo_empresa int NULL` · `Codigo_tipo_usuario int(20) NULL` · `Direccion_usuario varchar(200) NULL` ·
`Telefono_usuario varchar(50) NULL` · `Email_usuario varchar(80) NULL` ·
`Clave_usuario varchar(20)` · `fech_venc date DEFAULT '0000-00-00'` · `estatus varchar(20)`
*`Clave_usuario varchar(20)` holds plaintext passwords; `Codigo_tipo_usuario` is `int(20)` while its
target `tipos_de_usuarios.Id_tipo_Usuario` is `int(11)`.*

**`tipos_de_usuarios`** — 6 rows, 2 columns, PK `Id_tipo_Usuario`, AI 30.
`Id_tipo_Usuario int AI` · `Nombre_tipo varchar(50) NULL`

**`seguridades`** — 146 rows, 3 columns, PK `Id_seguridad`, AI 829, `KEY (Id_tipo_Usuario)`.
`Id_tipo_Usuario int` · `id_pantalla int NULL` · `Id_seguridad int AI`
*The grant table. No unique constraint on (`Id_tipo_Usuario`, `id_pantalla`), so duplicate grants
are possible; and 11 rows reference type `5`, which does not exist.*

**`pantallas`** — 136 rows, 5 columns, PK `id_pantalla`, AI 151.
`id_pantalla int AI` · `nombre_pantalla varchar(100)` · `url_pantalla varchar(150)` ·
`prioridad int` · `id_modulo int`
*`url_pantalla` holds bare PHP filenames. Two rows share `tbl_pantalla_edit.php`.*

**`modulos`** — 24 rows, 5 columns, PK (`Id_modulo`, `Id_categoria`), `KEY (Id_categoria)`, AI 31.
`Id_modulo int AI` · `Nombre_modulo varchar(50) NULL` · `Prioridad int NULL` · `Id_categoria int` ·
`url varchar(255)`

**`categorias`** — 8 rows, 5 columns, PK `Id_categoria`, AI 9.
`Id_categoria int AI` · `Nombre_categoria varchar(50) NULL` · `Imagen varchar(50) NULL` ·
`Prioridad int NULL` · `desc varchar(250)`
*Different from `categorias_titulos` and from `categoria_sector` — three unrelated "category"
tables with three different key names and case conventions.*

### 2.7 Content

**`noticias`** — 63 rows, 10 columns, PK `codigo_noticia`, AI 87.
`codigo_noticia int AI` · `resumen_noticia text` · `noticia_completa longtext` ·
`codigo_categoria int NULL` · `titulo varchar(250) NULL` · `fecha_publicacion date NULL` ·
`estatus_noticia varchar(11) DEFAULT 'Pendiente'` · `imagen_1 text` · `imagen_2 text` · `origen text`

**`categoria_noticias`** — 5 rows, 3 columns, PK `codigo_categoria`, AI 52.
`codigo_categoria int AI` · `nombre_categoria varchar(50) NULL` · `descripcion_categoria varchar(250) NULL`

**`links_interes`** — 13 rows, 8 columns, PK `lit_IdLink`, AI 46.
`lit_IdLink int(9) AI` · `lit_Sector int(9)` · `lit_Detalle varchar(40)` · `lit_Link varchar(100)` ·
`lit_UsuarioCrea varchar(100)` · `lit_FechaCrea datetime DEFAULT '0000-00-00 00:00:00'` ·
`lit_UsuarioMod varchar(100)` · `lit_FechaMod datetime DEFAULT '0000-00-00 00:00:00'`
*Prefixes every column with `lit_`. `lit_Sector` has no discoverable target table.*

---

## 3. Audit, session, configuration and infrastructure tables

This is a short section because the answer is: **they do not exist.**

| Expected object | Status |
|---|---|
| Audit / change log | **Absent.** No table records who changed what. |
| Created/updated metadata | Partial and inconsistent — see below |
| Session store | **Absent.** PHP default file sessions; nothing in the schema |
| `usuarios_login` (login log) | **Absent**, though two code paths insert into it |
| Configuration / settings | **Absent.** Configuration lives in the git-ignored PHP file |
| Migrations / schema version | **Absent.** The dump *is* the schema |
| Queue / job / outbox | **Absent** |
| Soft-delete flags | **Absent.** Every delete is a hard `DELETE` |

**The only audit-ish columns in the schema** are the four-column block on three tables:

| Table | Columns | Defaults |
|---|---|---|
| `paises` | `Usuario_cre`, `Fecha_cre`, `Usuario_mod`, `Fecha_mod` | all `NULL` |
| `estados` | `Usuario_cre`, `Fecha_cre`, `Usuario_mod`, `Fecha_mod` | `''`, `'0000-00-00 00:00:00'`, `''`, `'0000-00-00 00:00:00'` |
| `localidades` | `Usuario_cre`, `Fecha_cre`, `Usuario_mod`, `Fecha_mod` | all `NULL` |
| `links_interes` | `lit_UsuarioCrea`, `lit_FechaCrea`, `lit_UsuarioMod`, `lit_FechaMod` | `''`, zero-datetime, `''`, zero-datetime |

So **4 of 35 tables** carry any creation/modification metadata, and they are the geography and
links tables — precisely the least business-critical ones. The tables that hold the domain
(`contratos`, `articulos_contratos`, `bitacoras`, `acuerdos`, `titulos`) carry **none**. The
inconsistency in defaults between `paises`/`localidades` (`NULL`) and `estados`/`links_interes`
(zero sentinels) also means the same intent is represented two ways.

---

## 4. Cardinality (measured, not declared)

| Parent → child | Rows | Observed ratio |
|---|---|---|
| `empresas` → `contratos` | 80 → 73 | 72 distinct companies in 73 agreements; **one company has 2** |
| `contratos` → `articulos_contratos` | 73 → 5,402 | ~74 clauses per agreement |
| `titulos` → `articulos_contratos` | 64 → 5,402 | heavily skewed: top heading has 853 clauses, one heading is unused |
| `bitacoras` → `peticiones_sindicato` | 2 → 9 | |
| `bitacoras` → `ofertas_patrono` | 2 → 4 | |
| `bitacoras` → `reuniones` | 2 → 2 | |
| `bitacoras` → `acuerdos` | 2 → 1 | |
| `reuniones` ↔ `peticiones` | 2 ↔ 9 | 4 links recorded |
| `otras_leyes` → `articulos_otras_leyes` | 47 → 2,982 | ~63 articles per statute |
| `tipos_de_usuarios` → `seguridades` | 6 → 146 | ~24 grants per type |

The negotiation side of the model is **exercised at token scale** (2 cases, 9 petitions, 1
agreement) while the agreement side is real (73 agreements, 5,402 clauses). That asymmetry is the
strongest evidence available about which half of the product actually saw use.
