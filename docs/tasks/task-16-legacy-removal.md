# T16 — Legacy tree removal and credential rotation

**Status:** planned · **Depends on:** nothing · **Requires:** your approval before executing

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

| File | Purpose |
|---|---|
| `docs/operations/legacy-removal.md` | What is removed, what is kept and why, rotation checklist |
| Repository change | Legacy tree and dump removed (or moved out of Git) after approval |

## Acceptance criteria

1. A secret scan across the whole repository returns **zero** credential-shaped matches — proven by
   running it, not asserted.
2. Every behaviour the new system depends on is traceable to `docs/legacy/*`; nothing is lost by removing
   the code, and the mapping is verified by grepping for the rule IDs (BR-xx, DS-xx, S-xx).
3. The rotation checklist names each credential, the system it belongs to, and who must rotate it.
4. **Deleting files is not described as making credentials safe** — the record must say the secrets
   remain compromised until rotated.
5. Git history is addressed explicitly: the credentials exist in past commits and remain retrievable
   unless the history is rewritten, which is a separate decision.

## Risks

- Removing the dump is irreversible if the analysis was incomplete. The analysis docs are the insurance,
  so they must be verified complete first.
- Removing files from the working tree does **not** remove them from history. Any claim of safety that
  ignores this is false, and this task must not make one.

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

