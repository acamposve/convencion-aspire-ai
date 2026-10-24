# T15 — CI pipeline

**Status:** planned · **Depends on:** T2 (first real tests) · **Blocks:** nothing, but every later task benefits

## Objective

Make the tests run when nobody is watching. The release audit found **zero** workflows: everything that
passed, passed because a human ran it.

## Scope

**In:** GitHub Actions workflow on pull requests and `main`: restore, build with warnings as errors, run
`.NET` tests (including the PostgreSQL-backed integration tests via a service container), validate the
migration set, and — once T7 lands — build the frontend and check the bundle for service-key leakage.
Fail the job on any warning or test failure.

**Out:** deployment, releases, provisioning Supabase (needs approval).

## Deliverables

| File | Purpose |
|---|---|
| `.github/workflows/ci.yml` | Build, test, frontend |
| `docs/operations/ci.md` | What runs, how to reproduce locally |

## Acceptance criteria

1. A pull request runs build + tests and reports a check; breaking a test makes the check red.
2. Integration tests run against a real PostgreSQL in CI, not a substitute.
3. The workflow fails on a build **warning** (`TreatWarningsAsErrors` is already on; prove the gate).
4. Total runtime is bounded and reported.
5. The workflow is documented well enough that a newcomer can run the same commands locally.

## Risks

- Branch protection is **unavailable** on a private repo on the Free plan (HTTP 403 — see
  `repository-settings.md`). CI can report status but cannot be *required*. Until the plan changes, the
  local `pre-push` hook is the only enforcement, and that must be stated, not glossed.
