# T18 — Production readiness and the first controlled migration

**Status:** planned · **Depends on:** T14, T15 · **Blocks:** release · **Requires:** your explicit approval for any Supabase action

## Objective

Everything between "the MVP works on this machine" and "the MVP can be entrusted with real data".

## Scope

**In:** deployment target and configuration for API and web; secrets management (no secret in the repo,
no secret in an image); Supabase project provisioning **with your approval**; Storage bucket policy review;
applying the migrations to Supabase through the migrator, once, observed; backup and restore plan with a
rehearsed restore; rollback plan for a bad migration; health-check network exposure review; a smoke test
that a release must pass.

**Out:** deployment performed without authorisation; any destructive action on a remote database.

## Deliverables

| File | Purpose |
|---|---|
| `docs/operations/production-runbook.md` | Deploy, configure, restore, roll back |
| `docs/operations/smoke-test.md` | The release gate checklist |
| CI/deploy configuration | As approved |

## Acceptance criteria

1. A restore from backup is **rehearsed and timed**, with the result recorded — an untested backup is not
   a backup.
2. The migrations run against the target database and the result is verified by query, not by the exit
   code alone.
3. No environment contains a credential that appears in Git history.
4. `/health/ready` returns 503 when the database is unreachable, tested in the deployed environment.
5. The smoke test covers: sign in, upload, process, read text, author a clause, publish, compare — as a
   signed-in user, and the public portal anonymously.
6. Rollback instructions are written for a failed migration **and** for a failed previous release.

## Risks

- This is the first task that touches a remote system. Every action needs approval first, and the
  database is the one asset that cannot be recreated from this repository if it holds real data.
- Health endpoints disclose whether the schema is present; in production they must be network-restricted
  (audit M4).
