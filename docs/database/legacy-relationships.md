# Legacy relationships — declared vs. actual

How the legacy schema expresses relationships, what the application actually relies on, and how
well each relationship holds in the data.

Every orphan count below was **computed from the dump**, not inferred. Parsing was done by reading
the `INSERT` statements directly; the dump was not restored and no SQL was executed against a
database.

---

## 1. Declared relationships: there are none

| Mechanism | Count in schema |
|---|---|
| `FOREIGN KEY` constraints | **0** |
| `REFERENCES` clauses | **0** |
| Triggers that could enforce or cascade | **0** |
| Stored procedures / functions | **0** |
| Views | **0** |
| Unique constraints | **1** (on `articulos_ley_trabajo.nro_articulo`, which is already the primary key — redundant) |
| Secondary indexes | **4** |

All 35 tables are **MyISAM**, so even if foreign keys had been declared they would have been parsed
and ignored. There is therefore **no referential integrity of any kind, at any layer of the
database**.

The four secondary indexes are:
`ciudades(Id_pais)` · `localidades(Id_estado, Id_pais)` — *misnamed `Id_ciudad`* ·
`modulos(Id_categoria)` · `seguridades(Id_tipo_Usuario)`.

Note what is *missing* from that list: no index on `contratos.codigo_empresa`, on
`articulos_contratos.codigo_contratos` (5,402 rows traversed by every clause listing), on
`articulos_contratos.codigo_titulo_comparativo` (the entire comparison feature joins on it), or on
`bitacoras.codigo_empresa`. Every join in the application is a full table scan.

## 2. How the implicit relationships were derived

Three independent sources, and each relationship below is supported by at least two of them:

1. **PHP queries** — `JOIN` clauses and `WHERE` predicates in `lib/objetos/`. These are the
   authoritative statement of intent.
2. **Column naming** — `codigo_<entity>` / `Id_<entity>` pointing at `<entity>`'s key.
3. **Data validation** — resolving every child value against the parent's key and counting
   orphans. This is what distinguishes a real relationship from a naming coincidence.

Method 3 is what makes this document trustworthy: a relationship whose values all resolve is real;
a relationship with a specific, small set of dangling values is real *and* violated; a relationship
that resolves against a *different* column than its name suggests is a modelling error.

---

## 3. The decisive finding: two competitor identities for a company

`empresas` has both `id` (primary key, `AUTO_INCREMENT`) and `codigo_empresa` (`int NOT NULL`, no
default, not auto-increment). The two sequences are completely independent:

| | Range | Distinct |
|---|---|---|
| `id` | 116 – 195 | 80 |
| `codigo_empresa` | 2 – 115 | 80 |
| rows where `id = codigo_empresa` | **0 of 80** | |

Resolving `contratos.codigo_empresa` against each candidate settles which one the application means:

| Target | Orphans out of 71 non-zero agreements |
|---|---|
| `empresas.codigo_empresa` | **2** |
| `empresas.id` | **71** |

So the operational key is **`codigo_empresa`** — it is what every PHP query uses
(`empresas.php:29,49,69,89,147,423`). But the data contains one direct counter-example:

> `usuarios.Codigo_empresa = 116` resolves against `empresas.id` and **not** against
> `empresas.codigo_empresa`.

At least one code path (or one manual data edit) used the primary key where the rest of the system
expects the business code. And `empresas.php:360` — the only `INSERT` in the repository — omits
`codigo_empresa` entirely, so it cannot produce valid rows at all. The 80 populated rows must
predate that code.

**Conclusion:** the company entity has two identities in use simultaneously, one of them unwritten.
Any faithful mapping must collapse this to a single identifier, and must treat `codigo_empresa` as
*data* (a business code, if it is one) rather than as the key. See `schema-mapping.md` §Company and
[../legacy/open-questions.md](../legacy/open-questions.md) Q-18.

---

## 4. Complete implicit-relationship catalogue

Columns: `rows` = child rows · `zero` = child rows holding the literal `0` ("no relation") ·
`real` = rows with a non-zero value · `orph` = real values with no parent · `dst` = distinct
missing values.

### 4.1 Agreement cluster

| Child column | Parent column | rows | zero | real | orph | dst | PHP evidence |
|---|---|---|---|---|---|---|---|
| `contratos.codigo_empresa` | `empresas.codigo_empresa` | 73 | 2 | 71 | **2** | 2 | `comparador.php:241`, `discusion.php:1027` |
| `anexos_contratos.codigo_contrato` | `contratos.codigo_contrato` | 29 | 0 | 29 | **2** | 2 | `contratos.php` annex methods |
| `articulos_contratos.codigo_contratos` | `contratos.codigo_contrato` | 5,402 | 0 | 5,402 | **57** | 1 | `contratos.php:711`, `comparador.php:241` |
| `articulos_contratos.codigo_titulo_comparativo` | `titulos.codigo_titulo_comparativo` | 5,402 | 899 | 4,503 | 0 | 0 | `contratos.php:861` |
| `titulos.codigo_categoria_titulo` | `categorias_titulos.codigo_categoria_titulos` | 64 | 0 | 64 | 0 | 0 | `titulos.php:242` |

The 57 orphans in `articulos_contratos` are all one missing contract id: **57 clauses belong to an
agreement that no longer exists**, left behind because the delete path removes the agreement and
then deletes its clauses in a separate, untransacted statement that evidently did not run.

### 4.2 Negotiation cluster

| Child column | Parent column | rows | zero | real | orph | dst | PHP evidence |
|---|---|---|---|---|---|---|---|
| `bitacoras.codigo_empresa` | `empresas.codigo_empresa` | 2 | 0 | 2 | **1** | 1 | `discusion.php:1027` |
| `peticiones_sindicato.codigo_bitacora` | `bitacoras.codigo_bitacora` | 9 | 0 | 9 | 0 | 0 | `discusion.php:733` |
| `ofertas_patrono.codigo_bitacora` | `bitacoras.codigo_bitacora` | 4 | 0 | 4 | 0 | 0 | `discusion.php:948` |
| `ofertas_patrono.codigo_peticion` | `peticiones_sindicato.codigo_peticion` | 4 | 1 | 3 | **3** | 2 | `discusion.php:948` |
| `reuniones.codigo_bitacora` | `bitacoras.codigo_bitacora` | 2 | 0 | 2 | 0 | 0 | |
| `acuerdos.codigo_bitacora` | `bitacoras.codigo_bitacora` | 1 | 0 | 1 | 0 | 0 | `discusion.php:886` |
| `acuerdos.codigo_peticion` | `peticiones_sindicato.codigo_peticion` | 1 | 0 | 1 | **1** | 1 | `discusion.php:886` |
| `acuerdos.codigo_oferta` | `ofertas_patrono.codigo_ofertas` | 1 | 0 | 1 | 0 | 0 | |
| `acuerdos.codigo_reunion` | `reuniones.codigo_reunion` | 1 | 0 | 1 | 0 | 0 | |
| `acuerdos.codigo_titulo_comparativo` | `titulos.codigo_titulo_comparativo` | 1 | 0 | 1 | 0 | 0 | |

The negotiation cluster is where the data is thinnest (2 cases, 1 agreement) **and** where the
orphans are proportionally worst: 3 of 4 offers point at petitions that do not exist, and the single
agreement points at a missing petition. With n=4 this is not statistically meaningful, but it is
consistent with the code: `insertar_Acuerdos_Reuniones` (`discusion.php:1108`) writes offers and
agreements from a bulk screen that hardcodes identifiers and bypasses the DB abstraction.

### 4.3 Junction tables

| Child column | Parent column | rows | orph |
|---|---|---|---|
| `reuniones_peticiones.codigo_reuniones` | `reuniones.codigo_reunion` | 4 | 0 |
| `reuniones_ofertas.codigo_reuniones` | `reuniones.codigo_reunion` | 4 | 0 |
| `reuniones_acuerdos.codigo_reuniones` | `reuniones.codigo_reunion` | 1 | 0 |
| `reuniones_acuerdos.codigo_acuerdos` | `acuerdos.codigo_acuerdos` | 1 | 0 |

Three junction tables, each with **no primary key, no unique constraint, and no index**, so the same
pair can be recorded twice with nothing to prevent it. Each also carries a redundant
`codigo_bitacora` column that duplicates the case id already implied by the meeting — a
denormalisation that permits a row to link a meeting from case A to a petition from case B.

Note the naming mismatch that makes an FK impossible even if someone tried:

| Junction column | Parent primary key |
|---|---|
| `codigo_reuniones` (plural) | `reuniones.codigo_reunion` (singular) |
| `codigo_peticiones` (plural) | `peticiones_sindicato.codigo_peticion` (singular) |
| `codigo_acuerdos` (plural) | `acuerdos.codigo_acuerdos` (plural — this one matches) |

### 4.4 Legal library

| Child column | Parent column | rows | zero | real | orph | dst |
|---|---|---|---|---|---|---|
| `articulos_otras_leyes.codigo_otra_ley` | `otras_leyes.codigo_otra_ley` | 2,982 | 0 | 2,982 | **8** | 5 |
| `articulos_ley_trabajo.codigo_titulo_comparativo` | `titulos.codigo_titulo_comparativo` | 556 | 199 | 357 | 0 | 0 |
| `articulos_otras_leyes.codigo_titulo_comparativo` | `titulos.codigo_titulo_comparativo` | 2,982 | 2,672 | 310 | 0 | 0 |

`ley_trabajo` has **no primary key**, so `articulos_ley_trabajo` has **no parent identifier to
reference** — the relationship between the labour statute and its 556 articles is expressed only by
convention (there is exactly one statute, so no column is needed).

The comparison-coverage numbers here matter for the product, not just the schema: **89.6% of other
statute articles carry comparison heading `0`** (2,672 of 2,982), and 35.8% of labour-law articles
do. Statute-vs-clause comparison can therefore only work for a tenth of the statute corpus.

### 4.5 Security and UI

| Child column | Parent column | rows | orph | dst | Note |
|---|---|---|---|---|---|
| `seguridades.Id_tipo_Usuario` | `tipos_de_usuarios.Id_tipo_Usuario` | 146 | **11** | 1 | all 11 point at type `5`, which does not exist |
| `seguridades.id_pantalla` | `pantallas.id_pantalla` | 146 | 0 | 0 | nullable, no nulls present |
| `pantallas.id_modulo` | `modulos.Id_modulo` | 136 | 0 | 0 | |
| `usuarios.Codigo_tipo_usuario` | `tipos_de_usuarios.Id_tipo_Usuario` | 4 | 0 | 0 | |
| `usuarios.Codigo_empresa` | `empresas.codigo_empresa` | 4 | **1** | 1 | the value is a valid `empresas.id` (§3) |

`seguridades` also has **no unique constraint on (`Id_tipo_Usuario`, `id_pantalla`)**, so a role can
be granted the same screen repeatedly; and its own PK `Id_seguridad` is an `AUTO_INCREMENT` that is
pure surrogate — the grant has no identity of its own.

A second security-related integrity gap is in `pantallas`: `url_pantalla` is the *de facto* lookup
key used by the permission check (`pantalla.php:54` `where url_pantalla = %s`), but it is neither
unique-constrained **nor unique in the data** — `tbl_pantalla_edit.php` appears twice (screens 1 and
65), so `getporUrl()` can return either, and a grant made on one screen silently applies to the
other.

### 4.6 Geography

| Child column | Parent column | rows | zero | orph |
|---|---|---|---|---|
| `estados.Id_pais` | `paises.Id_pais` | 23 | 0 | 0 |
| `localidades.Id_estado` | `estados.Id_estado` | 410 | 0 | 0 |
| `localidades.Id_pais` | `paises.Id_pais` | 410 | 0 | 0 |

This is the **only fully consistent hierarchy in the schema** — which is unsurprising, since it is
the one cluster with composite keys that structurally enforce the parent (`estados` PK is
`(Id_estado, Id_pais)`), real secondary indexes, and an actual cascade implemented in code
(`eliminarpais.php:10-12`).

Note the structural oddity: `estados` has `Id_estado` as `AUTO_INCREMENT` but the primary key is
`(Id_estado, Id_pais)`. Since the auto-increment is global, the composite key neither adds
uniqueness nor scopes numbering per country — `Id_pais` in the PK is decorative.

### 4.7 Organisation attribution

| Child column | Parent column | rows | zero | orph |
|---|---|---|---|---|
| `empresas.codigo_pais` | `paises.Id_pais` | 80 | 3 | 0 |
| `empresas.codigo_estado` | `estados.Id_estado` | 80 | 13 | 0 |
| `empresas.codigo_localidad` | `localidades.Id_localidad` | 80 | 15 | 0 |
| `empresas.codigo_sector` | `sectores_empresas.codigo_sector` | 80 | 1 | 0 |
| `empresas.codigo_tipo` | `tipo_empresa.codigo_tipo` | 80 | 12 | **1** |
| `empresas.codigo_categoria` | `categoria_sector.codigo_categoria` | 80 | 10 | 0 |
| `empresas.codigo_actividad` | `actividad_empresa.codigo_actividad` | 80 | 17 | 0 |
| `noticias.codigo_categoria` | `categoria_noticias.codigo_categoria` | 63 | 0 | 0 |

Zero-orphan but heavily zero-populated: **17 of 80 companies have no activity**, 15 no locality, 13
no state, 12 no type, 10 no category. The `0` default is doing the work that `NULL` should do, so
"no value" and "value zero" are the same state and cannot be distinguished.

---

## 5. Orphan summary

**87 orphan rows across 10 relationships**, out of 146 relationship-bearing row instances that have
a real (non-zero) value:

| Relationship | Orphans | Consequence |
|---|---|---|
| `articulos_contratos` → `contratos` | **57** | Clauses stranded by a non-transactional delete |
| `seguridades` → `tipos_de_usuarios` | **11** | Grant rows for a role that does not exist |
| `articulos_otras_leyes` → `otras_leyes` | **8** | Articles of deleted statutes |
| `ofertas_patrono` → `peticiones_sindicato` | 3 | Offers of deleted petitions |
| `contratos` → `empresas` | 2 | Agreements of deleted companies |
| `anexos_contratos` → `contratos` | 2 | Annexes of deleted agreements |
| `bitacoras` → `empresas` | 1 | Negotiation case of a deleted company |
| `acuerdos` → `peticiones_sindicato` | 1 | Agreement attached to a missing petition |
| `usuarios` → `empresas` | 1 | User pointing at the *other* company identity |
| `empresas.codigo_tipo` → `tipo_empresa` | 1 | Company of a deleted type |

That these numbers are small is not reassurance — it is the signature of manual cascades that
mostly worked. The 57 stranded clauses prove the mechanism fails whenever a multi-statement delete
is interrupted, and with MyISAM there is no rollback to save it.

---

## 6. Relationships the schema cannot express at all

| Relationship the domain needs | Why the schema cannot hold it |
|---|---|
| Agreement → its clauses **ordered by number** | `nro_articulos` is `varchar(11)`, so "next number" is undefined and ordering is lexicographic |
| Clause → an aligned clause in another agreement | No such notion exists; comparison is by heading only |
| Negotiation case → audit history of state changes | No audit table; `estatus_bitacora` is overwritten in place |
| User → who created/modified a record | No created/updated columns on any business table |
| Publication → who published, when | Only a `char(2)` flag |
| Statutory article → its statute (labour law) | `ley_trabajo` has no primary key, so no reference target exists |
| Agreement → company, unambiguously | Two competing company identities (§3) |
| Screen → unique URL | `url_pantalla` is neither unique-constrained nor unique (§4.5) |

---

## 7. Ownership / tenant analysis

The task asks about tenant relationships. There is **no tenancy dimension** in the schema: no
`tenant_id`, no owner column, no partitioning key. The system appears to be single-organisation.

The closest thing is `usuarios.Codigo_empresa` — a user may be attached to a company, and
`discusion_contratos_listar.php:8` scopes the negotiation list by `$user->codigo_empresa`. So
"company" doubles as a **soft tenancy boundary for exactly one screen**, and it is enforced by
passing a filter, not by authorisation. With 3 of 4 users blocked and one referencing a company by
the wrong key, even that cannot be verified in practice.

If the replacement needs multi-company scoping, it is a **new requirement**, not a port.
