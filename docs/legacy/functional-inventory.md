# Functional inventory — legacy PHP application

Read-only inventory of what exists in `admin/`, `lib/`, `plantillas/`. Companion documents:
[functional-flows.md](functional-flows.md) (journeys), [business-rules.md](business-rules.md)
(rules and states), [security-findings.md](security-findings.md), [open-questions.md](open-questions.md).

## Method

Every entry below was traced through code, not inferred from filenames. Three recurring
distinctions matter and are marked explicitly:

- **Reachable** — a route in a dispatcher `switch` that resolves to a file that exists.
- **Dangling** — a route whose target file does not exist (an action that cannot work).
- **Orphan** — a file that exists but that no route reaches.

The application cannot be executed (see [security-findings.md](security-findings.md) §S15), so
"reachable" means statically reachable. No behavior here was observed at runtime.

---

## 1. Corpus

| Area | Files | LOC | Nature |
|---|---|---|---|
| `admin/modulos/` | 215 | 27,787 | Controllers + views (mixed HTML/PHP) |
| `lib/objetos/` | 33 | 12,309 | Domain objects + SQL |
| `lib/funciones/php/` | 146 | 13,836 | Route controllers, helpers, vendored FPDF |
| `lib/bd/` | 3 | 1,243 | DB abstraction (MySQL + ODBC drivers) |
| **First-party total** | **397** | **55,175** | |
| `lib/componentes/` (vendored) | 37 | 3,825 | xajax, calendario |
| `lib/funciones/js/` (vendored) | 243 files | — | tinyMCE, HTMLArea |
| `lib/funciones/php/pdf/` (vendored) | 41 files | — | FPDF 1.6 + HTML2FPDF 3.0β |

444 PHP files in total; **61 use latin1 encoding** and are not valid UTF-8, which breaks naive
tooling (a documented hazard: two of my own tool invocations failed on these).

## 2. Route inventory

**179 route entries** are registered across seven dispatcher `switch` statements. **8 are
dangling.** Routing is a two- or three-level chain:

```
admin/index.php?url=<module>&tbl=<entity>&accion=<action>
  → admin/modulos/settings.php        switch($_GET['url'])   — whitelist, has default
    → admin/modulos/<module>/index.php  reads $_GET['tbl'], $_GET['accion']
      → admin/modulos/<module>/settings.php  switch($_GET['tbl']) → switch($_GET['accion'])
        → include($dir/$url) or include($tbl)
```

| Dispatcher | Routes | Dangling | Outer `default`? |
|---|---|---|---|
| `admin/modulos/settings.php` | 7 | 0 | **yes** (safe) |
| `contratos/settings.php` | 50 | 0 | **NO** → `include($_GET['tbl'])` (see S6) |
| `leyes/settings.php` | 23 | 4 | yes |
| `tablas/settings.php` | 71 | 0 | yes |
| `seguridad/settings.php` | 22 | 4 | yes |
| `perfil/settings.php` | 4 | 0 | yes |
| `bienvenida/settings.php` | 2 | 0 | yes |

**Dangling routes (actions that cannot work):**

| Route | Target that does not exist |
|---|---|
| `leyes` / `LOT` → `accion=imprimir` | `lys_ley_trabajo_imprimir.php` |
| `leyes` / `LOT` → default | `lys_ley_trabajo_listar.php` |
| `leyes` / `Otras Varias` → `accion=imprimir` | `lys_otras_leyes_imprimir.php` |
| `leyes` / default | `lys_leyes_listar.php` |
| `seguridad` → `accion=agregar/editar/eliminar/ver` on `tbl_seguridad` | `tbl_seguridad_{add,edit,eli,ver}.php` |

The last row matters functionally: **permission grants can be listed but never created, edited or
deleted through the UI.** The RBAC administration is half-built.

---

## 3. Modules

### 3.1 `bienvenida` — landing page
Routes: 2. Files: 3 (491 LOC). Renders a splash image grid and enforces account expiry
(`principal.php:5-70`, see [business-rules.md](business-rules.md) §BR-11). Includes the
role-filtered `menu/menu.php` at `index.php:84`.

**Verdict: CONFIRMED but trivial.** The splash is obsolete; the expiry check is a real intent in
an unusable implementation.

### 3.2 `contratos` — the core module
50 routes, 71 PHP files, 11,097 LOC. Three sub-areas under one `tbl` switch:

**(a) Agreements and clauses — `tbl=Contratos`, 21 files, 17 routes.**
Entities: `contratos`, `articulos_contratos`, `anexos_contratos`, `empresas`, `titulos`.
Actions: `listar`, `agregar`, `editar`, `ver`, `eliminar`, `publicar_contrato`;
clauses `listar_articulos`, `agregar_articulos`, `editar_articulos`, `ver_articulo`,
`eliminar_articulos`, `impresion_articulos`, `impresion_articulos_total`;
annexes `listar_anexos`, `agregar_anexos`, `editar_anexos`, `ver_anexos`, `eliminar_anexos`.

Note that `contratos` has **no name or title column** — an agreement is identified only by its
company, validity dates and scope (`ambito_aplicacion`). There is no agreement number.

**(b) Comparison — `tbl=Comparacion de Contratos`, 7 files, 2 routes.**
Entities (read-only): `articulos_contratos`, `contratos`, `empresas`, `titulos`,
`categorias_titulos`, `articulos_ley_trabajo`, `articulos_otras_leyes`, `otras_leyes`.
Live entry: `comparador.php`. **Writes nothing.**

**(c) Negotiation — `tbl=Bitacora`, 36 files, 27 routes.**
Entities: `bitacoras`, `peticiones_sindicato`, `ofertas_patrono`, `reuniones`, `acuerdos`, the
three `reuniones_*` join tables, plus `contratos`/`articulos_contratos` on close.
Actions: case CRUD + close/reopen; per-child list/add/edit/verify/delete for petitions,
offers, meetings and agreements; `impresion_gen` print preview; a pop-up history screen.

**Verdict: (a) and (c) CONFIRMED-MVP, (b) CONFIRMED-MVP.** All three carry defects that are
documented in the companion files and must not be carried over.

### 3.3 `leyes` — legal library
23 routes (4 dangling), 22 files, 2,764 LOC. Two sub-areas:

- **`LOT`** — the labour statute, held as a *single* row in `ley_trabajo` (a table with **no
  primary key**, so a second row breaks the module: the loader requires
  `realcount()==1`). Articles in `articulos_ley_trabajo`.
- **`Otras Varias`** — statutes in `otras_leyes`, articles in `articulos_otras_leyes`, one PDF
  per statute under `otras_varias/documentos/<codigo>/`.

**Confirmed defect:** `otras_varias/lys_articulos_otras_leyes_listar.php` is **truncated
mid-expression** (1,460 bytes, ends `while( list( $key, $val) =`), so the article list for other
statutes is a PHP parse error. That screen is dead; its CRUD is reachable only by hand-built URL.

**Verdict: CONFIRMED-DEFERRED by Product Owner decision** — in scope as a feature but starting
empty, with no legacy content. Inventory recorded for shape only.

### 3.4 `seguridad` — users, roles, permissions
22 routes (4 dangling), 19 files, 1,902 LOC. Entities: `usuarios`, `tipos_de_usuarios`,
`pantallas`, `seguridades`, `modulos`.
Actions: user CRUD; user-type CRUD; screen catalogue CRUD; grant listing.
Permission model: `usuarios.Codigo_tipo_usuario` → `seguridades(Id_tipo_Usuario, id_pantalla)` →
`pantallas(id_pantalla, url_pantalla, id_modulo)` → `modulos`.

**Verdict: CONFIRMED-MVP, must be redesigned.** The model is sound; enforcement is not (see
[security-findings.md](security-findings.md) §S5). Grant editing screens are missing.

### 3.5 `perfil` — self-service profile
4 routes, 5 files, 367 LOC. Edit own profile; change own password (no current-password check).

**Verdict: CONFIRMED-MVP.**

### 3.6 `tablas` — master data
71 routes, 85 files, 10,618 LOC. Thirteen managed entities, each with list/add/edit/delete/verify:

| Menu label | Table | Notes |
|---|---|---|
| Paises | `paises` | |
| Estados | `estados` | |
| Localidad | `localidades` | |
| Bloques de Clausulas | `categorias` | carries `requiere_campo_comparacion_economica` |
| Comparativo de Clausulas | `titulos` | comparison headings (64 rows) |
| Sectores | `sectores_empresas` | **a catalogue, not a join table** |
| Tipos | `tipo_empresa` | |
| Categorias | `categoria_sector` | |
| Actividades | `actividad_empresa` | |
| Empresas | `empresas` | logo upload |
| Noticias | `noticias` | publish flag |
| Categorias de Noticias | `categoria_noticias` | |
| Links de Interes | `links_interes` | |

Plus two unreachable screens: `noticiero/` (`Boletin`) and `documentos/` — neither has a
`modulos` row, so nothing links to them.

**Verdict: catalogs CONFIRMED-MVP** (they are the reference data the platform cannot function
without). `links_interes` is **UNCERTAIN** — no requirement has been stated for it.

### 3.7 `login` / `menu` / `principal`
Login form is **non-functional** as wired: the header form field names (`login2`/`pass2`) are
never copied to the names the handler reads, and the intermediary function
`levantar_sesion2()` is defined but never called. Two parallel menus exist; the one the shell
actually renders (`admin/menu2.php`) is static and applies **zero** role filtering.

**Verdict: OBSOLETE** — all three are replaced by the authenticated shell (and Supabase Auth).

---

## 4. Table usage — what the code actually touches

35 tables exist in the dump. Traced usage:

| Status | Tables |
|---|---|
| Read **and** written | `articulos_contratos`, `articulos_ley_trabajo`, `articulos_otras_leyes`, `acuerdos`, `anexos_contratos`, `bitacoras`, `categorias`, `categorias_titulos`, `contratos`, `empresas`, `estados`, `ley_trabajo`, `links_interes`, `localidades`, `modulos`, `noticias`, `ofertas_patrono`, `otras_leyes`, `paises`, `pantallas`, `peticiones_sindicato`, `reuniones`, `seguridades`, `sectores_empresas`, `tipo_empresa`, `tipos_de_usuarios`, `titulos`, `usuarios`, `actividad_empresa`, `categoria_sector` |
| Written, absent from schema | `usuarios_login` — target of both login-audit writers; the table does not exist |
| Referenced by joins, never written | `reuniones_peticiones`, `reuniones_ofertas`, `reuniones_acuerdos`, `categoria_noticias`, `ciudades` |
| In schema, no code path | `ciudades` (0 rows) |

`categoria_noticias` is listed as written by `noticias.php:306` in one audit and as having no
working writer; the insert there builds
`VALUES ( '".$nombre_categoria."', '.".$descrip_categoria."' )` — a stray `.` inside the literal,
producing malformed SQL. Recorded as **UNCERTAIN**.

---

## 5. Files, documents and storage

| Artefact | Location | Produced by |
|---|---|---|
| Agreement act PDF (`pdf_auto_acta`) | `contratos/documentos/<codigo_contrato>/` | uploaded, linked from lists |
| Negotiation PDFs (petitions, offers) | `discusion/documentos/<codigo_bitacora>/` | uploaded |
| Clause printing | — | browser print / `document.write` pop-up, **not** PDF |
| Statute PDFs | `ley_trabajo/media/`, `otras_varias/documentos/<codigo>/` | uploaded |
| Company logo | `tablas/empresa/documentos/<codigo_empresa>/` | uploaded |
| News images | `tablas/noticias/documentos/<codigo_noticia>/` | uploaded |
| Comparison report | `reporte.pdf` | `generar_pdf.php` via HTML2FPDF |

Only **one** PDF generator is live (`comparacion/generar_pdf.php`), and it is reachable only from
an orphaned xajax shell — see [functional-flows.md](functional-flows.md) §F4.

Uploads are stored under the **client-supplied filename** with `chmod 0777`, into directories the
web server serves. See [security-findings.md](security-findings.md) §S7.

No external integrations exist: no email, no SMS, no queue, no webhook, no third-party API.
Scheduled work does not exist: no cron entry point, no daemon, no job table.

---

## 6. Dead code and duplicates

397 first-party files, of which a large fraction is unreachable or superseded. Register:

| Category | Files |
|---|---|
| Superseded class twins | `lib/objetos/{discusion_old,contratos_old,noticias_old,noticias_2,links_interes_2010-05-27,Sectorlinks_interes_2010-05-27,2user,usuarios}.php` |
| Duplicate class declarations | `usuarios.php` and `tipo_usuario.php` both declare `class TIPO_USUARIO`; including both is a fatal redeclaration |
| Misfiled router | `lib/objetos/settings.php` declares no class — it is a module router duplicating `tablas/settings.php` |
| Shadowed views | `admin/modulos/contratos/acuerdos/` (2 files) — unreachable; the `Bitacora` branch maps to `discusion/`, whose same-named files win |
| Orphan comparison | `comparacion1.php` (zero inbound references), `comparador-30-04-2009.php`, `comparacion_old.php`, `comparacion_contratos_listar_old.php` (byte-identical to live) |
| Orphan xajax | `FuncionesDiscusionesXajax.php` (29 KB) — includes itself only; never loaded |
| Unreachable screens | `leyes/ley_trabajo/lys_ley_trabajo_list.php`, `tablas/noticiero/`, `tablas/documentos/` |
| Stale parked copies | `tablas/media/lys_otras_leyes_listar.php`, `plantillas/plantilla_admin/images/comparador.php` |
| Duplicate controllers | `lib/funciones/php/{agregar,editar,eliminar}{pais,estado,localidad}.php` (9 files) superseded by `lib/funciones/php/tablas/`, yet still wrongly included from `tablas/estado/index.php:13` |
| Dead helper | `lib/funciones/php/login.php` — included by nothing |
| Test/sample | `discusion/prueba.php`, `discusion/discusion1.php`, `discusion/reuniones.php` |
| Editor litter | 21 `.LCK` files, 21 `Thumbs.db`, 21 `dwsync.xml`, `*.php_old`, `*.bak` |

Also: `contratos/settings.php` lines 254–372 contain an entire **commented-out** fourth branch
(`Discusion de contratos`) duplicating the live `Bitacora` branch — roughly 120 lines of dead
configuration that looks like active code.

---

## 7. Feature classification — master table

`CONFIRMED-MVP` = real behavior, in scope. `CONFIRMED-DEFERRED` = real, later release.
`OBSOLETE` = exists but must not be rebuilt. `UNCERTAIN` = needs a Product Owner decision.

| # | Feature | Evidence | Class |
|---|---|---|---|
| 1 | Login / authentication | `lib/objetos/user.php:154`, `lib/funciones/php/login.php` | CONFIRMED-MVP (redesign; currently bypassed) |
| 2 | Role + permission matrix (type → screen) | `lib/objetos/seguridad.php:55`, `pantalla.php:54` | CONFIRMED-MVP (data model only) |
| 3 | User CRUD, block, expiry | `admin/modulos/seguridad/usuarios/*`, `bienvenida/principal.php:5` | CONFIRMED-MVP (expiry semantics unresolved) |
| 4 | Self-service profile + password change | `admin/modulos/perfil/*` | CONFIRMED-MVP |
| 5 | Agreement CRUD | `contratos/settings.php:25-115` | CONFIRMED-MVP |
| 6 | Clause authoring (rich text + summary + heading) | `lib/objetos/contratos.php:928` | CONFIRMED-MVP |
| 7 | Agreement annexes | `contratos/settings.php:75-97` | CONFIRMED-MVP |
| 8 | Publish / unpublish agreement | `lib/funciones/php/contratos/contratos/publicar.php` | CONFIRMED-MVP (two-click defect; must be fixed) |
| 9 | Clause printing | `contratos_articulo_contratos_impresion_clausulas.php` | CONFIRMED-MVP |
| 10 | Negotiation case CRUD | `discusion/settings.php:132-253` | CONFIRMED-MVP |
| 11 | Petitions → offers → meetings → agreements | `discusion_contratos_agregar_reuniones_externa.php:48-127` | CONFIRMED-MVP |
| 12 | Close case + promote agreements to clauses | `cerrar_discusion.php:28-51` | CONFIRMED-MVP (not idempotent) |
| 13 | Reopen case / reopen single petition | `reabrir_discusion.php:7`, `discusion.php:1185` | CONFIRMED-MVP |
| 14 | Negotiation history (per meeting) | `discusion_contratos_historico.php` | CONFIRMED-MVP |
| 15 | Compare clauses across published agreements by heading | `comparador.php:238-449` | CONFIRMED-MVP |
| 16 | Show statute text alongside comparison | `comparador.php:319,349` | CONFIRMED-MVP |
| 17 | Filter comparison by sector / type / state | `comparador.php:214-222` | CONFIRMED-MVP |
| 18 | Economic-comparison flag per heading category | `categorias_titulos.requiere_campo_comparacion_economica` | CONFIRMED-MVP (**undocumented rule**) |
| 19 | Comparison PDF export | `comparacion/generar_pdf.php:114` | CONFIRMED-DEFERRED (unreachable today) |
| 20 | Legal library (statute + articles + PDF) | `leyes/settings.php` | CONFIRMED-DEFERRED (starts empty) |
| 21 | Master data catalogues (13) | `tablas/settings.php:33` | CONFIRMED-MVP |
| 22 | Company registry with logo | `lib/objetos/empresas.php:360` | CONFIRMED-MVP (dual-key defect) |
| 23 | News + publish flag (public portal) | `lib/objetos/noticias.php:174` | CONFIRMED-MVP |
| 24 | News categories | `noticias.php:306` | CONFIRMED-MVP (malformed SQL; redesign) |
| 25 | Links of interest | `lib/objetos/links_interes.php:92` | **UNCERTAIN** — no stated requirement |
| 26 | Public portal (news, published agreements) | `index.html`, `tablas/noticiero/` | CONFIRMED-MVP (portal itself is static legacy HTML) |
| 27 | Login audit trail | `user.php:177`, `settings.php:46` | OBSOLETE (table absent) |
| 28 | Bulletins (`Boletin`) | `tablas/noticiero/` | OBSOLETE (unreachable) |
| 29 | Documents repository | `tablas/documentos/` | OBSOLETE (unreachable) |
| 30 | Pass-to-contracts flag | `estatu_pase_contratos` | OBSOLETE (never written) |
| 31 | Clause number normalisation (`mascara`) | `discusion.php:1141` | **UNCERTAIN** — intent only, broken |
| 32 | Static header login box | `plantillas/plantilla_admin/cabecera.php:92` | OBSOLETE |
| 33 | Role-filtered menu (`menu.php`) | `menu/menu.php:25` | OBSOLETE (superseded by static `menu2.php`) |
| 34 | Catalog cascade delete (country → states → municipalities) | `eliminarpais.php:10-12` | CONFIRMED-MVP (must become a real constraint) |

---

## 8. Explicitly not found

Stated so their absence is not mistaken for an oversight:

- **No transactions, anywhere.** MyISAM plus a driver whose `commit`/`rollback` emit no SQL.
- **No audit trail.** No created/updated metadata on any table.
- **No scheduled processes.** No cron entry point, no job table, no queue.
- **No email, notification, or external integration** of any kind.
- **No soft deletes.** Every delete is a hard `DELETE`.
- **No prepared statements, no ORM, no migrations.** Schema lives only in `presenci_cccol.sql`.
- **No test suite, no seed fixtures, no build script, no CI.**
- **No API.** Server-rendered pages only; no JSON endpoint exists.
- **No i18n.** Spanish strings are inline in PHP and HTML.
- **No uniqueness enforcement** on `usuarios.Login_usuario` or any catalog name.
