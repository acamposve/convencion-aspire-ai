# Legacy to target mapping — implemented

Table-by-table mapping from the legacy MySQL schema to the **implemented** PostgreSQL schema.
This is the concrete successor to the entity-level proposal in
[schema-mapping.md](schema-mapping.md); where the implementation diverges from that proposal, the
change is recorded in §5.

Sources: [legacy-schema-inventory.md](legacy-schema-inventory.md) (35 tables) ·
[legacy-relationships.md](legacy-relationships.md) (implicit relationships and measured orphans) ·
[legacy-schema-issues.md](legacy-schema-issues.md) (34 defects) · target: [target-schema.md](target-schema.md).

Disposition legend: **MAP** one entity, redesigned · **SPLIT** one table became several ·
**MERGE** several tables became one · **ABSORB** no longer needed, its meaning is expressed
structurally · **REPLACE** superseded by a different mechanism · **DROP** no target · **DEFER** out
of the MVP.

---

## 1. Master mapping — all 35 legacy tables

| # | Legacy table | Rows | Target | Disposition |
|---|---|---|---|---|
| 1 | `empresas` | 80 | `app.company` | MAP |
| 2 | `contratos` | 73 | `app.agreement` | MAP |
| 3 | `articulos_contratos` | 5,402 | `app.agreement_clause` | MAP |
| 4 | `anexos_contratos` | 29 | `app.agreement_annex` | MAP |
| 5 | `bitacoras` | 2 | `app.negotiation_case` + `app.negotiation_party` | SPLIT |
| 6 | `peticiones_sindicato` | 9 | `app.union_petition` | MAP |
| 7 | `ofertas_patrono` | 4 | `app.employer_offer` | MAP |
| 8 | `reuniones` | 2 | `app.negotiation_meeting` | MAP |
| 9 | `acuerdos` | 1 | `app.meeting_agreement` | MAP |
| 10 | `reuniones_peticiones` | 4 | — | ABSORB |
| 11 | `reuniones_ofertas` | 4 | — | ABSORB |
| 12 | `reuniones_acuerdos` | 1 | — | ABSORB |
| 13 | `otras_leyes` | 47 | `app.statute` (kind `OtherStatute`) | MERGE |
| 14 | `ley_trabajo` | 1 | `app.statute` (kind `LabourLaw`) | MERGE |
| 15 | `articulos_otras_leyes` | 2,982 | `app.statute_article` | MERGE |
| 16 | `articulos_ley_trabajo` | 556 | `app.statute_article` | MERGE |
| 17 | `categorias_titulos` | 5 | `app.comparison_heading_group` | MAP |
| 18 | `titulos` | 64 | `app.comparison_heading` | MAP |
| 19 | `paises` | 237 | `app.country` | MAP |
| 20 | `estados` | 23 | `app.state` | MAP |
| 21 | `localidades` | 410 | `app.municipality` | MAP |
| 22 | `ciudades` | **0** | — | DROP |
| 23 | `sectores_empresas` | 3 | `app.company_sector` | MAP |
| 24 | `tipo_empresa` | 11 | `app.company_type` | MAP |
| 25 | `categoria_sector` | 18 | `app.company_category` | MAP |
| 26 | `actividad_empresa` | 21 | `app.economic_activity` | MAP |
| 27 | `categorias` | 8 | — | REPLACE |
| 28 | `modulos` | 24 | — | REPLACE |
| 29 | `pantallas` | 136 | `platform.capability` | REPLACE |
| 30 | `seguridades` | 146 | `platform.role_capability` | REPLACE |
| 31 | `tipos_de_usuarios` | 6 | `platform.role` | MAP |
| 32 | `usuarios` | 4 | `platform.user_account` + `platform.user_profile` (+ Supabase Auth) | SPLIT |
| 33 | `noticias` | 63 | `app.news_article` | MAP |
| 34 | `categoria_noticias` | 5 | `app.news_category` | MAP |
| 35 | `links_interes` | 13 | — | DEFER (FUT-05) |

**Tally:** 21 mapped, 3 merged, 2 split, 3 absorbed, 4 replaced, 2 dropped/deferred.

---

## 2. Column-level transformations that mattered

| Legacy column(s) | Target | Transformation | Why |
|---|---|---|---|
| `empresas.id` + `empresas.codigo_empresa` | `company.id` + `company.legacy_code` | Two identities → one key plus an optional attribute | The two sequences never coincided in 80 rows, agreements joined on the code, one user row used the id. Legacy code kept as data (A-42) |
| `contratos.status_publicacion char(2)` (`PE`/`Pe`/`PU`) | `agreement.state` enum + `published_at` + `published_by` | Three values for two states → enum + actor + timestamp with a consistency CHECK | Two rows held `'Pe'`, which matched no predicate in the application |
| `articulos_contratos.nro_articulos varchar(11)` | `number_int int` + `number_suffix text` | String → integer + suffix + derived ordering | Lexicographic sort produced 1, 10, 100, 11, 2 |
| `articulos_contratos.status_articulo` | **dropped** | — | 5,402 rows, one value, zero writers |
| `articulos_contratos.articulo_cerrado char(1)` | `is_excluded_from_compilation boolean` + `exclusion_note` | Renamed to state the behaviour; three-valued (`'0'`/`'1'`/NULL) → boolean + note | It excluded clauses from the printed compilation; the name said "closed" |
| `articulos_contratos.codigo_titulo_comparativo` (nullable, 899 zeros) | `heading_id uuid NOT NULL` | Nullable → mandatory, with `is_excluded` on the heading | Removes the 16.6% unclassified backlog structurally (A-16) |
| `articulos_contratos.campo_comparativo varchar(250)` | `economic_comparison_note text` | Kept as free text | No code ever read it; structuring it would invent semantics (A-18) |
| `bitacoras.representante_*` × 12 columns | `negotiation_party` rows | Fixed triplicated block → collection with `role` enum | A fourth party or two representatives needs no schema change |
| `bitacoras.estatus_bitacora char(3)` (`''`/`1`/`10`) | `negotiation_case.state` enum | Numeric codes in a character column → named enum | `'1'` was written only by *reopen*; no case ever reached `'10'` |
| `bitacoras.estatu_pase_contratos` | **dropped** | — | Read by dead JavaScript, written by nothing, empty in all rows |
| `bitacoras.fecha_inicio_disc text` (`dd/mm/yyyy`) | `negotiation_case.started_on date` | Text → `date` | — |
| `acuerdos.fecha_acuerdo text` (`dd-mm-yyyy`) | `meeting_agreement.agreed_on date` | Text → `date`, different format from the above | Two formats in one module |
| `reuniones.fecha_reunion text` (`yyyy-mm-dd`) | `negotiation_meeting.held_on date` | Text → `date` | Third format |
| `acuerdos.codigo_bitacora` + `codigo_peticion` + `codigo_oferta` + `codigo_reunion` | `case_id` + `petition_id` + `meeting_id` with two composite FKs | Four unlinked pointers → two constrained ones plus the case | Nothing prevented them disagreeing |
| `reuniones_*` (3 junction tables) | — | Absorbed: a petition and a meeting both belong to a case, and `meeting_agreement` names both | No PK, no index, redundant case id in each |
| `ley_trabajo` (no primary key) + `otras_leyes` | `statute` with `kind` | Two tables → one with a discriminator; partial unique index allows one `LabourLaw` | The single-row table had no referenceable identity, so its 556 articles had no FK |
| `articulos_ley_trabajo.nro_articulo int` vs `articulos_otras_leyes.nro_articulo varchar(11)` | `statute_article.number_int int` + suffix | One representation for the same concept | Same field, two types, two tables |
| `articulos_ley_trabajo.texto_completo_articulos` (plural) | `statute_article.body` | Renamed for consistency | Sibling tables used the singular |
| `total_articulos`, `total_articulos_ley` | **dropped** | Computed on read | Application-maintained counters drifted |
| `categorias_titulos.requiere_campo_comparacion_economica char(2)` (`'si'`/`'no'`) | `requires_economic_comparison boolean` | Spanish strings → boolean | — |
| Heading named "No Compara" (853 clauses) | `comparison_heading.is_excluded` | Taxonomy entry → property of the taxonomy | 15.8% of clauses sat under a heading that was really an opt-out |
| `paises`/`estados`/`localidades` composite keys with global `AUTO_INCREMENT` | `uuid` PK + natural-key unique indexes + composite uniques as FK targets | Surrogate keys, natural uniqueness, and hierarchy consistency via composite FKs | `Id_pais` in the primary key added no uniqueness |
| `paises.Usuario_cre`/`Fecha_cre`/... (4 tables only) | `created_at`/`updated_at` everywhere; `created_by`/`updated_by` on business tables | Uniform audit | Legacy had audit columns on 4 of 35 tables — the geography and links tables |
| `usuarios.Clave_usuario varchar(20)` (plaintext) | **no column** | Credentials move to Supabase Auth | `varchar(20)` cannot hold any modern hash; the value was compared with `LIKE` |
| `usuarios.estatus` (`'BLO'`) | `user_account.is_blocked` | String sentinel → boolean, enforced on every request | Legacy enforced it only at login |
| `usuarios.fech_venc date DEFAULT '0000-00-00'` | `user_account.expires_on date` nullable | Zero-date sentinel → NULL | `'0000-00-00'` is not a valid PostgreSQL date |
| `pantallas.url_pantalla varchar(150)` (PHP filenames) | `capability.key` (`resource.action`) | File-keyed grants → capability keys | A SPA has no filenames |
| `seguridades` (no uniqueness) | `role_capability` with PK | Duplicate grants impossible | 11 legacy grants pointed at a role type that did not exist |
| `documentos` stored as `text` filenames in 7 columns | `platform.document` + Storage | Filename columns → metadata entity with content-type allow-list, size limit and a unique server-generated path | Client-named, client-typed, `chmod 0777`, web-served |
| `usuarios_login` (absent from schema) | `platform.audit_entry` | The audit that never existed | Both writers targeted a table that does not exist |
| `noticias.estatus_noticia varchar(11)` (`'Pendiente'`/`'Publicar'`) | `news_article.state` enum + actor + timestamp | Free strings → enum with consistency CHECK | Create hardcoded `'Pendiente'`, so publishing required create-then-edit |

---

## 3. Dropped and replaced — nothing lost

| Legacy | Disposition | Reasoning |
|---|---|---|
| `ciudades` | DROP | 0 rows, referenced by nothing, a third geography level never used |
| `categorias` | REPLACE | Grouped the legacy admin menu; React has its own navigation |
| `modulos` | REPLACE | Same — menu grouping, plus a `url` column pointing at PHP files |
| `pantallas` | REPLACE → `capability` | A catalogue of PHP **filenames** used as permission keys |
| `seguridades` | REPLACE → `role_capability` | Grants on those filenames |
| `links_interes` | DEFER | Phase 1 Q-16: no requirement has ever been stated for this feature |
| `status_articulo` | DROP | Dead column |
| `estatu_pase_contratos` | DROP | Dead column |
| Annex publication state | DROP | All 29 rows `'Pendiente'`, read by no code |
| Three `reuniones_*` junctions | ABSORB | Their meaning is expressed by the case relationship |

---

## 4. New entities with no legacy counterpart

| Target | Why it must exist |
|---|---|
| `platform.audit_entry` | No audit existed. Append-only, written in the same transaction as the change |
| `platform.capability` + `role_capability` | File-keyed permissions cannot survive a SPA |
| `platform.user_account` / `user_profile` | Identity moved to Supabase Auth; authorisation records still needed |
| `platform.document` | The 7 filename columns needed a real metadata entity |
| Uniform audit columns | Legacy had them on 4 of 35 tables |
| `agreement.title` / `agreement.number` | Agreements had no identity at all |
| `negotiation_case.promoted_agreement_id` | Promotion was untracked, so it could duplicate every clause |
| `comparison_heading.is_excluded` | The "No Compara" concept, modelled as a property |

---

## 5. Divergences from the Phase 2 proposal

Recorded because the Phase 2 mapping was an entity-level sketch and the implementation had to make
concrete choices.

| Phase 2 proposal | Implemented | Why |
|---|---|---|
| Two schemas: one `DbContext`, one `public` schema | **Two schemas** `app` + `platform`, one `DbContext` | The Phase 4 requirement asks to separate business from infrastructure tables. Cross-module atomicity is preserved because the DbContext is still single. Privilege separation is the concrete benefit: `anon` has no `USAGE` on `platform` |
| `Company` + separate `CompanyAddress` / `CompanyContact` entities | Address and contact as **owned columns** on `company` | Neither has independent identity or lifecycle, and no invariant spans them. Separate tables would add two joins per company read for no constraint benefit |
| `MeetingPetition` junction table | **No junction.** `meeting_agreement` carries `case_id` + `petition_id` + `meeting_id` with composite FKs | A petition and a meeting both already belong to a case, so the junction was redundant; the composite FKs enforce same-case membership, which a junction could not |
| `Statute.ArticleCount()` computed | Computed, no column | As proposed |
| `Document` entity | `platform.document` + Storage | As proposed |
| — | `platform.document.extraction_status` enum | The reserved seam for future text extraction (A-10), now a typed state rather than a string |
| — | `platform.audit_entry.correlation_id` | Ties audit rows to a request trace |

---

## 6. What was *not* carried over, by decision

Recorded so the absence is deliberate rather than an oversight:

- **No data.** Not one row of the 10,634 legacy rows exists in the target, apart from the five
  comparison heading groups and one country, which were re-authored as reference data (Art. V.2,
  Art. V.3). Verified: zero business rows after migration and seed.
- **No legacy identifier scheme.** No `codigo_*` columns, no `PE`/`PU` codes, no `char(3)` state
  codes, no `'0000-00-00'`. Verified by the legacy-name scan in
  [validation-report.md](validation-report.md) §3.
- **No `status_articulo` workflow.** It never existed in practice.
- **No filename-keyed permissions.** The model was replaced, not translated.
- **No announcement that the schema is final.** `links_interes` (FUT-05) and any tenant dimension
  remain open and would require migrations.
