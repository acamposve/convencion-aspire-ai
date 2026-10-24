# Repository settings â€” required configuration and current status

Owner: Product Owner. Constitution Art. VIII.1 makes `main` protection an enforcement obligation,
not an intention.

---

## 1. Required settings on `main`

| Setting | Value | Why |
|---|---|---|
| Require a pull request before merging | **on** | No change reaches `main` directly (Art. VIII.1) |
| Required approvals | **0** | The repository has one human reviewer, who cannot approve their own PR. A non-zero count would deadlock every change. Revisit if a second reviewer joins |
| Block force pushes | **on** | History on `main` is never rewritten (Art. VIII.6) |
| Block branch deletion | **on** | `main` cannot be removed |
| Bypass actors | **Product Owner only** | "My user is the only one that can override and merge" |
| Required status checks | **not yet** | There is no CI workflow to require. Add when the first pipeline exists (Art. VII.3) |

The agent never merges, and never bypasses.

---

## 2. Status: **blocked by the hosting plan** (recorded 2026-10-24)

Both GitHub mechanisms answer **HTTP 403** on this repository:

```
GET  /repos/acamposve/convencion-aspire-ai/branches/main/protection
POST /repos/acamposve/convencion-aspire-ai/rulesets
â†’ { "message": "Upgrade to GitHub Pro or make this repository public to enable this feature.",
    "status": 403 }
```

Branch protection and repository rulesets are not available for **private repositories on the
GitHub Free plan**. The repository is private and must stay private: it still contains the legacy
PHP tree and `presenci_cccol.sql`, which holds personal data of identifiable individuals. Making it
public to obtain a feature would be a serious data exposure, so it is not an option.

**Consequence:** the rule is currently enforced by a committed **pre-push hook** instead
(Â§3). This satisfies the Art. VIII.1 fallback: *a hosting limitation changes where the rule is
enforced; it never relaxes the rule.*

---

## 3. Control in force today: the local pre-push hook

Committed at [`.githooks/pre-push`](../.githooks/pre-push), installed by
[`tools/install-git-hooks.ps1`](../tools/install-git-hooks.ps1):

```powershell
powershell -ExecutionPolicy Bypass -File ./tools/install-git-hooks.ps1
```

It refuses any push whose destination is `refs/heads/main`, prints the PR workflow, and offers the
Product Owner an explicit override:

```sh
CONVENCIONES_ALLOW_MAIN_PUSH=1 git push origin main
```

**Honest assessment of this control.** It runs on the developer machine, so it is bypassable with
`git push --no-verify` or by clearing `core.hooksPath`. It prevents accidental direct pushes â€” the
realistic failure mode for an agent or a distracted human â€” and it does not prevent a determined
one. It is a stopgap, not the enforcement the constitution asks for.

---

## 4. How to close the gap (Product Owner action)

Either condition unblocks the hosting-level ruleset:

1. **Upgrade the account to GitHub Pro** (the repository stays private â€” recommended), or
2. Make the repository public **after** the legacy tree and dump are removed from the repository and
   from history. That is a separate, deliberate decision and is not recommended before the legacy
   tree is retired.

### Applying the ruleset once unblocked

```bash
gh api --method POST repos/acamposve/convencion-aspire-ai/rulesets --input - <<'JSON'
{
  "name": "protect-main",
  "target": "branch",
  "enforcement": "active",
  "conditions": {
    "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] }
  },
  "rules": [
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": false,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false
      }
    },
    { "type": "non_fast_forward" },
    { "type": "deletion" }
  ],
  "bypass_actors": [
    { "actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always" }
  ]
}
JSON
```

`actor_id: 5` selects the **repository admin** role, which is the Product Owner's account. This is
the value documented for `RepositoryRole` in the rulesets API at the time of writing; confirm it in
*Settings â†’ Rules â†’ Rulesets* after applying, because this payload could not be executed against
this repository to verify it (Â§2).

Verify afterwards:

```bash
gh api repos/acamposve/convencion-aspire-ai/rulesets --jq '.[].name'
git checkout main && git commit --allow-empty -m probe && git push origin main   # must be refused
```

---

## 5. What the agent does regardless of enforcement

These hold whether or not the platform can enforce them:

1. Never commits to `main`.
2. Never pushes to `main`; pushes only the task branch.
3. Never merges a pull request, including its own.
4. Never force-pushes, amends shared history, or deletes a branch on the remote.
5. One task, one branch, one PR (Art. VIII.2â€“3).

