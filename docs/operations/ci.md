# CI — what runs and how to reproduce it

The gate is [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml). It runs on every pull
request and on every push to `main`, with read-only repository permissions.

## Jobs

| Job | What it proves | Approximate runtime |
|---|---|---|
| **Build and test (.NET)** | The solution compiles with warnings as errors, and the unit + integration suites pass. The integration tests use Testcontainers, so they run against a real PostgreSQL 17, not a substitute | 3–5 min |
| **Apply migrations to PostgreSQL 17** | Every `supabase/migrations/*.sql` applies in order to an empty database and the seed loads; the schema and RLS policies are then queried | 1–2 min |
| **Typecheck, test and build (web)** | `tsc` is clean, the component suite passes, and the production build succeeds — including the bundle scan that fails if a service-role key, database URL or other server-only marker reaches the browser bundle | 2–3 min |
| **Secret scan** | `tools/scan-secrets.ps1` finds no credential-shaped string in the tracked files | < 1 min |

## Reproduce locally

```bash
# .NET
dotnet restore Convenciones.slnx
dotnet build Convenciones.slnx --configuration Release -warnaserror
dotnet test  Convenciones.slnx --no-build --configuration Release

# Migrations (any PostgreSQL 17; the suite uses a disposable container)
ConnectionStrings__convenciones="Host=localhost;Port=5432;Database=convenciones;Username=postgres;Password=postgres" \
MIGRATIONS_PATH="$PWD/supabase/migrations" \
dotnet run --project src/Convenciones.Migrator --configuration Release

# Web
cd src/Convenciones.Web
pnpm install --frozen-lockfile
pnpm typecheck
pnpm test
pnpm build

# Secrets
pwsh ./tools/scan-secrets.ps1
```

## What CI cannot enforce

Branch protection and repository rulesets are **unavailable** for private repositories on the
GitHub Free plan (HTTP 403 — see [repository-settings.md](repository-settings.md)). CI reports a
status on every pull request, but the status **cannot be required** until the plan changes. Until
then the committed `pre-push` hook is the only enforcement of "no direct push to `main`", and this
limitation is stated rather than glossed over (constitution Art. VII.3 and Art. VIII.1).

## Deliberate choices

- **Docker on the runner.** Testcontainers needs it; GitHub-hosted Ubuntu runners provide it, which
  is why the integration suite runs for real instead of being skipped.
- **A service container for the migration job.** It applies the same SQL the deployed migrator will
  apply, on the same PostgreSQL major version, so a migration that only works in the test harness is
  caught here.
- **The web suite caps its worker pool at four and allows 20 seconds per test.** The heavier screens
  render the whole application; without the cap the run becomes a timing coin flip on a loaded
  machine. This is a stability setting, not a relaxation of any assertion.
- **No deployment here.** Deployment is T18 and lives in its own workflow.
