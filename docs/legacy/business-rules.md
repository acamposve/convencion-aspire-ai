# Business rules — legacy PHP application

Rules extracted from code, with the evidence that establishes each one and a verdict on what the
replacement should do with it.

Each rule carries a **Confidence** marker:

- **Evidenced** — the code implements it consistently and the data corroborates it.
- **Evidenced, broken** — the intent is clear but the implementation does not deliver it.
- **Inferred** — only derivable from a fragment (a dead branch, a dropdown, a data shape). Treat
  as a hypothesis requiring Product Owner confirmation before it becomes a requirement.

Rules marked **Ambiguous** are collected as questions in [open-questions.md](open-questions.md).

---

## A. Agreements and clauses

### BR-01 — An agreement is identified by company, validity period and scope; it has no title
**Confidence: Evidenced.**

The `contratos` table has no name, code or number column: `codigo_contrato, pdf_auto_acta,
fecha_de_inicio, fecha_de_termino, duracion, ambito_aplicacion, codigo_empresa,
status_publicacion`. The list screen renders company + dates only.

*Consequence for the redesign:* if agreements need to be distinguishable — and comparison across
several agreements by the same company makes that near-certain — a title or number is a **new**
requirement, not a preserved one. Ask (Q-1).

### BR-02 — Clauses are ordered by their article number, ascending
**Confidence: Evidenced, broken.**

`lib/objetos/contratos.php:711`:
```sql
... WHERE codigo_contratos = <id> ORDER BY nro_articulos ASC
```
`nro_articulos` is `varchar(11)`, so this is a **lexicographic** sort: 1, 10, 100, 11, 2. The
intent is numeric ordering; the implementation cannot produce it. The same defect exists for
statute articles (`ORDER BY nro_articulo ASC`) and for petition/offer numbers.

*Verdict:* keep the rule (clauses are ordered by article number); fix the type. Clause numbers
must sort numerically, including the `NNN-N` composite form (BR-03).

### BR-03 — Article/petition/offer numbers are three-digit zero-padded, optionally with a suffix
**Confidence: Inferred from a broken implementation.**

`lib/objetos/discusion.php:1141` `mascara($nro)` intends `"5" → "005"` and `"12-3" → "012-3"`.
It fails on three counts: it reads an undefined variable (`$nroOferta` instead of `$nro`), uses
`split()` (removed in PHP 7), and `switch($cadena)` switches on an **array**, so every case
compares an array to a boolean. Padding never executes.

The same padding block is copy-pasted into four handlers
(`agregar_peticiones_discusion.php:11-43` and siblings) where it *does* work.

The dump corroborates the intent: `articulos_contratos.nro_articulos` holds values like `'004'`,
and the `titulos`-era data shows `'01'`, `'01-A'` conventions.

*Verdict:* the *format* is real (`NNN` or `NNN-N`); the shared helper is the fix. Ask (Q-2)
whether the suffix suffix is a sub-clause (e.g. `012-3` = clause 12, item 3).

### BR-04 — Every clause is classified under exactly one comparison heading
**Confidence: Evidenced.**

`articulos_contratos.codigo_titulo_comparativo` is set from a `titulos` dropdown in
`contratos_articulo_contratos_agregar.php:73-80`. The heading drives the entire comparison
feature: `contratos.php:861` joins clauses to headings, and `comparador.php:241` selects
candidate agreements by heading.

Headings are two-level: `categorias_titulos` (block) → `titulos` (heading) →
clauses. 64 headings across 5 category blocks in the dump.

*Verdict:* keep. This is the load-bearing concept of the whole product.

### BR-05 — A heading category may require an economic-comparison value on its clauses
**Confidence: Evidenced (undocumented).**

`categorias_titulos.requiere_campo_comparacion_economica char(2)`. Written by
`lib/objetos/categoria_titulos.php:230`, surfaced in `tablas/categoria/tbl_categoria_list.php:91`.
When set, the clause's `campo_comparativo varchar(250)` is expected to carry a comparable
economic figure (amount/percentage) so clauses of different agreements can be lined up.

**Nothing enforces it.** The clause add/edit handlers have no required-field check
(`agregar_articulo_contratos.php:6-45`), so the flag is advisory metadata only, and
`campo_comparativo` is never read by any comparison code.

*Verdict:* the concept is real and important — it is the only structured comparison dimension in
the system. Its semantics are ambiguous: is the value free text, a number, or a formula? Ask
(Q-3). Without an answer this cannot be modelled.

### BR-06 — An agreement is either pending (`PE`) or published (`PU`); publication gates visibility
**Confidence: Evidenced, with a defect.**

Reads: only `status_publicacion='PU'` appears in the comparison candidate query
(`comparador.php:241`) and in contract counts (`contratos.php:269,518`). The list shows a checkbox
checked when `=='PU'` (`contratos_contratos_listar.php:114`).

Write: `lib/funciones/php/contratos/contratos/publicar.php:12`:
```php
if ($status_publicacion=="Pe"){ ... set "PU" } else { ... set "Pe" }
```
The schema default is **`PE`** (uppercase) and the intended pending value is **`Pe`** (mixed case).
PHP string comparison is case-sensitive, so a freshly created agreement holds `PE`, fails the
`=="Pe"` test, and gets written to `Pe` — i.e. **the first click does nothing; publishing takes
two clicks.** Every `'PU'`/`'PE'` predicate elsewhere uses uppercase, so a `Pe` row is invisible to
them.

*Verdict:* keep the two-state model (pending/published) and the rule that unpublished agreements
do not appear in comparison or public views. The case bug must not be reproduced — the state
belongs in a typed enum with a database `CHECK`, per Art. V of the constitution.

### BR-07 — Annexes belong to an agreement and carry their own publication status
**Confidence: Evidenced.**

`anexos_contratos(codigo_contrato, nombre_anexo, descripcion_anexo, texto_completo_anexo,
pdf_anexo, status_publicacion varchar(15) default 'Pendiente')`. Note this column is a free
`varchar(15)` holding `'Pendiente'`, not the `char(2)` `PE`/`PU` used by agreements — **two
different state vocabularies for the same concept in adjacent tables.** Nothing filters on the
annex value.

*Verdict:* keep the entity; unify the state vocabulary (Q-7).

### BR-07A — Creating an agreement requires an attached act PDF
**Confidence: Evidenced (as a gate, not as a validation).**

`lib/funciones/php/contratos/contratos/agregarcontratos.php:26`:
```php
if ($pdf_asociado=="") { alert("Debe Asociar un PDF al Contrato"); } else { ...insert... }
```
The insert is skipped when no file is supplied. The check is a JS `alert()` in a controller, so it
fires after submission and the user loses the form; and the upload branch is itself gated on
`if($ext!="")` where `$ext` comes only from the client's MIME claim, so an unrecognised MIME type
is a **silent no-op with no message**.

The same shape governs negotiation cases (§F3.1), where *both* PDFs are required.

*Verdict:* "an agreement has an act document" is plausible as a real requirement — the PDF is what
the public portal links to. Making it mandatory is a business decision (Q-12).

### BR-07B — `status_articulo` is a dead column; no clause ever leaves "Pendiente"
**Confidence: Evidenced.**

The column exists (`varchar(12) DEFAULT 'Pendiente'`) and is **read in 26 places, written in
zero**. No INSERT or UPDATE anywhere in the codebase sets it: `agregarArticuloContrato`
(`contratos.php:928`) omits it, and no edit handler supplies it. Every clause therefore holds
`'Pendiente'` for its entire life.

Its apparent purpose — an editorial workflow (draft → reviewed → approved) before a clause is
published — **does not exist**. Either the workflow was dropped late in development, or it was
never built and the column is aspirational.

*Verdict:* do not model a clause-level editorial workflow unless the Product Owner asks for one.
Record as a question only if clause review turns out to matter. Currently: **OBSOLETE**.

### BR-07C — A clause flagged `articulo_cerrado` is excluded from the compiled printed document
**Confidence: Evidenced. This is the only real per-clause behavior in the system.**

`admin/modulos/contratos/contratos/contratos_articulo_contratos_impresion_clausulas.php:22`:
```php
if ($articulo_cerrado != 1) { ...append this clause to the combined document... }
```
`articulo_cerrado char(1) NULL` is the **only** clause-level flag that changes behavior anywhere.
It is set by the clause edit form and read at exactly this one place.

Interpretation: some clauses are marked "closed"/finalised and are therefore **omitted** when the
full agreement text is printed as one document — presumably because they are superseded, reserved,
or not part of the published compilation.

*Verdict:* this is a genuine, undocumented business rule living in a print filter, and it must be
confirmed (Q-19). If clauses are excluded from the published compilation, that is exactly the kind
of rule the replacement must model explicitly — a boolean on the clause, applied wherever the
compiled document is produced — rather than at print time only.

### BR-07D — Clause numbers are zero-padded by the clause form, and duplicate numbers are not checked
**Confidence: Evidenced.**

`lib/funciones/php/contratos/contratos/agregar_articulo_contratos.php:14-43` zero-pads to three
characters and preserves a `-` suffix (`5` → `005`, `12-3` → `012-3`) — the same ladder that is
copy-pasted (and there, working) in the petition handler of §BR-03.

**No duplicate-number check exists for agreement clauses**, unlike statute articles (§BR-27). Two
clauses numbered `005` can coexist in one agreement, and the list will show both.

*Verdict:* keep the format rule (BR-03), enforce uniqueness **inside an agreement** — which the
legacy system does not do.

---

## B. Negotiation

### BR-08 — A negotiation case belongs to a company and carries representatives of three parties
**Confidence: Evidenced.**

`bitacoras` holds `codigo_empresa`, `fecha_inicio_disc`, and a representative block for each of:
the company, the union (*sindicato*), and the Labour Ministry (*ministerio del trabajo*) — each
with name, phone, email and position. Plus `fecha_ultima_reunion`, `fecha_proxima_reunion` and a
PDF for petitions and one for offers.

This is the domain's political structure: a collective agreement in Venezuela is negotiated
tripartitely. It should be modelled explicitly, not as a flat 21-column row.

### BR-09 — A case has exactly three states: pending (`''`), open (`1`), closed (`10`)
**Confidence: Evidenced for `1` and `10`; Ambiguous for `''`.**

`estatus_bitacora` is `char(3)`. It is written in exactly one statement —
`lib/objetos/discusion.php:399` `Update bitacoras set estatus_bitacora = %d where
codigo_bitacora = %d` — with exactly two callers:

| Value | Label shown (`discusion_contratos_listar.php`) | Written by |
|---|---|---|
| `''` | "Pendiente" (line 115-117) | **never written**; column `DEFAULT ''` |
| `1` | "Modificación" (line 112-113) | `reabrir_discusion.php:7` |
| `10` | "Cerrado" (line 109-111) | `cerrar_discusion.php:46` |

So `1` means *opened/reopened* and `10` means *closed* — numeric codes stored in a character
column. `''` is the initial state only because the insert
(`discusion.php:467 insertarDiscusion`) omits the column.

*Ambiguity worth flagging:* the UI labels `1` as "Modificación", which reads as "under
modification", while the only writer is **reopen**. Whether `1` means "open" or
"reopened-for-editing" is not distinguishable, and it matters for the state machine. Ask (Q-4).

### BR-10 — A closed case is read-only, and closing requires at least one agreement
**Confidence: Evidenced (UI only, not enforced).**

The only business gate in the module, `discusion_contratos_cerrar.php:38-41`:
```php
if ($conteo==0){ $avaluo="No Existen Acuerdos en este Contrato"; }
```
and the submit button is omitted when `$avaluo != ""` (lines 210-215). So a case cannot be closed
via the UI without agreements.

**It is not enforced anywhere server-side.** `cerrar_discusion.php` accepts a POST with `enviar=1`
directly. Worse, the closed state does not actually make a case read-only: the edit/delete
handlers for petitions, offers, meetings and agreements check nothing, so a closed case's
children remain fully mutable by URL. The list screen merely hides the buttons
(`discusion_contratos_listar.php:134` `if ($estatus_bitacora!=10)`).

*Verdict:* both rules are real intent. Both must be enforced as invariants in the replacement.

### BR-11 — Closing a case promotes its agreements into agreement clauses
**Confidence: Evidenced, with several defects.**

`discusion/cerrar_discusion.php:28-51` performs, in order:

1. `insertarContrato("", "*Inicio", "*Fin", "*Duración", "*Ambito", $cod_empresa)` — creates an
   agreement with **literal placeholder strings** in `fecha_de_inicio` and `fecha_de_termino`,
   which are `date` columns. Under strict `sql_mode` this insert fails silently (result
   unchecked); under non-strict it yields `0000-00-00`. This is the origin of the zero dates
   throughout the dump.
2. `obtenerIdUltimoContrato()` = `select * from contratos order by codigo_contrato desc limit 0,1`
   — **not** `LAST_INSERT_ID()`. A concurrent insert makes it return someone else's agreement,
   and a failed insert makes it return a pre-existing one, so clauses can be appended to the
   **wrong agreement**.
3. One `agregarArticuloContrato()` per agreement, copying each `acuerdos` row into
   `articulos_contratos`.
4. `actualizarDiscusiones_status($id, 10)`.
5. `mkdir(".../documentos/".$codigo_contrato, 0777)`.

The `acuerdos` rows **survive** — they are copied, not moved. There is **no idempotency guard**:
reopen and close again creates a second agreement and duplicates every clause into it.

*Verdict:* the workflow rule is the heart of the product (a successful negotiation becomes a
contract) and must be preserved. The implementation is unsafe and must be transactional and
idempotent.

### BR-12 — A petition that produced an agreement is marked with status `9`
**Confidence: Evidenced.**

`discusion.php:1126-1133` `update_Ofertas_Acuerdos($codigo_peticion)` sets
`estatus_peticion = 9`; `discusion.php:1185` resets it to `''` on "reopen petition"
(`reabrir_peticiones.php:8`). The UI exposes that reopen only when `estatus_peticion==9`
(`discusion_contratos_listar_peticiones.php:76`).

Values are only `''` and `9`; the column is `char(1)` and collated `latin1_bin`. The UI labels it
"RA" (re-abrir).

*Verdict:* the concept — "this petition is agreed and therefore locked" — is real. Whether `9`
carries meaning beyond truthiness is **unknown** (no other value is ever written). Model it as a
boolean/enum, not as a magic number. Ask (Q-5).

### BR-13 — Meetings are linked to the petitions, offers and agreements discussed in them
**Confidence: Evidenced.**

Three join tables: `reuniones_peticiones`, `reuniones_ofertas`, `reuniones_acuerdos`, each
`(codigo_reuniones, codigo_bitacora, codigo_<child>)`. They are written in one screen
(`discusion_contratos_agregar_reuniones_externa.php:101-118`) and read only by the history view
(`discusion_contratos_historico.php:166,207,249`).

Note the join column is `codigo_reuniones` (plural) while `reuniones`' key is
`codigo_reunion` (singular) — so no foreign key is even expressible.

*Verdict:* keep — this is the only record of *which* items a meeting resolved. Note that the
history screen prints the petition text where the offer belongs
(`historico.php:233`), so the legacy display is not a trustworthy spec.

### BR-14 — A meeting can generate agreements, and agreement creation stamps placeholder text
**Confidence: Evidenced.**

`discusion.php:1108 insertar_Acuerdos_Reuniones` inserts with hardcoded values
`'Campo Comparativo Pendiente'`, `'Resumen Pentiende'` and omits `estatus_acuerdo`. The dump's
single `acuerdos` row shows exactly these placeholders (`'Resumen Pentiende '`) and an empty
`texto_completo_articulo`.

So the junk data in the dump is **generated by code**, not typed by users, and the caller also
hardcodes `codigo_titulo_comparativo = 0` (`..._reuniones_externa.php:109`), which fails BR-04.

*Verdict:* the rule (agreement inherits its meeting's context) is real; the placeholder seeding
is a defect to drop. Also note `estatus_acuerdo` is `char(1)` and never advanced past `''`/`'0'` —
agreements have **no meaningful lifecycle** in the legacy system.

---

## C. Comparison

### BR-15 — Comparison groups clauses by heading across published agreements, side by side
**Confidence: Evidenced.**

`comparador.php:238-242` selects candidate agreements that have at least one clause under the
chosen heading and are published:
```sql
SELECT distinct(nombre_empresa), codigo_contrato, contratos.status_publicacion
FROM articulos_contratos
  INNER JOIN contratos ON contratos.codigo_contrato = articulos_contratos.codigo_contratos
  INNER JOIN empresas  ON empresas.codigo_empresa   = contratos.codigo_empresa
WHERE articulos_contratos.codigo_titulo_comparativo = '<heading>'
  AND <sector/type/state filters>
  AND contratos.status_publicacion = 'PU'
ORDER BY nombre_empresa
```
The user ticks agreements; the page renders one column per agreement, each listing **all** of its
clauses for that heading, plus the labour statute and other statutes for that heading in a
leading column.

**There is no clause-level alignment.** Each column streams its own clauses sorted by
`nro_articulos`; nothing pairs clause 5 of agreement A with clause 5 of agreement B. There is no
diff, no matching, no "missing clause" marker. "Comparison" means *adjacency*, not *diff*.

*Verdict:* this is the single most important scope question in the project. If the Product Owner
expects true clause-to-clause comparison (alignment, differences, coverage gaps), that is a
**new capability**, not a port. Ask (Q-6).

### BR-16 — The comparison detail query does not re-apply the publication filter
**Confidence: Evidenced.**

The candidate list filters `status_publicacion='PU'` (line 241), but the per-column detail query
(`comparador.php:413-416`) filters only by heading and contract id — no `PU` condition. The
filter therefore holds only because the user can select solely from the pre-filtered list; a
crafted `fields[]` request returns clauses from unpublished agreements.

*Verdict:* the *rule* is "only published agreements are comparable" (BR-06); the implementation
enforces it in the wrong layer. Do not reproduce the pattern.

### BR-17 — Comparison filters by company sector, company type, and state
**Confidence: Evidenced.**

`comparador.php:214-222` builds `and codigo_sector = '...'`, `and codigo_tipo = '...'`,
`and codigo_estado = '...'` from three dropdowns populated via `sectores.php:185`,
`tipos_empresas.php:189`, `estados.php:130`.

Those three dropdowns depend on hardcoded development database credentials that the production
server rejected, and `comparacion/error_log` records 45
`Access denied for user 'root'@'localhost'` failures in March 2016 — **so the filters returned
nothing in production.** The feature exists; evidence suggests it did not work.

*Verdict:* the filter concept (scope comparison by sector/type/region) is plausible and useful;
whether it was ever used cannot be confirmed. Ask (Q-8).

---

## D. Identity, roles and access

### BR-18 — A user has a type; a type is granted a set of screens; screens belong to modules
**Confidence: Evidenced (model), broken (enforcement).**

`usuarios.Codigo_tipo_usuario` → `seguridades(Id_tipo_Usuario, id_pantalla)` →
`pantallas(id_pantalla, url_pantalla, id_modulo)` → `modulos`.

`pantallas.url_pantalla` stores **bare filenames** (`tbl_paises_list.php`), and the check compares
the currently requested file name against that column
(`pantalla.php:54` `where url_pantalla = %s`). Grants are per-screen, so permission granularity is
per-page, including read screens.

**Enforcement is broken in five independent ways** — see
[security-findings.md](security-findings.md) §S5. The practical consequence: the permission data
describes an intent that the code does not uphold, and 55 mutating screens carry no check at all.

*Verdict:* keep the concept (role → screen grants, per-page granularity is unusual but defensible
for an editorial back-office); rebuild enforcement. Ask (Q-9) whether per-screen grants are
genuinely wanted or whether per-capability permission is the real requirement.

### BR-19 — Roles as configured: Master, Operador, Transcriptor, plus per-customer profiles
**Confidence: Evidenced from data; intent Ambiguous.**

Six types, 146 grants:

| ID | Name | Grants |
|---|---|---|
| 1 | Master | 2 |
| 2 | Operador | 6 |
| 3 | Transcriptor | 45 |
| 25 | Perfil 2 – Leyes-Contratos-Comparativo | 18 |
| 27 | Perfil 3 – Leyes-Contratos-Comparativo-Bitacoras | 49 |
| 28 | DEMO | 15 |

The names suggest *work functions* (operator, transcriber) for the first three and *bundled
feature profiles* for the last three — two different axes in one table. `Transcriptor` holding
delete rights on companies and statutes contradicts its name. Type `5` has 11 grants but no row in
`tipos_de_usuarios`.

*Verdict:* do not treat this as a specification. Ask (Q-9).

### BR-20 — A user can be blocked (`BLO`), and blocking is enforced only at login
**Confidence: Evidenced.**

`lib/funciones/php/login.php:18` rejects `estatus == "BLO"`. `user->Bloqueo()` writes `'BLO'`.
Nothing else reads `estatus`, so a blocked user's existing session continues to work.

### BR-21 — A user account may have an expiry date, after which it is blocked
**Confidence: Inferred; implementation dead.**

`usuarios.fech_venc date`. `admin/modulos/bienvenida/principal.php` intends to warn 7 days before
and block on expiry, but: it passes a **login name** to `getId()`, which filters `where id = %s`
and returns nothing, so `$fecha` is empty; it relies on `split()` and a bareword `date(Y-m-d)`,
both fatal on modern PHP; the month arithmetic subtracts 31/30/28 by hand and mishandles year
rollover; and it runs **only on the welcome page**, so any other URL bypasses it. The Master type
(`==1`) is exempted.

No user row in the dump has a future `fech_venc`, so whether it ever worked cannot be determined.

*Verdict:* the intent (time-limited accounts) is plausible for this domain (contractors,
transcribers). The semantics are unknown. Ask (Q-10).

---

## E. Master data

### BR-22 — Deleting a country deletes its states and municipalities
**Confidence: Evidenced.**

`lib/funciones/php/tablas/pais/eliminarpais.php:10-12`:
```php
$pais->delete($id_pais);
$estado->deleteporPais($id_pais);
$localidad->deleteporPais($id_pais);
```
Three independent statements, no transaction, no confirmation of child volume. A failure mid-way
leaves orphans. Asymmetric: deleting a state also cascades to municipalities
(`eliminarestado.php` → `deleteporEstado`), but deleting a company checks nothing, so
`contratos.codigo_empresa` and `bitacoras.codigo_empresa` survive pointing at a deleted row.

*Verdict:* the rule (referential cleanup) is correct; the mechanism should be a real foreign key
with `ON DELETE RESTRICT` (not cascade — deleting a country that has agreements should be refused,
not silently cascaded). Ask (Q-11).

### BR-23 — A company is keyed by `codigo_empresa`, but that column is never written, and the primary key is `id`
**Confidence: Evidenced — and this is the most consequential data-model defect found.**

Every read, update and delete filters on `codigo_empresa`
(`empresas.php:29,49,69,89,147`). The **only** INSERT in the repository
(`empresas.php:360`) omits the column from its 23-column list; it is `int(11) NOT NULL` with no
default and no AUTO_INCREMENT. The dump contains no trigger and no procedure to backfill it.

The dump proves the two sequences are completely independent:

| | range | distinct |
|---|---|---|
| `id` (PK) | **116 – 195** | 80 |
| `codigo_empresa` | **2 – 115** | 80 |
| rows where `id == codigo_empresa` | **0 of 80** | |

So `obtenerIdUltimaEmpresa()` (`empresas.php:423`,
`select * from empresas order by codigo_empresa desc limit 0,1`) returns the highest **legacy**
value (115), and `agregarempresa.php` uses that value as the upload folder — meaning a newly
created company stores its logo in **another company's directory**.

*Verdict:* the 80 populated rows must predate this code. In the replacement there must be exactly
one identifier for a company, and it must be the primary key.

### BR-24 — A comparison heading belongs to a category block, which carries ordering and a flag
**Confidence: Evidenced.**

`categorias_titulos(codigo_categoria_titulos, nombre_categoria, descripcion_categoria,
requiere_campo_comparacion_economica)`; `titulos(codigo_titulo_comparativo, nombre_titulo,
descripcion_titulo, codigo_categoria_titulo)`. The comparison dropdown orders by
`nombre_categoria, nombre_titulo` — **alphabetically**, not by any priority.

One heading in the data is named "No Compara", which suggests an explicit *opt-out* heading
(subject excluded from comparison). **No code references it.** *Inferred only.* Ask (Q-12).

### BR-25 — News items are either pending or published
**Confidence: Evidenced.**

`noticias.estatus_noticia` ∈ {`'Pendiente'`, `'Publicar'`}; the public list filters
`estatus_noticia='Publicar'` (`noticias.php:150`). Defect: the create handler hardcodes
`'Pendiente'` (`noticias.php:174`) regardless of the submitted dropdown, so **publishing requires
a create followed by an edit**.

### BR-26 — A statute's article count is a maintained counter
**Confidence: Evidenced, fragile.**

`ley_trabajo.total_articulos`, `otras_leyes.total_articulos_ley`. Maintained on article
add/delete only. `ley_trabajo.php:148-153` updates with
`set total_articulos = %d WHERE total_articulos = %d` — matching on the **old value**, so it
succeeds only because the row is read immediately before. Never recomputed on renumber. *Verdict:*
a derived value; compute it, do not store it.

### BR-27 — Statute article numbers must be unique within a statute
**Confidence: Evidenced, with a defect.**

`agregar_articulo_ley_trabajo.php:13` checks `if($ley_trabajo->getArticuloLey($nro_articulo))` and
alerts. But `editar_articulo_ley_trabajo.php:23-24` tests the **old** number when the number
changes, which always exists, so **renumbering an article is impossible**. Also
`getArticuloLey2()` compares a `varchar` number with `%d`, so `'01'` and `'01-A'` collide.

*Verdict:* keep the uniqueness rule; it belongs in a database constraint, not a SELECT-then-INSERT.

---

## F. Cross-cutting conventions

### BR-28 — Dates cross the UI boundary as `dd-mm-yyyy`
**Confidence: Evidenced.**

`MenejoFechas.php` provides `FechaConvertirFormato()` (dd-mm-yyyy → yyyy-mm-dd) and its inverse,
plus `FechaConvertirFormatoCompleto()` for MySQL datetimes. All three are bare `explode()` calls:
no `checkdate`, no `strtotime`, no validation. `'0000-00-00'` becomes `'00-00-0000'`.

The dump corroborates: `bitacoras.fecha_inicio_disc` is `text` holding `'01-01-2005'`-style values,
and `acuerdos.fecha_acuerdo` is `text` too.

*Verdict:* the *display* convention is a real domain expectation (Spanish locale). The storage
convention is a defect. Use `date` columns, format at the boundary.

### BR-29 — Pagination is `LIMIT offset, size` with a `LIKE '%term%'` filter
**Confidence: Evidenced.**

Universal idiom across list screens, with `pager.php` computing offsets. Ordering is usually
`ORDER BY <name> ASC`. Known defects: several `contar*()` methods omit the filter that the
corresponding `listar*()` applies, so pager totals are wrong whenever a filter or company scope is
active (e.g. `discusion.php:643` vs `:1027`); `categoria_titulos.php:176` hardcodes `LIMIT 0,10`
and ignores the offset entirely.

### BR-30 — Nothing is validated server-side
**Confidence: Evidenced.**

Across the whole application there is **one** server-side field check outside of login:
`if ((trim($origen))=="")` on news (`agregarnoticias.php:12`). All other validation is JavaScript
in `plantillas/plantilla_admin/cabecera.php:38-79`, and those validators are wired to the wrong
forms — most call `validarEstado(this)` while dereferencing `data.id_pais`/`data.nombreestado`,
fields that do not exist on those forms, producing a `TypeError` that silently permits submission.
None of the three validators `return true`, so even a passing check does not guarantee submit.

There is **no** uniqueness check on any catalog name or on `usuarios.Login_usuario`; no email or
date format check; no numeric range check; no required-field enforcement on 23-field company
records.

*Verdict:* treat the legacy validation surface as nonexistent. Requirements must be gathered
anew (Q-13).

---

## Rules deliberately **not** carried forward

Stated explicitly so their absence is a decision, not an omission:

| Legacy behavior | Why not |
|---|---|
| `PE`/`Pe`/`PU` case-sensitivity and the two-click publish | Ambiguous state model; use a typed enum + `CHECK` |
| `estatus_bitacora` numeric codes in `char(3)` | Use a typed state machine with explicit transitions |
| `estatus_peticion = 9` magic value | Use a named state |
| Clause ordering by `varchar` | Lexicographic sort is a bug, not a rule |
| Placeholder strings (`'*Inicio'`, `'Resumen Pentiende'`, `'Campo Comparativo Pendiente'`) | Code-generated junk; the dump's dirty data is not a spec |
| Auto-cascading reference deletes | Should be restricted, not cascaded, where business rows depend on them |
| Dual company key (`id` + `codigo_empresa`) | Indefensible; single key |
| Stored `total_articulos` counters | Derive instead |
| Permission-by-hiding-UI | Must be enforced server-side |
| Master-type exemption from every check | No implicit superuser bypass; see constitution Art. VI |
