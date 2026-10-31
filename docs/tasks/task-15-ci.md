# T15 — CI pipeline

**Status:** implemented — PR #30 open · **Depends on:** T2 · **Blocks:** nothing directly, but T18 depends on it

## Objective

Make the tests run when nobody is watching. The release audit found **zero** workflows: everything that
passed, passed because a human ran it.

## Scope

**In:** GitHub Actions workflow on pull requests and `main`: restore, build with warnings as errors, run
`.NET` tests (including the PostgreSQL-backed integration tests via a service container), validate the
migration set, and build the frontend with the bundle check. Fail the job on any warning or test failure.

**Out:** deployment, releases, provisioning Supabase (needs approval).

## Deliverables

| File | Purpose | State |
|---|---|---|
| `.github/workflows/ci.yml` | Build, test, migrations, web, secret scan | **written** |
| `docs/operations/ci.md` | What runs, how to reproduce locally | **written** |

## Acceptance criteria — evidence

| AC | Evidence |
|---|---|
| 1. A PR runs build + tests and reports a check | Run [`41200362770`](https://github.com/acamposve/convencion-aspire-ai/actions/runs/41200362770) on PR #30: all four jobs **success** |
| 2. Integration tests run against a real PostgreSQL | The .NET job runs the Testcontainers suite (154 integration tests) on the runner's Docker; the migration job additionally applies the 19 SQL migrations to a `postgres:17` service container |
| 3. The workflow fails on a build warning | `dotnet build … -warnaserror`; the property is already on, the flag makes the gate explicit |
| 4. Total runtime is bounded and reported | 3.0 min .NET, 1.3 min web, 0.6 min migrations, 0.2 min scan — **~5.1 min** wall clock, `timeout-minutes` set per job |
| 5. Documented so a newcomer can reproduce it | [ci.md](../operations/ci.md) lists every command |

## Risks

- Branch protection is **unavailable** on a private repo on the Free plan (HTTP 403 — see
  `repository-settings.md`). CI can report status but cannot be *required*. Until the plan changes, the
  local `pre-push` hook is the only enforcement, and that is stated, not glossed.

## Progress log

### 2026-10-31 — implemented and run

The workflow has four independent jobs: `.NET` (restore, Release build with `-warnaserror`, 64 unit +
154 integration), migrations (19 files applied to PostgreSQL 17, seed loaded, then 32 tables, 164 RLS
policies and 23 capabilities verified by query), web (typecheck, 59 tests, production build with the
bundle scan) and the secret scan.

Two problems were found by running it, not by reading it:

1. **The scanner failed on Linux.** `Get-Item -LiteralPath` on a bare dotfile behaved differently from
   `Test-Path`; the scan now resolves and stats through .NET, so Windows and the runner agree.
2. **The workflow contained a credential-shaped URL.** The `psql` verification used
   `postgresql://user:password@host`, and the scanner correctly refused it. It now uses the keyword DSN
   plus `PGPASSWORD`, so the scan stays at zero with no allowlist.

The vitest suite also received a 20 s per-test timeout. It was the known flake: the heaviest login tests
exceed Vitest's 5 s default on a loaded machine. The assertions are unchanged; local evidence was a green
59/59 run beside the .NET suite that had previously produced four failures.

**Verified after the fix:** run `41200362770` — .NET success, migrations success, web success, secret
scan success.
