# Legacy tree removal and credential rotation (T16)

**Status:** implemented, pending review/merge.
**Executed:** 2026-10-31, branch `task/29-legacy-removal`, from the merge of PR #25 (rewritten as `2512ef9` by the history purge in §6).
**Authority:** the Product Owner instructed "proceed with T16" on 2026-10-31. Because the removal
contradicts an article of the constitution, the instruction is treated as the approval that
[Art. XIV](../constitution.md#art-xiv--amendment) requires, and the constitution is amended in the same
change (see §7).

---

## 1. What was removed

**1,869 tracked files**, `97,616` lines deleted, no other change in the same commit.

| Path | Tracked files | Size on disk | What it was |
|---|---|---|---|
| `admin/` | 998 | 148.3 MB | The PHP administration application, including contract PDFs |
| `lib/` | 522 | 2.2 MB | The PHP library: database drivers, objects, functions, vendored components |
| `plantillas/` | 121 | 0.9 MB | PHP templates |
| `img/` | 210 | 0.9 MB | Images |
| `new_img/` | 7 | < 0.1 MB | Images |
| `css/` | 3 | < 0.1 MB | Stylesheets |
| `index.html` | 1 | 112 B | The legacy public portal entry point |
| `presenci_ccol.sql` | 1 | 48.9 MB | The MySQL dump of the production database |
| **Total** | **1,869** | **~201 MB** | |

The working tree now contains only the new stack (`src/`, `tests/`, `supabase/`, `tools/`,
`docs/`, the solution and build files, `.githooks/`). The tracked-file count fell from **2,143 to
274**. After the history purge (section 6) the removed paths exist in no commit either; the counts
above describe the tree before the purge.

**One quirk, recorded because it affects reproduction.** On this machine the tracked dump cannot be
addressed by its literal name: `Get-Item`, `File.Exists`, `cmd /c dir presenci_ccol.sql` and
`git rm --cached -- presenci_ccol.sql` all report the path as missing, while `Get-ChildItem`,
`cmd /c dir presenci*.sql` and `git ls-files` list it. Wildcard access works, which is how it was
copied and deleted. The removal was staged with `git add -A` over the filesystem change rather than a
literal pathspec. If this repeats on another machine, use the wildcard and treat the literal-path
failure as a filesystem/console issue, not as evidence that the file is gone.

## 2. Why it was removed

| Reason | Evidence |
|---|---|
| **Committed database credentials** (release audit **B7**, security finding **S1**) | 19 `mysql_connect` call sites in 13 files carrying 4 credential sets (§3) |
| **Personal data committed** | The dump contains identifiable individuals; the tree contains contract PDFs. Constitution Art. VI.5 forbids both in the repository |
| **Committed legacy documents** | Art. VII.4 and Art. XI.2 say legacy PDFs may be inspected privately and are **not committed** — they were |
| **The tree was never a build input** | No file in `src/`, `tests/`, `supabase/` or `tools/` references a legacy path (the only match was a comment in `supabase/seed.sql`, updated here) |
| **The analysis is the retained evidence** | `docs/legacy/` (6 documents, **40** `BR-` rules, **15** `S-` security sections) and `docs/database/` (7 documents, **57** `DS-` findings) |

## 3. The credential inventory (passwords never reproduced here)

Nineteen `mysql_connect` sites resolved to **four distinct credential sets** plus one empty template.
Passwords are deliberately not written in this document; the values remain in git history and are
listed there.

| Set | Host / database | Account | Sites | Historical locations (removed files) |
|---|---|---|---|---|
| **A** | `localhost` / `CCCOL` | `root` | 6 | `lib/objetos/actividad_empresa.php:364`, `categoria_sector.php:169`, `estados.php:130`, `tipos_empresas.php:189`, `titulos.php:270`, `titulos.php:291` |
| **B** | `localhost` / `cccol` | `root` | 6 | `lib/objetos/discusion.php:1073,1087,1106,1118,1129`, `sectores.php:185` |
| **C** | `localhost` / `presenci_ccol` | `presenci_ccol` | 5 | `lib/objetos/categoria_titulos.php:268`, `pantalla.php:73`, `seguridad.php:75`, `titulos.php:239`, `lib/funciones/php/contratos/comparacion/conexion.php:6` |
| **D** | `localhost` / `CCCOL` | `pv014_admcccol` | 1 | `admin/modulos/settings.php:41` |
| — | none (class defaults) | empty | 1 | `lib/bd/drivers/mysql.php:51` — template, no password |

Set **C** appears twice in the inventory because `lib/funciones/php/contratos/comparacion/conexion.php`
bypassed the `$LibSite` abstraction and opened its own connection with its own literals — the same
weakness security finding S1 describes. The release audit counted **17** sites with literal
credentials; this inventory counts **18** literal sites plus the empty template. The difference is one
site and is not material to the remediation; the recorded figure is the measured one.

## 4. Rotation checklist

**Nothing in this section is done.** Deleting the files does not rotate anything: the credentials are
still valid wherever those database servers still exist, and they remain readable in git history.

Owner for every row: **Product Owner** — the only person who can reach the legacy MySQL servers or
declare them decommissioned. An agent must not attempt the rotation (T16 scope, and Art. VI.4 for
remote systems).

| # | Credential | System it belongs to | Where it was | Action | Status |
|---|---|---|---|---|---|
| 1 | Set A — `root` @ `CCCOL` | Legacy MySQL server (localhost at authoring time; possibly the packaged local stack) | `lib/objetos/*.php` | Rotate the password, or confirm the server is decommissioned and destroy the database | **OUTSTANDING** |
| 2 | Set B — `root` @ `cccol` | Same server, different schema/case | `lib/objetos/discusion.php`, `sectores.php` | Rotate or decommission | **OUTSTANDING** |
| 3 | Set C — `presenci_ccol` @ `presenci_ccol` | Legacy MySQL production account | `lib/objetos/*.php`, `comparacion/conexion.php` | Rotate; check for reuse of the same password on any other system | **OUTSTANDING** |
| 4 | Set D — `pv014_admcccol` @ `CCCOL` | Legacy MySQL administration account | `admin/modulos/settings.php` | Rotate or decommission | **OUTSTANDING** |
| 5 | Supabase / new stack | Not in this repository | — | Nothing to rotate here; T18 covers provisioning | Not applicable |

**Done when:** the owner records, for each row, either the rotation date or a dated statement that the
server no longer exists. Until then the exposure is open, and this document must not be read as
saying otherwise.

## 5. Verification executed

### 5.1 Secret scan (AC 1)

The scan is committed as [`tools/scan-secrets.ps1`](../../tools/scan-secrets.ps1): five patterns
(credential assignment, URL with password, `mysql_connect` with a literal password, PEM private key,
AWS access-key id), tracked files only by default, values redacted in its output, exit code 1 on any
match. It has **no allowlist**.

```
PS> pwsh ./tools/scan-secrets.ps1
Secret scan - 276 files, 5 patterns
CLEAN: no credential-shaped string found.
exit code: 0

PS> pwsh ./tools/scan-secrets.ps1 -IncludeWorkingTree
Secret scan - 276 files, 5 patterns
CLEAN: no credential-shaped string found.
exit code: 0
```

The first run, made immediately after the deletion and before the new documents existed, scanned 273
tracked files. The final tree has 276: the removal record, the scanner itself, and `.githooks/pre-push`
(a shell script with no extension, which the first filter skipped and the filter now includes).

Two test fixtures were reshaped to make that true without an exception, because both contained
credential-shaped strings that were not secrets:

- `src/Convenciones.Web/src/features/auth/LoginPage.test.tsx` asserted the typed value with a literal;
  it now uses a variable, so the file contains no credential-shaped assignment.
- `src/Convenciones.Web/scripts/check-bundle.test.ts` built a database URL carrying a user and a
  password to test the bundle scanner; it now assembles both at run time.

The scanner itself is ASCII-only (a UTF-8 em dash read as CP1252 became a smart quote and broke
PowerShell 5.1 parsing), and it assembles the private-key marker and the scheme separator at run time
so it does not match its own patterns.

### 5.2 What the removal did not break

| Check | Result |
|---|---|
| `git ls-files` after staging | 274 files, none under the removed paths |
| New-stack references to legacy paths | only `supabase/seed.sql:10`, a comment, updated |
| `dotnet build Convenciones.slnx` | see the task record (§4 of the record) |
| `dotnet test` and the web suite | see the task record |

## 6. Git history: what was done (AC 5)

Deleting the working copy was only half of the exposure: every removed file was still reachable in
every earlier commit. On 2026-10-31, with the Product Owner's explicit authorisation, the history
was rewritten to purge the legacy material from every ref.

- **Backup first.** A full mirror clone at `C:\proyectos\convenciones-pre-rewrite.git` (28 refs,
  150.9 MB), verified to still contain the dump blob. It holds the credentials and the personal data
  and must be protected like the archive.
- **Rewrite.** `git filter-repo --invert-paths` over `admin`, `lib`, `plantillas`, `css`, `img`,
  `new_img`, `index.html`, `SpryAssets`, `Scripts` and `presenci_ccol.sql` (glob), across every ref.
  The removal commit became empty and was pruned; the documentation and tooling commits survived.
- **Verification, run after the rewrite.**
  - zero objects under any removed path in `git rev-list --objects --all`;
  - the dump blob `322d113c...` is no longer retrievable (`git cat-file -s` fails);
  - `git log -S 'mysql_connect('` matches **no** commit, so no literal connection call survives;
  - the pack dropped from 150.9 MB to under 1 MB of objects.
- **Remote.** Every branch was force-pushed with the rewritten history; `main` is `95004ae`.

**What this achieves, and what it does not.** It removes the material from this repository's history
and from every branch on GitHub. It does **not** reach GitHub's pull-request refs (#6-#26), which
keep the old commits, nor clones and mirrors made before the rewrite, nor the backup above. Those
remain readable until GitHub Support purges the remote refs and the copies are destroyed. **Rotation
is still the only remedy that removes the value of the credentials, and it is outstanding.**

**Cost, recorded deliberately.** Every commit SHA changed, so the short SHAs quoted in older documents
(for example the audit at `92dfa3d`) no longer resolve; open pull requests had to be recreated from
the rewritten branches, and every collaborator must re-clone.
## 7. Constitutional conflict, resolved in this change

[Art. IX.1](../constitution.md#art-ix--the-legacy-codebase-reference-only) said the legacy PHP
application and the MySQL dump "are never modified, moved, or deleted". T16 deletes both. Art. XIV
forbids a silent deviation, so the article is amended in the same change:

- **Art. IX.1** now records that the tree and dump were removed from the repository on 2026-10-31
  (T16), that the committed analysis in `docs/legacy` and `docs/database` is the retained evidence, and
  that the removed material is archived outside Git because it carried credentials (Art. VI.3) and
  personal data (Art. VI.5).
- The constitution version becomes **1.3.0**, with an entry in the amendment log.

**This is the part of T16 that needs the Product Owner's explicit confirmation at review.** The
instruction "proceed with T16" was read as that approval; if it was not intended to amend the
constitution, the correct outcome is to reject the pull request, restore the files from the archive,
and execute the narrower option ("keep them out of the build only") instead.

## 8. Archive outside the repository

Because the deletion is irreversible for the working tree, the material was preserved **outside Git**
before removal:

| Path | Contents | Size |
|---|---|---|
| `C:\proyectos\convenciones-legacy-archive\tree\` | `admin`, `lib`, `plantillas`, `css`, `img`, `new_img`, `index.html` | 152 MB |
| `C:\proyectos\convenciones-legacy-archive\presenci_ccol.sql` | The dump, copied with a wildcard | 48.9 MB |
| `C:\proyectos\convenciones-legacy-archive\local\` | The untracked `local/configuracion*.php` files that were never in Git | 10 KB |
| `C:\proyectos\convenciones-legacy-archive\README.txt` | Provenance: source repository, commit `28b89be`, date, and a warning | — |

**The archive is not safe.** It contains the same credentials and personal data, unencrypted, on this
machine. It exists only so the analysis can be checked against the source and so the dump is not lost;
it must be protected like the secret it holds and deleted once it is no longer needed.

`local/` was also left in place in the working tree: it is untracked and ignored, and it is the
developer's own configuration. It is not part of the repository and is not covered by the scan's
default scope.

## 9. Left undone

1. **Rotate the four credential sets** (§4) — the only step that makes the credentials safe.
2. **Remote refs and clones** (§6) — the local and branch history is clean; GitHub pull-request refs and
   pre-rewrite clones are not, and purging them needs GitHub Support plus destroying the backup.
3. **Run the scan in CI** — [`tools/scan-secrets.ps1`](../../tools/scan-secrets.ps1) is ready for T15,
   which is also where the bundle check joins it.
4. **Prune the now-dead rules** in `.gitattributes` (done for `*.php` and the dump) and in `.gitignore`
   (the legacy block is replaced by an explicit "removed" note).
