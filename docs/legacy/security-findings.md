# Security findings — legacy PHP application

Static, read-only analysis of `admin/`, `lib/`, `plantillas/` and `local/`.

## Method and its limits

**No finding here was produced by running the application.** It cannot be run on a supported
runtime: it is built on `ext/mysql` (`mysql_*`, removed in PHP 7.0), `ereg_replace()`
(removed in 7.0), `split()` (removed in 7.0) and `each()` (removed in 8.0), and calls
`reset()` on result **objects** (a `TypeError` in PHP 8). Reviving it would require rewriting
the very layers under audit, which would invalidate the evidence.

So every claim below is backed by quoted source with a file and line, not by exploitation.
Where impact depends on runtime configuration that is not in the repository (MySQL
`sql_mode`, PHP `display_errors`, server docroot layout), that is stated explicitly.

Counts were measured across the tree, not estimated: 444 PHP files, 55,175 lines of
first-party PHP, 179 registered routes, 19 `mysql_connect` call sites in 13 files.

**Nothing here is a suggestion to patch the legacy application.** It is being replaced. These
findings exist to (a) prove which behaviors must not be reproduced and (b) drive credential
rotation and threat modelling for the replacement.

---

## S1 — Live-looking database credentials committed in source

**Severity: critical.** Three distinct credential sets are hardcoded in 17 call sites, plus a
fourth in the git-ignored `local/configuracion.php`.

| Credential set | Database | Sites |
|---|---|---|
| `pv014_admcccol` / `***` | `CCCOL` | `admin/modulos/settings.php:41` |
| `presenci_cccol` / `***` | `presenci_cccol` | `lib/objetos/seguridad.php:75`, `lib/objetos/pantalla.php:73`, `lib/objetos/categoria_titulos.php:268`, `lib/objetos/titulos.php:239`, `lib/funciones/php/contratos/comparacion/conexion.php:6` |
| `root` / `***` | `cccol` / `CCCOL` | `lib/objetos/discusion.php:1073,1087,1106,1118,1129`, `lib/objetos/sectores.php:185`, `lib/objetos/estados.php:130`, `lib/objetos/tipos_empresas.php:189`, `lib/objetos/categoria_sector.php:169`, `lib/objetos/actividad_empresa.php:364`, `lib/objetos/titulos.php:270,291` |
| `root` / *(empty)* | `cccol` | `local/configuracion.php` (DSN index 0 — the connection the app actually uses) |

Passwords are intentionally **not reproduced** in this document; they remain in the legacy
sources, which are the authority. A fifth pattern: `local/configuracion.php` stores DSNs
obfuscated only by `rawurlencode()`, which is reversible percent-encoding, not encryption.

Two aggravating details:

- `admin/modulos/settings.php:41` uses a **different database name** (`CCCOL`) from the app's
  own DSN (`cccol`). Case sensitivity depends on the server filesystem, so that code path was
  environment-specific and is probably broken elsewhere.
- `lib/funciones/php/contratos/comparacion/conexion.php` is a standalone second connection
  file with the same production credentials, bypassing the `$LibSite` abstraction entirely.

**Update 2026-10-31 (T16):** the Product Owner confirmed the MySQL infrastructure was decommissioned and no
other system used these accounts; rotation is not applicable. See [../operations/legacy-removal.md](../operations/legacy-removal.md). The finding above is kept as the historical record.

---

## S2 — Authentication bypass by hardcoded override

**Severity: critical.** `admin/index.php` is the only bootstrap for the admin surface.

```php
14: if (isset($_SESSION['nombre'])){ $nombreses = $_SESSION['nombre']; } else { $nombreses=""; }
20: $nombreses="Alex";
21: if($nombreses!=""){
22:     include('modulos/settings.php');
23: } else {
24:     ... login branch, never reached ...
```

Line 20 unconditionally overwrites the session-derived value, so the login branch at lines
23–29 is **unreachable dead code**. Every request renders the authenticated shell.

```php
31: $t_usuario=1;
```

Line 31 hardcodes the caller as user type `1` (Master), so every `if($t_usuario!=1)` permission
check evaluates to "privileged". `plantillas/plantilla_admin/cabecera.php:119` then renders the
navigation menu whenever `$t_usuario` is set.

Consequence: `?url=contratos&tbl=Contratos&accion=listar` returns agreement data to an
anonymous client. This is not a subtle flaw — it is a debugging stub left in a production
entry point, and it single-handedly voids the entire permission model.

Modules that recover a real identity still get it wrong for their own reasons: `tablas/`,
`seguridad/`, `perfil/`, `bienvenida/`, `principal/` each re-read `$t_usuario=$_SESSION["tipo"]`
in their `index.php`, so they *would* be subject to real checks — except that their deny
branches are either commented out (`contratos/settings.php:390-399`) or overwritten
(`tablas/settings.php:525-538`). See S5.

---

## S3 — Passwords stored and compared in plaintext, with wildcard bypass

**Severity: critical.** No hashing exists anywhere in the codebase.

```php
// lib/objetos/user.php:154-172 — function login($user, $password)
$qQuery = "Select * from usuarios WHERE Login_usuario LIKE '".$user."' AND Clave_usuario LIKE '".$password."'";
```

Three independent defects in one line:

1. **Plaintext storage.** The comparison is against the column value directly;
   `updatePassword()` (`user.php:113-127`) and `insert()` (`user.php:278-284`) write the plain
   string. The dump corroborates: `usuarios.Clave_usuario varchar(20)` holds human-typed
   passwords of typical length, and some are 5–9 alphanumeric characters.
2. **Wildcard authentication bypass.** `LIKE` is used on credentials. Submitting `%` for both
   fields matches any row, provided the row's values are non-empty. This is a full login bypass
   with no password knowledge.
3. **SQL injection** in the same statement — see S4.

Related: `lib/funciones/php/perfil/password/editarpassword.php` changes a password by comparing
`pass1`/`repass` and calling `updatePassword()` **without ever verifying the current password**,
so possession of a session is sufficient to seize the account.

`lib/objetos/2user.php:157` contains a second, near-identical `login()` with the same defects —
a copy-paste duplicate. Whichever file is included wins; both are reachable.

---

## S4 — Systemic SQL injection; the abstraction's escaping is unsound

**Severity: critical.** `lib/bd/base.php:136-148` defines the project's single quoting entry
point:

```php
function quoteString( $str, $noEscape = FALSE ) {
    if( ! $noEscape ) $str = $this->escapeString( $str );
    $str = "'" . $str . "'";
    if( preg_match( "/^(\'.*)\+\'\'$/", $str, $parts )) $str = $parts[ 1 ];
    return $str;
}
```

For the MySQL driver (`lib/bd/drivers/mysql.php:172-177`, the `default:` branch — reached
because `dbType` is parsed from the DSN as `MySQL`):

```php
$str = ereg_replace( "'", "\\'", $str );
$str = ereg_replace( "\n", "\\n", $str );
$str = ereg_replace( "\r", "\\r", $str );
$str = ereg_replace( "\t", "\\t", $str );
```

`quoteString` therefore **does not make injection impossible**, for five separate reasons:

1. **Backslashes are never escaped.** A lone trailing `\` in input escapes the quote that
   `quoteString` itself appends, producing an unterminated literal: `'\'` swallows the closing
   delimiter. This is the classic escape-sequence injection and it is a one-character payload.
2. **LIKE wildcards pass through.** `%` and `_` are untouched, which is precisely what makes
   S3's wildcard login work and what makes every `LIKE '%".$filtro."%'` search injectable in
   its semantics even when quoted.
3. **No numeric handling.** `%d` is applied ad hoc by callers via `sprintf`; the abstraction has
   no notion of a typed parameter, so any caller that forgets `%d` gets raw concatenation.
4. **No identifier or clause protection.** `ORDER BY`/`LIMIT` are interpolated from request
   data (`opciones_consulta` in `lib/objetos/contratos.php` builds `ORDER BY emp.` + input).
5. **Charset is never negotiated.** No `SET NAMES` and no `mysql_set_charset()` anywhere, so
   multibyte-based escaping attacks are neither prevented nor excluded.

`quoteDate()` is worse: `lib/bd/base.php:125-133` returns `"'".$date."'"` with **no escaping at
all**. There are no prepared statements, no placeholders, and no typed parameters anywhere in
the codebase — `execute()` passes a finished SQL string to `mysql_query`.

The abstraction is also routinely bypassed. `escapeString()` in the base class is a no-op
(`base.php:171-174` `return $str;`), so any code path that reaches the base implementation
rather than the MySQL override performs zero escaping.

Representative injection sites (all raw concatenation of superglobals):

| File:line | Input |
|---|---|
| `lib/objetos/user.php:158` | login + password (S3) |
| `lib/objetos/discusion.php:27` | `$_GET['id']` unquoted numeric context |
| `lib/objetos/discusion.php:342` | eight `$_REQUEST` values, unquoted |
| `lib/objetos/discusion.php:738,769,804,853,886,922,954,988,1027,1029,1178` | `$_GET['id']`, `$filtro` |
| `lib/objetos/otras_leyes.php:391` | `insertarLey()` — no quoting at all |
| `lib/objetos/otras_leyes.php:252` | `$filtro` inside `LIKE` |
| `lib/objetos/pais.php:95` | `$filtro`, and `LIMIT ".$ini.", ".$fin` |
| `lib/objetos/ley_trabajo.php:186,191` | `$filtro` |
| `admin/modulos/contratos/comparacion/comparador.php:241,305,416` | `$empresa_dato`, contract ids |
| `admin/modulos/contratos/discusion/discusion_contratos_agregar_reuniones_externa.php:84` | **a whole SELECT statement carried in a GET parameter** (`peticiones_sindicato_2`) and passed verbatim to `mysql_query()` |

That last row deserves emphasis: the comparison/meeting screen transmits an entire SQL
statement through a hidden form field and executes whatever arrives. It is not an injection
*risk*; it is a remote SQL execution feature.

**Not verifiable from the repository:** whether `magic_quotes_gpc` was enabled on the production
server. If it was, some string injections would have been masked. That setting was removed in
PHP 5.4 and cannot be assumed either way, so the conservative reading stands: all of the above
is reachable.

---

## S5 — Authorization is attached to read screens and absent from write screens

**Severity: critical.** The permission *model* is coherent and the *enforcement* is largely
decorative. Measured across `admin/modulos` (215 PHP files):

| Metric | Count |
|---|---|
| Files referencing `$_SESSION` at all | 55 |
| Files referencing no session state whatsoever | 160 |
| Files calling the RBAC helpers `getSeguridad`/`getporUrl` | 43 |
| …of which are `_list`/`_ver`/`listar` (read screens) | 32 |
| Mutating screens (`agregar`/`editar`/`eliminar`/`cerrar`/`reabrir`) | 55 |
| …that carry any permission check | **1** |

The single guarded write endpoint is
`discusion_contratos_agregar_reuniones_externa.php`. Everything else — deleting an agreement,
publishing it, creating a user, editing a clause — has **no server-side check**. Authorization
was implemented as "hide the button", and the button is still reachable by typing the URL.

The one intended deny in the contracts module is commented out:

```php
// admin/modulos/contratos/settings.php:390-399
/*
if($_SESSION['tipo']!=1){
    $var=$pantalla->getporUrl($url);
    ...
    if(!$seg){ $tbl="error.php"; }
}
*/
```

And in `tablas/settings.php:525-538` the deny is assigned then immediately overwritten:

```php
if(!$seg){ $tbl="error.php"; }
...
if($dir=="") $tbl=$url; else $tbl=$dir."/".$url;   // denial discarded
```

Additionally, `admin/error.php` is a 13-line static HTML page reading "No tiene Permiso". It
returns **HTTP 200**, has no redirect, no logic, and renders inside the normal admin shell with
the full navigation menu visible. Even when reached, it neither blocks nor hides anything.

**The role data does not describe a sane model.** 146 grants across 6 user types:

| Type | Name | Grants |
|---|---|---|
| 1 | Master | 2 (irrelevant — bypasses every check) |
| 2 | Operador | 6 (read-only geographic catalogs) |
| 3 | Transcriptor | **45** (includes delete on companies and laws) |
| 25 | Perfil 2 – Leyes-Contratos-Comparativo | 18 |
| 27 | Perfil 3 – Leyes-Contratos-Comparativo-Bitacoras | **49** |
| 28 | DEMO | 15 |

The "Transcriptor" (transcriber) holds more power than the "Operador" (operator). Type **5** is
referenced by 11 grants but does not exist in `tipos_de_usuarios` — orphan rows, possible
because MyISAM has no foreign keys. Whether these roles reflect intent or accumulated drift
cannot be determined from code or data.

---

## S6 — Local file inclusion through the dispatcher

**Severity: high.** `admin/modulos/contratos/index.php` reads the template path directly from
the request and includes it:

```php
4:  $tabla=$_GET['tbl'];
5:  $tbl=$_GET['tbl'];
...
114: <td height="100%" colspan="2" valign="top" ><? include($tbl); ?></td>
```

`admin/modulos/contratos/settings.php:24` then runs `switch ($tabla)` with cases
`"Contratos"`, `"Comparacion de Contratos"` and `"Bitacora"` **and no outer `default`**, so any
other `tbl` value leaves `$tbl` as the raw request string and line 114 includes it. Verified by
reading the dispatcher to its closing brace (line 398): the only `default:` cases belong to the
inner `$accion` switches.

Practical reach: includes of arbitrary local paths, and `allow_url_include`-dependent remote
inclusion. The path is prepended with nothing, so absolute paths and `../` traversal both work.

**A claim I had to correct.** The same pattern in `admin/index.php` (`include($url)`, line 36) is
**not** exploitable: `modulos/settings.php` is a whitelist switch over `$_GET['url']` with an
explicit `default` that sets `$url` to a fixed path, so `$url` can never carry attacker input.
The vulnerability is real but narrower than a first reading suggests — it exists specifically
where a dispatcher lacks an outer `default` (the contracts module), not everywhere an
`include()` takes a variable.

---

## S7 — Unrestricted file upload into a web-served directory

**Severity: high.** Uploads exist in the contracts, negotiation, legal-library and master-data
modules. The common pattern:

```php
// admin/modulos/leyes/ley_trabajo/lys_ley_trabajo_editar.php
if($tipo=='application/pdf'){ $ext='pdf'; }
move_uploaded_file($_FILES['pdf_asociado']['tmp_name'],
                   'modulos/leyes/ley_trabajo/media/'.$nombre);
chmod(..., 0777);
```

Four defects, repeated across modules:

1. **The saved filename is the client-supplied `$_FILES[...]['name']`, unsanitised** — no
   `basename()`, no extension rewrite, no randomisation. A client sending
   `application/pdf` as the MIME type while naming the file `x.php` can plant an executable
   file in a directory the web server serves.
2. **The extension is derived from the client's MIME claim**, not from the filename or the
   bytes. `$_FILES[...]['type']` is attacker-controlled.
3. **`chmod 0777`** on both files and created directories.
4. **The old file is deleted before the new one is validated** (`lys_ley_trabajo_editar.php:17`
   unlinks inside the "a file was chosen" branch), so a failed edit destroys the previous
   document.

Separately, `unlink()` paths are built from database-stored filenames, giving path traversal on
delete if a filename is ever attacker-influenced.

---

## S8 — Cross-site scripting, by design

**Severity: high.** The application stores and re-emits HTML. Clause text, petition text, offer
text and news bodies are authored in tinyMCE and printed without escaping:

```php
// admin/modulos/contratos/discusion/discusion_contratos_listar.php:88
print $nombre_empresa;
// admin/modulos/contratos/comparacion/comparador.php:436
print $texto_completo_articulo;
```

There are **zero** calls to `htmlspecialchars`/`htmlentities` in `admin/modulos`. The rich-text
fields make this a stored-XSS channel by construction: any user who can author a clause can
execute script in the session of any user who views it — including a Master, which by S2 is
everyone.

Two refinements worth noting for the redesign:

- Because the content is *meant* to be HTML, escaping on output is not a sufficient answer.
  The replacement needs a sanitisation policy (allow-list of tags) for authored rich text, not
  blanket escaping.
- `admin/modulos/contratos/discusion/impresion_gen.php:20-30` opens a same-origin window and
  `document.write()`s `$_REQUEST['texto_completo']` — reflected XSS with no storage required.

---

## S9 — No CSRF protection; destructive actions are GET

**Severity: high.** There is no CSRF token anywhere in the application — no nonce, no
per-request secret, no origin check. Combined with:

- Deletes triggered by a **GET** request with a boolean flag in the query string:
  `?accion=eliminar&id=7&flag=SI`.
- Every form posting to `action=""`, i.e. re-entering the same dispatcher.
- Session cookies with no `SameSite` attribute set in code (PHP defaults were unset at the time).

Any page on the internet that causes a logged-in administrator's browser to request a delete
URL deletes a record. Given S2, the browser does not even need a logged-in session.

---

## S10 — Session handling offers no fixation or theft resistance

**Severity: medium-high.**

- 40 `session_start()` calls, **zero** `session_regenerate_id()`. No fixation defence.
- No `httponly`, `secure`, or `samesite` cookie parameters set anywhere.
- No idle timeout, no absolute lifetime, no IP binding.
- Only **one** `isset($_SESSION)` guard exists in the whole admin tree
  (`admin/index.php:14`) and S2 overrides it.
- Logout (`admin/modulos/settings.php:39-64`) writes an audit row to the non-existent
  `usuarios_login` table, destroys the session, and redirects — but `redirect.php` does not
  call `exit`, so execution continues after `header("Location: ...")`.

Stealing or fixing a session yields full Master access (S2), so the absent controls matter more
than usual.

---

## S11 — Account expiry is broken and bypassable

**Severity: medium.** Expiry **is** implemented, in
`admin/modulos/bienvenida/principal.php:5-70`, and it is genuinely unusable:

```php
$user->getId($login);                       // passes a LOGIN string to getId()
$cadena = split("-", $fecha);               // split() removed in PHP 7
if ($_SESSION["tipo"]!=1)                   // Master never expires
  if ($cadena[0]-date("Y")==0)              // date(Y) — bareword constant, fatal in PHP 8
    if ($cadena[2]-date("d")<=0) $user->Bloqueo($login);
```

Defects: `getId()` filters `where id = %s` but receives a login name, so it returns nothing and
`$fecha` is empty; `split()` and the unquoted `date(Y-m-d)` are fatal on modern PHP; the
month arithmetic subtracts 31/30/28 by hand and mis-handles year rollover; and the check runs
**only on the welcome page**, so any other URL sidesteps it.

`estatus='BLO'` (blocked) *is* enforced, but at login only (`lib/funciones/php/login.php:18`).
An account blocked mid-session keeps working until it re-authenticates.

For the redesign: expiry is a real business intent worth keeping; its implementation is not
evidence of the intended semantics. See open questions Q-4.

---

## S12 — Information disclosure

**Severity: medium.**

- SQL statements echoed into rendered pages: `print $query;` in `lib/objetos/ley_trabajo.php:52`,
  `echo $lot_listar;` in `comparador.php:320`, `echo $tbl;` in `admin/modulos/leyes/index.php:103`.
- `admin/error_log` (120 KB, committed) contains production error output;
  `admin/modulos/contratos/comparacion/error_log` records repeated
  `mysql_connect(): Access denied for user 'root'@'localhost'` in March 2016 — hard evidence that
  the hardcoded `root` credentials did **not** work in production, which is why those drop-downs
  returned nothing.
- Connection failures `die("Could not connect")` with no logging and no differentiation from a
  query error.

---

## S13 — Nothing fails visibly, so failures are silent data loss

**Severity: medium** (correctness, not confidentiality).

`lib/bd/drivers/mysql.php:71-85` `execute()` returns a result object unconditionally; there is
no success test, and no `mysql_error()`/`trigger_error()` call exists anywhere in the data layer.
Every domain method then ends with `return true` regardless of outcome (e.g.
`lib/objetos/discusion.php:68-70,165-168,510-512,577-579`). A failed `INSERT` is
indistinguishable from a successful one, so the UI reports success for work that was never
persisted.

Transactions do not exist: `beginTransaction()`, `commit()` and `rollback()` in the MySQL driver
all `return TRUE` without emitting SQL, and `isInTransaction()` returns `TRUE` unconditionally —
it lies. With MyISAM tables, atomicity is structurally impossible. Multi-step business
operations run unprotected, most notably the negotiation-to-agreement handoff which performs
five dependent writes.

Concrete instance: `lib/objetos/contratos.php` writes the literal strings `'*Inicio'`, `'*Fin'`,
`'*Duración'`, `'*Ambito'` into `contratos.fecha_de_inicio` / `fecha_de_termino`, which are
`date` columns. Under strict `sql_mode` the insert fails silently (result unchecked) and
`obtenerIdUltimoContrato()` then returns a pre-existing contract id, appending negotiated
clauses to the **wrong agreement**. Under non-strict mode the dates become `0000-00-00`.
Which mode production used is not recorded in the repository.

---

## S14 — No audit trail, and the intended one targets a table that does not exist

**Severity: medium.** No audit table exists in the schema, and no table carries created/updated
metadata. The system does attempt to log logins:

```php
// lib/objetos/user.php:177-183 and admin/modulos/settings.php:46-47
INSERT INTO `usuarios_login` (`Login_usuario`,`date_login`,`ip_login`,`tipo`) VALUES (...)
```

`usuarios_login` **is not in the dump** (35 tables, verified). Both writers therefore fail
silently — the audit was never functional in the captured environment. Deletions are hard
`DELETE`; `estatus_bitacora` is overwritten in place, so negotiation state history is lost.

Auditability is a **new requirement** for the replacement, not a preserved behavior.

---

## S15 — The legacy application cannot run on a supported runtime

**Severity: informational, but constrains the whole project.**

| Construct | Sites | Removed in |
|---|---|---|
| `mysql_*` | 42 in `lib/bd/drivers/mysql.php`; 19 `mysql_connect` + 50 `mysql_query` elsewhere | PHP 7.0 |
| `ereg_replace()` | 38 (this is the *only* escaping routine) | PHP 7.0 |
| `split()` | 19 in 17 files | PHP 7.0 |
| `each()` | 300 in 148 files | PHP 8.0 |
| `reset()` on an object | dozens | `TypeError` in PHP 8 |
| PHP4-style constructors (`function FPDF(...)`) | vendored FPDF/HTML2FPDF | args dropped in PHP 8 |

Consequence for the project: **the legacy system cannot be executed to observe behavior**, so
there is no way to distinguish "documented in code" from "actually worked in production" except
through data. Where the dump is silent, behavior must be treated as intent, not fact. This is
the single largest epistemic limit on this audit and the reason `open-questions.md` is long.

---

## Anti-patterns the replacement must not reproduce

Recorded so they can be tested against, not just remembered:

1. Credentials in source; `root` DB account; empty passwords; percent-encoding as "obfuscation".
2. Authentication bypass stubs left in the bootstrap (`$nombreses="Alex"`, `$t_usuario=1`).
3. Plaintext password storage; `LIKE` on credentials; password change without current password.
4. String-concatenated SQL. The replacement uses parameterised queries exclusively — no
   "quoting helper" as the security boundary.
5. Authorization implemented as hidden UI. Every mutating endpoint must check server-side.
6. A permission-denied page that returns HTTP 200 and renders the application shell.
7. `include()` of any request-derived path, with or without a dispatcher.
8. Uploads named by the client, typed by client MIME, into web-served directories, with `0777`.
9. Blanket-escaped rich text *or* unescaped rich text — the replacement needs a sanitiser policy.
10. Destructive actions over GET; no CSRF token.
11. Silent failure: no result checking, `return true` unconditionally, no transactions.
12. Roles defined by accumulated grants with no stated intent, and orphan grant rows.
13. An audit log that writes to a table that does not exist.
14. Expiry logic duplicated in a view, computed by hand, and enforced on one URL only.

---

## Cannot be confirmed

- Whether any of these were ever exploited. No access logs are in scope.
- Production `sql_mode`, `display_errors`, `magic_quotes_gpc`, `allow_url_include`, and docroot
  layout — each materially changes exploitability and none is recorded here.
- Whether the three credential sets were all live simultaneously; `error_log` proves the `root`
  set failed in March 2016, but not that the `presenci_cccol` set worked.
- Whether `usuarios_login` existed in some environments (it is absent from the only dump).
- Whether the role grants (S5) reflect deliberate policy. No document states intent; the
  profile names suggest per-feature profiles that were configured per customer.
- Real password strength/entropy — passwords are visible in the dump, but characterising them
  further than "plaintext, short, alphabetic-numeric" adds no value.
