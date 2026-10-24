# Functional flows — legacy PHP application

End-to-end journeys traced through code. Each flow states its entry point, the data changes at
each step, where it terminates, and how it fails.

Flows are described as the legacy system *intends* them, with the breakages marked inline. Because
the application cannot be executed ([security-findings.md](security-findings.md) §S15), no flow
below was observed at runtime; every step is code-derived.

Routing background: everything enters through `admin/index.php?url=…&tbl=…&accion=…`, which
includes `modulos/settings.php` (a whitelist over `url`), which sets `$url` and `$titulo`, includes
the module's `index.php`, which reads `tbl`/`accion`, includes the module's `settings.php`, which
resolves an action controller and a view. Controllers are included **before** the view renders, so
a POST mutates data and then re-renders the same screen.

---

## F1 — Authentication and session

**Entry:** `admin/index.php` (line 2 `session_start()`).

**Intended flow**
1. No session → render the login module (`login/index.php`), whose form posts `submit=1`,
   `login`, `pass`.
2. `lib/funciones/php/login.php:10-15` reads `$_REQUEST['submit']`, `['login']`, `['pass']`.
3. `lib/objetos/user.php:154 login($user,$password)` runs
   `Select * from usuarios WHERE Login_usuario LIKE '<user>' AND Clave_usuario LIKE '<password>'`.
4. On a single match, `assignRecord()` + `assignUserSession()` set
   `$_SESSION['login'|'nombre'|'tipo'|'fecha_vence']`, then redirect to `?url=principal`.
5. `login.php:18` refuses if `estatus == 'BLO'` (blocked account).

**As implemented**
- Step 1 never happens: `admin/index.php:20` hardcodes `$nombreses="Alex"`, so the
  `if($nombreses!="")` test at line 21 is always true and the login branch is unreachable dead
  code. Every request is treated as authenticated.
- The header login box (`plantillas/plantilla_admin/cabecera.php:92-107`) posts `login2`/`pass2`,
  but the handler reads `login`/`pass`; the adapter `levantar_sesion2()` (line 19-26) is defined
  and never called. Also dead.
- Passwords are plaintext and compared with `LIKE`, so `%`/`%` authenticates as any user
  (§S3). `admin/index.php:31` additionally hardcodes `$t_usuario=1` (Master).

**Data changed:** `$SESSION` only. The intended audit insert into `usuarios_login`
(`user.php:177`, `modulos/settings.php:46`) always fails — the table does not exist.

**Exit states:** authenticated-looking shell, always.

**Failure modes:** no lockout, no rate limit, no password policy, no session regeneration.

---

## F2 — Author an agreement: create → clauses → annexes → publish

**Entry:** `?url=contratos&tbl=Contratos&accion=listar` → "Agregar Registro" (`accion=agregar`).

### F2.1 Create the agreement
1. `contratos_contratos_agregar.php` renders a form posting to `action=""`.
2. `lib/funciones/php/contratos/contratos/agregarcontrato.php` reads the posted fields and calls
   `Contratos::insertarContrato($pdf, $inicio, $termino, $duracion, $ambito, $empresa)`.
3. Insert targets `contratos` (8 columns). `status_publicacion` is **not** supplied, so the row
   takes the schema default `'PE'`.

**Fields an agreement actually has:** validity start, validity end, duration (free text,
`varchar(15)`), application scope (`ambito_aplicacion`), company, attached act PDF. **No title, no
number, no agreement-level status beyond publish/pending** ([business-rules.md](business-rules.md) §BR-01).

**The one gate:** `agregarcontratos.php:26` refuses to insert when no PDF was attached
(`if ($pdf_asociado=="")`), and the upload itself is gated on a client-MIME check that fails
silently. Otherwise validation is **none** — not even that `fecha_de_inicio <= fecha_de_termino`,
and the `dd-mm-yyyy` string captured by the date picker is written **unparsed** into a MySQL `date`
column. The dump shows the result: **18 occurrences of `'0000-00-00'` among the 73 agreement rows**.

### F2.2 Add clauses
1. `accion=listar_articulos` → `contratos_articulo_contratos_listar.php` (paged, searchable by
   `titulo_articulo`).
2. `accion=agregar_articulos` → form with: article number (free text), title, summary, full text
   (tinyMCE rich editor), comparison heading (`titulos` dropdown), economic comparison field.
3. `Contratos::agregarArticuloContrato` (`contratos.php:928`) inserts into `articulos_contratos`
   **omitting** `status_articulo` and `articulo_cerrado`, so each clause inherits
   `status_articulo='Pendiente'` and `articulo_cerrado=NULL` from the schema.
4. Listing orders by `nro_articulos ASC` — a `varchar`, so lexicographically (§BR-02).

**Consequence of step 3:** every clause is permanently "Pendiente" unless edited by hand, and
nothing in the application ever reads `status_articulo` or `articulo_cerrado` to filter or gate
anything. They are inert columns.

### F2.3 Attach annexes
`accion=agregar_anexos` → `Contratos::insertarAnexo` writes `anexos_contratos` including
`texto_completo_anexo` and an uploaded `pdf_anexo` into
`contratos/documentos/<codigo_contrato>/`. The annex carries its own
`status_publicacion varchar(15) default 'Pendiente'` — a **different vocabulary** from the
agreement's `char(2)` `PE`/`PU`, and nothing ever filters on it (§BR-07).

### F2.4 Publish
1. The list renders a checkbox per agreement, `checked` when `status_publicacion=='PU'`
   (`contratos_contratos_listar.php:114`), with `onclick="publicar(<id>)"`.
2. `publicar()` requests `lib/funciones/php/contratos/contratos/publicar.php?id=<id>`, which reads
   the current value and flips it: `'Pe' → 'PU'`, anything else → `'Pe'`.
3. Because the default is the uppercase `'PE'` and the comparison is `=="Pe"`, **the first click
   writes `'Pe'` (still unpublished) and only the second click publishes** (§BR-06). A `'Pe'` row
   matches none of the `='PU'` predicates anywhere.

**Side note:** `accion=publicar_contrato` is a *different* screen
(`contratos_publicar.php`) — a read-only summary titled "Publicar Contrato" with no publish logic
at all. The naming is misleading; the real toggle is the list checkbox.

### F2.5 Print clauses
`accion=impresion_articulos` / `impresion_articulos_total` render printable clause views for the
browser's print dialog. The combined document is built by concatenating clause text into a pop-up
and `document.write`-ing it — **no PDF is produced** (the only FPDF print path lives in an
unreachable `*-old.php` file). Printing is therefore browser-side and lossy.

**One real rule hides here:** `contratos_articulo_contratos_impresion_clausulas.php:22` skips any
clause whose `articulo_cerrado == 1`, so flagged clauses are **excluded from the compiled
document**. This is the only per-clause behavior in the entire system
([business-rules.md](business-rules.md) §BR-07C).

**Exit state:** agreement exists, clauses exist, publication pending-or-published depending on how
many times the checkbox was clicked.

---

## F3 — Negotiation lifecycle

The highest-value flow in the system. Entry: `?url=contratos&tbl=Bitacora`.

### F3.1 Open a case
1. `accion=agregar` → `discusion_contratos_agregar.php`.
2. Form captures: company, start date, and three representative blocks (company, union, Labour
   Ministry) each with name/phone/email/position, plus **two PDF uploads** (union petitions,
   employer offers).
3. `Discusion::insertarDiscusion(...)` (`discusion.php:467`) inserts into `bitacoras`
   **without** `estatus_bitacora`, so the row takes `DEFAULT ''` → rendered as "Pendiente".
4. `agregar_discusion.php:26` overwrites the petition/offer text fields with
   `$_FILES[...]['name']`, and line 46 gates the whole save on `if($ext!==""||$ext2!=="")` — so
   **the case is only created if both PDFs are attached with a pdf/jpeg/png MIME claim**.

### F3.2 Record petitions
`accion=listar_peticiones` → `agregar_peticiones`. `Discusion::insertarPeticionDiscusion` writes
`peticiones_sindicato` (number, title, full text, status `''`). Number padding to three digits is
copy-pasted into this handler and works (`agregar_peticiones_discusion.php:11-43`), unlike the
shared `mascara()` helper which is broken (§BR-03).

### F3.3 Record offers
Same shape against `ofertas_patrono`, linked to a petition via `codigo_peticion`. Offers are the
employer's response to a specific petition.

### F3.4 Record a meeting — and resolve items in one shot
`accion=listar_reuniones` → "abrir ventana" →
`discusion_contratos_agregar_reuniones_externa.php`, a pop-up that performs a **bulk
transaction-like** operation in one submit (`:48-127`):

1. Insert the `reuniones` row (date, time, duration, summary, details, attendees).
2. For each checked petition: after `if ($llave_ofertas==1)`, insert an **offer** (`:90`), insert a
   `reuniones_ofertas` row (`:101`) and a `reuniones_peticiones` row (`:102`).
3. If the "Existe Acuerdo" checkbox is on for that item
   (`if($aprobador_confirmacion[$x]=='on')`), insert an **agreement** (`:109`), a
   `reuniones_acuerdos` row (`:118`), and set `estatus_peticion = 9` via
   `update_Ofertas_Acuerdos` (`:119`).

**This is the only writer of the three join tables**, and it bypasses the DB abstraction entirely:
raw `mysql_query` on a **second hardcoded connection** (`discusion.php:1073-1129`).

**Placeholder pollution:** `insertar_Acuerdos_Reuniones` (`discusion.php:1108`) writes
`'Campo Comparativo Pendiente'`, `'Resumen Pentiende'` and hardcodes the comparison heading to `0`
from the caller (`:109`). The dirty `acuerdos` row in the dump is **produced by this code**, not
typed by a user ([business-rules.md](business-rules.md) §BR-14).

### F3.5 Close the case and promote to an agreement
1. `accion=cerrar&id=<bitacora>` → `discusion_contratos_cerrar.php`, a read-only review form.
   The submit button is rendered **only** if the case has at least one agreement
   (`:38-41`, `:210-215`) — the module's only business gate.
2. POST `enviar=1` → `cerrar_discusion.php`:
   - `insertarContrato("", "*Inicio", "*Fin", "*Duración", "*Ambito", $empresa)` — creates a new
     agreement with **literal placeholder strings** in two `date` columns.
   - `obtenerIdUltimoContrato()` = `order by codigo_contrato desc limit 0,1` — **not**
     `LAST_INSERT_ID()`.
   - Copy every `acuerdos` row into `articulos_contratos` (one insert per agreement).
   - `actualizarDiscusiones_status($id, 10)` → closed.
   - `mkdir(".../documentos/<new_contract_id>", 0777)`.
   - Redirect to the new agreement's clause list.

**Three defects with data-loss potential:** (a) if the agreement insert fails under strict
`sql_mode` — likely, because `'*Inicio'` is not a valid `date` — the result is unchecked and
`obtenerIdUltimoContrato()` returns an **existing** agreement, so negotiated clauses are appended
to the wrong contract; (b) copying is not idempotent, so reopening and closing again duplicates
every clause into a second agreement; (c) no transaction spans the five writes, and MyISAM cannot
provide one.

### F3.6 Reopen
- Whole case: `accion=reabrir&id=` → `reabrir_discusion.php:7` sets `estatus_bitacora=1`. No
  confirmation, no guard that an agreement was already generated.
- Single petition: only when `estatus_peticion==9`, the list offers "RA" →
  `reabrir_peticiones.php:8` resets `estatus_peticion=''`, unlocking that petition without
  touching the agreement already copied out of it.

### F3.7 History and printing
- `discusion_contratos_historico.php` (pop-up) renders, per meeting, the petitions, offers and
  agreements discussed. It queries raw via `mysql_query` and is heavily N+1 (three nested levels).
  It **prints petition text where the offer belongs** (`historico.php:233`), so it is not a
  reliable specification.
- `accion=impresion_gen` opens a blank window whose content is a textarea pre-filled from the DB,
  then `document.write`s whatever is posted back. No PDF.

**Closed-state enforcement:** none. §BR-10 — a closed case's petitions, offers, meetings and
agreements all remain editable and deletable by direct URL; the list merely hides the buttons.

---

## F4 — Compare clauses across agreements

**Entry:** the sidebar menu opens `admin/modulos/contratos/comparacion/comparador.php` in a pop-up
window (a **standalone script**, not routed through the dispatcher).

1. Three filters (sector, company type, state) and a comparison-heading selector.
   The `onchange="Recargar()"` auto-submit handlers are inside an HTML comment
   (`comparador.php:52-56`), so nothing auto-submits — the user must press "Buscar".
2. With a heading chosen, the page lists **candidate agreements**: those having at least one clause
   under that heading, filtered to `status_publicacion='PU'`, optionally narrowed by sector/type/
   state (`:238-242`). Each candidate is a checkbox.
3. Submitting with selections (`count($fields)` unchecked against `is_array`, a PHP 8 fatal)
   renders the comparison grid: one leading column with the labour statute and other statutes for
   that heading, then **one column per selected agreement** listing all its clauses for the
   heading, ordered by article number.

**What comparison is not:** there is no clause-to-clause alignment, no diff, no gap detection, no
scoring. Adjacency only ([business-rules.md](business-rules.md) §BR-15). This is the single most
important expectation to confirm before designing (Q-6).

**PDF export:** `generar_pdf.php` uses HTML2FPDF and would produce `reporte.pdf`, but it is
reachable only from `comparacion1.php`, an orphaned xajax shell with **zero inbound references**
(and whose heading filter handler `generarListaTitulo()` does not exist). Its ids are packed into a
space-separated string with a trailing separator, so the query can end up as
`WHERE codigo_contrato =` — a SQL error. **The PDF export is effectively dead.**

**No writes occur in this whole flow.**

---

## F5 — Legal library

**Entry:** `?url=leyes&tbl=LOT` or `tbl=Otras Varias`.

**Labour statute (single row).** `ley_trabajo` has **no primary key** and the loader requires
`realcount()==1`, so a second row silently breaks the module. Flow: list articles → add/edit/delete
→ "Editar Datos LOTTT" edits the one statute row and replaces its PDF. Counter
`total_articulos` is maintained on add/delete only and never recomputed (§BR-26).

**Other statutes.** List → add statute (PDF is mandatory: `agregar_otras_leyes.php:28` alerts
"Debe Asociar un PDF al Contrato" and refuses) → creates `documentos/<codigo>/` → article CRUD per
statute. **The article list screen is a PHP parse error** — the file is truncated at 1,460 bytes
mid-expression — so article management for other statutes is reachable only by hand-built URLs.

**Broken routes:** default and `accion=imprimir` targets do not exist (§2 of the inventory).

**Product Owner decision:** this whole module is **in scope as a feature but starts empty**; the
legacy statutes, articles and gazette PDFs are not carried over. This flow is therefore recorded
for *shape*, not for content.

---

## F6 — Manage master data

**Entry:** `?url=tablas&tbl=<Menu label>&accion=listar|agregar|editar|eliminar|ver`.

Uniform CRUD over 13 entities (see inventory §3.6). Each action: list (paged, `LIKE` filter) →
"Agregar Registro" → form → POST → controller → object → redirect back to the list. Delete is a
two-step confirm page whose second step is a GET with `flag=SI`.

**Deviations from the uniform pattern:**

- **Companies** — 23 fields plus a logo upload. The insert (`empresas.php:360`) omits
  `codigo_empresa`, the very column every read/update/delete filters on, so a created company is
  keyed `0` and effectively unreachable ([business-rules.md](business-rules.md) §BR-23). The
  insert is also gated on an uploaded file (`agregarempresa.php:40`), so a company **cannot be
  created without attaching a logo**.
- **Comparison headings** — two-level taxonomy (block → heading) with the economic-comparison flag.
  The block editor's object (`categoria_titulos.php`) is included by `settings.php` but **no case
  selects it**, so the block screen may be unreachable (agents disagree; flagged uncertain).
- **News** — create always writes `'Pendiente'` regardless of the dropdown, so publishing is
  create-then-edit (§BR-25).
- **News categories** — the insert builds malformed SQL (`VALUES ( 'x', '.".$y."' )`), so the
  entity is likely non-functional.
- **Country delete** cascades to states and municipalities in three untransacted statements
  (§BR-22).

**Two screens are unreachable** for lack of a `modulos` row: `noticiero/` ("Boletin") and
`documentos/`.

---

## F7 — Publish news to the public portal

1. Admin creates a news item in `tablas/noticias` — status forced to `'Pendiente'`.
2. Admin edits it and sets status `'Publicar'` (`noticias.php:78`).
3. The public front list (`tablas/noticiero/tbl_noticias_front.php`, itself unreachable by menu —
   no `modulos` row) selects `estatus_noticia='Publicar'` and pages the results; a row opens
   `tbl_noticias_front_ver.php?id=`.

The portal the product actually shipped is the **static** `index.html` at the repository root,
which is unrelated to these screens. So "public portal" exists twice: a static legacy HTML site
and an unreachable dynamic news front-end. §BR-25 records the state model; the *delivery*
mechanism is a design decision for the replacement (Q-14).

---

## F8 — Administer users and permissions

**Entry:** `?url=seguridad&tbl=<Usuarios|Tipos de Usuario|Pantallas|Seguridades>`.

1. **Users** — CRUD over `usuarios`; required fields enforced client-side
   (`agregarusuario.php:18`): name, login, password, type, status, expiry, and company unless the
   type is Master. No uniqueness check on `Login_usuario`, so duplicates are possible and
   `login()` (which requires `realcount()==1`) then fails for **both** accounts.
2. **User types** — CRUD over `tipos_de_usuarios`.
3. **Screens** — CRUD over `pantallas` (the catalogue of page filenames).
4. **Grants** — `seguridades` maps (type, screen). **Only the list screen exists**; add/edit/delete
   routes are dangling (inventory §2). So grants can be inspected but **not administered** through
   the UI — they were evidently maintained by direct SQL.

5. **Self-service** — `?url=perfil`: edit own profile; change own password with no
   current-password verification.

**Account expiry** is applied on the welcome page only, and is dead in practice
([business-rules.md](business-rules.md) §BR-21).

**What the user actually experiences:** the shell renders `admin/menu2.php`, a **static** menu that
lists every module unconditionally with no role filtering. The role-aware menu
(`modulos/menu/menu.php`) exists but is only included by the welcome and profile pages. So even
the *appearance* of role separation is absent from the main navigation.

---

## Flow interaction map

```
F1 login ──► F2 author agreement ──► clauses ──► publish ──┐
                                                     │      │
F3 negotiation ──► close/promote ────────────────────┴──────┴──► F4 comparison
   ├ petitions                                                 (reads published agreements
   ├ offers                                                    + statute articles)
   ├ meetings ──► agreements
   └ reopen

F5 legal library ─────────────────────────────────────────────► F4 comparison
F6 master data ──► companies/sectors ─────────────────────────► F4 filters
                └ news ──► F7 public portal
F8 users/roles ──► gates everything (in theory; broken in practice)
```

The critical dependency: **F4 comparison consumes the output of both F2 (agreements) and F5
(statutes), and is scoped by F6 (company sector/type/region).** If any of those three is
incomplete in the MVP, comparison cannot demonstrate value. This ordering should drive the
implementation sequence.
