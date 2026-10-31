# T16 — Legacy tree removal and credential rotation

**Status:** implemented (removal, history purge) — **credential rotation outstanding** · **Requires:** your approval before executing
**Branch:** `task/29-legacy-removal` (from `main` at `28b89be`)

## Objective

Remove the exposed credentials from the repository, and preserve the reference value of the legacy
analysis without the secrets.

## Scope

**In:** identify the **17** hardcoded `mysql_connect` sites; decide per directory whether it is needed as
read-only reference; extract the analysis that matters (already captured in `docs/legacy/*` and
`docs/database/*`) and remove the rest; remove or relocate the MySQL dump; write the rotation checklist
with owners.

**Out:** rotating credentials myself — that is a service-owner action and must not be attempted without
authorisation.

## Deliverables

| File | Purpose | State |
|---|---|---|
| `docs/operations/legacy-removal.md` | What is removed, what is kept and why, rotation checklist | **written** |
| Repository change | Legacy tree and dump removed (or moved out of Git) after approval | **done, 1,869 files** |
| `tools/scan-secrets.ps1` | The scan AC 1 asks for, runnable locally and in CI | **written and run** |

## Acceptance criteria — evidence

| AC | Evidence |
|---|---|
| 1. A secret scan across the whole repository returns **zero** credential-shaped matches — proven by running it | `pwsh ./tools/scan-secrets.ps1` → `Secret scan - 273 files, 5 patterns` / `CLEAN`, exit 0. With `-IncludeWorkingTree` → `274 files` / `CLEAN`, exit 0. No allowlist; two test fixtures were reshaped instead |
| 2. Every behaviour the new system depends on is traceable to `docs/legacy/*`; nothing is lost by removing the code | No file in `src/`, `tests/`, `supabase/` or `tools/` referenced a legacy path (0 matches after the seed comment was updated). The analysis is committed: 6 documents in `docs/legacy` (**40** `BR-` rules, **15** `S-` sections) and 7 in `docs/database` (**57** `DS-` findings) |
| 3. The rotation checklist names each credential, the system it belongs to, and who must rotate it | [legacy-removal.md §4](../operations/legacy-removal.md): four credential sets (A–D) with host/database, account, historical file:line, system, owner and status **OUTSTANDING** |
| 4. Deleting files is **not** described as making credentials safe | [legacy-removal.md §4 and §6](../operations/legacy-removal.md) state that the credentials are still valid and still in history, and that rotation is the only remedy |
| 5. Git history is addressed explicitly | [legacy-removal.md §6](../operations/legacy-removal.md): the history was rewritten with `git filter-repo`, every ref force-pushed, and the purge verified (no legacy path, no dump blob, no literal connection call). Pull-request refs, pre-rewrite clones and the backup remain |

## Risks

- Removing the dump is irreversible if the analysis was incomplete. The analysis docs are the insurance,
  so they must be verified complete first. **Mitigated:** the analysis was verified (AC 2) and the whole
  tree and dump were archived outside Git before deletion (§8 of the removal record).
- Removing files from the working tree does **not** remove them from history. Any claim of safety that
  ignores this is false, and this task must not make one. **Addressed:** recorded as the first thing a
  reader sees in §6.

## Progress log

### 2026-10-24 — dead vendor assets removed (partial)

Commit `a65fcd0` removed **22 files**: `SpryAssets/` (21), including a stale nested copy at
`SpryAssets/SpryAssets/`, and `Scripts/AC_RunActiveContent.js`.

Verified after removal:

```
dotnet build → Compilación correcta. 0 Advertencias, 0 Errores
dotnet test  → 10/10 passing
```

Nothing in `src/`, `tests/`, `supabase/` or `docs/` references them.

**What this did not fix.** The credential scan still reports **17** hardcoded `mysql_connect` sites,
because they live in `admin/modulos/settings.php` and `lib/objetos/*.php` — files that were not removed.
The dump (`presenci_ccol.sql`, 51 MB) is also still present and still tracked.

So: no credential has been removed, and none has been rotated. The exposure is unchanged.

**Side effect, recorded deliberately.** Twelve legacy PHP pages still reference the removed assets, so
they no longer render as they once did. They remain readable as source, which is what the analysis needs;
nothing in the new stack depended on them.

### 2026-10-31 — removal executed (approved by the Product Owner)

The Product Owner instructed "proceed with T16". Because the removal contradicts
constitution Art. IX.1 ("never modified, moved, or deleted"), the instruction is treated as the approval
Art. XIV requires **and the constitution is amended in this same change** (v1.3.0).

| Step | Result |
|---|---|
| Inventory before removal | 1,868 tracked files in the legacy tree + 1 tracked dump; 2,143 tracked files in total |
| Credential inventory | 19 `mysql_connect` sites in 13 files → **4 credential sets** (A: `root@CCCOL` ×6, B: `root@cccol` ×6, C: `presenci_ccol` ×5, D: `pv014_admcccol@CCCOL` ×1) + one empty template. Measured, not estimated; the release audit's "17" is one lower and that is recorded |
| Archive outside Git | `C:\proyectos\convenciones-legacy-archive` — tree, dump (48.9 MB), `local/`, and a provenance README; 1,915 files, 201.3 MB |
| Removal | 6 directories + `index.html` deleted from the working tree; the dump deleted with a wildcard (literal-path access fails on this machine — recorded in the removal §1); `git add -A` staged **1,869 deletions**, `97,616` lines, nothing else |
| Rule cleanup | `.gitattributes`: `*.php` and the dump rule removed. `.gitignore`: legacy block replaced by an explicit "removed" note and the dump patterns. `README.md`, `docs/legacy/README.md`, `docs/database/legacy-schema-inventory.md`, `docs/operations/repository-settings.md` and `supabase/seed.sql` updated so no document claims the tree is present |
| Secret scan | `pwsh ./tools/scan-secrets.ps1` → **CLEAN** (exit 0); 273 tracked files at the moment of deletion, 276 in the final tree (the record, the scanner and the pre-push hook were added); `-IncludeWorkingTree` also CLEAN |
| Scanner | `tools/scan-secrets.ps1` written: five patterns, redacted output, no allowlist, exit code 1 on any match. Two test fixtures were reshaped so the repository contains no credential-shaped string at all |
| Build | `dotnet build Convenciones.slnx` → **0 warnings, 0 errors** |
| .NET tests | **59 unit + 142 integration passed** (this branch is from `main`; T13/T14 add 5 + 12 more on their branches) |
| Web | **44/44 passed** when run alone; under concurrent .NET load the login test exceeds Vitest's default 5 s timeout (4.9 s standalone). T14 raises that timeout and caps workers in its branch; this branch does not duplicate that fix |
| References | 0 new-stack references to `admin/`, `lib/`, `plantillas/` or the dump name |
| Constitution | **v1.3.0**: Art. IX.1 amended; amendment-log entry added; reason and authority recorded |

**History purge (same day, separate authorisation).** A mirror backup was taken
(`C:\proyectos\convenciones-pre-rewrite.git`, 28 refs, verified to contain the dump blob), then
`git filter-repo --invert-paths` removed the legacy paths from every ref. All branches were
force-pushed; `main` is `95004ae`; the removal commit became empty and was pruned, so the deletion now
exists only in the record. Verification after the rewrite: no object under any removed path, the dump
blob is not retrievable, no literal `mysql_connect(` call in any commit, and the pack fell from
150.9 MB to under 1 MB. Pull-request refs (#6–#26), pre-rewrite clones and the backup remain.

## What remains

1. **Rotate the four credential sets.** This is the only step that reduces the exposure; the owner for
   every row is the Product Owner. See [legacy-removal.md §4](../operations/legacy-removal.md).
2. **Remote refs and clones** — the branch history is clean; GitHub pull-request refs and pre-rewrite
   clones are not; purging them needs GitHub Support plus destroying the backup.
3. **Run the scan in CI** — `tools/scan-secrets.ps1` is ready for T15.
4. **Merge order.** This branch is based on `main` with T13 merged; T14 is unmerged and touches
   `docs/tasks/README.md`. Expect a documentation conflict in the task index when T14 merges; the
   conflict is textual (status rows), not behavioural.

## Left undone, explicitly

- **No credential has been rotated.** The repository tip is clean; the history is not.
- **The dump is not in Git, but it is still on disk** in the archive, unencrypted, with personal data.
