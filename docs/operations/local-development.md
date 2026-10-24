# Local development — running the stack

Two paths, both verified. Read the status note first: on this machine Aspire orchestrates
**containers** but not **project** resources, so the documented fallback is currently the reliable
way to run the API.

---

## 1. Status of Aspire orchestration on this machine — read this

`dotnet run` on `Convenciones.AppHost` starts successfully and provisions PostgreSQL and pgAdmin. It
does **not** launch the two project resources (`migrator`, `api`). The application model is correct —
`--publisher manifest` shows both as `project.v0` — but DCP never reaches them:

| Observation | Evidence |
|---|---|
| Model contains the resources | `--publisher manifest` lists `migrator` and `api` as `project.v0` |
| Containers start | `postgres-…` and `pgadmin-…` reach `Running` on every run |
| Project resources stall or never reconcile | CLI log: `Resource api/… changed state: Starting -> Waiting`, then nothing |
| DCP connection breaks | CLI log: `unable to decode an event from the watch stream: http2: response body closed` |

Attempts already made: enabled `AspireUseCliBundle` (it removed the ASPIRE010 warning and the
Aspire CLI now participates), and killed seven stale `dcp` processes left by earlier runs. The
behaviour is reproducible across five runs, so it is an environment issue rather than a project one.

**Consequence:** the API and migrator are run directly (§3) until this is resolved. Everything they
need — schema application, health probes, logging, exception handling — is verified by the test
suite, which hosts the real pipeline.

**To resolve:** install the Aspire CLI (`dotnet tool install --global Aspire.Cli`) and run
`aspire run` from the repository root, then confirm both project resources reach `Running`. Record
the outcome here.

---

## 2. Prerequisites

| Tool | Version used |
|---|---|
| .NET SDK | 10.0.401 (pinned in `global.json`) |
| Docker | running, Linux containers |
| Node.js | 22.19.0 (for the SPA, added in a later task) |

No credential is required for local development. The fallback database uses trust authentication on a
disposable container; Aspire generates and stores its own password in the AppHost user-secrets.

---

## 3. Verified path — run the API directly against a local PostgreSQL

### 3.1 Start a disposable database

```powershell
docker run --rm -d --name convenciones-dev `
  -e POSTGRES_HOST_AUTH_METHOD=trust `
  -e POSTGRES_DB=convenciones `
  -p 55432:5432 postgres:17-alpine
```

Trust authentication is deliberate: the container is disposable, holds no real data, and this keeps
any password out of the repository, scripts and shell history.

### 3.2 Point the tools at it

```powershell
# Trust authentication: the container requires no password, so none appears here.
$env:ConnectionStrings__convenciones = 'Host=localhost;Port=55432;Database=convenciones;Username=postgres'
$env:MIGRATIONS_PATH = (Resolve-Path ./supabase/migrations).Path
```

The API reads `ConnectionStrings:convenciones`. It has **no default** and fails fast at startup when
the value is missing, so a misconfigured host never serves requests it cannot fulfil.

### 3.3 Apply the schema and reference data

```powershell
dotnet run --project src/Convenciones.Migrator
```

Expected output: `Applied 11 migration(s) and the reference seed.` Exit code `0`. Each migration runs
in its own transaction and a failure rolls back and exits non-zero.

### 3.4 Start the API

```powershell
dotnet run --project src/Convenciones.Api
```

Then probe it:

```powershell
curl.exe -k https://localhost:<port>/health/live    # 200 — process is up
curl.exe -k https://localhost:<port>/health/ready   # 200 — database reachable AND schema present
```

`/health/ready` returns **503** when PostgreSQL is unreachable or the `app` schema is missing. That
distinction is intentional: a live process with an unprepared database must not receive traffic.

### 3.5 Tear down

```powershell
docker rm -f convenciones-dev
```

---

## 4. Aspire path (containers verified, project resources not)

```powershell
dotnet run --project src/Convenciones.AppHost
```

Opens the Aspire dashboard (URL printed to the console, including a login token) and provisions:

| Resource | Verified |
|---|---|
| `postgres` (postgres:17-alpine) | reaches `Running` |
| `pgadmin` | reaches `Running` |
| `migrator` | **does not start — see §1** |
| `api` | **does not start — see §1** |

The AppHost declares `migrator` with `WaitFor(database)` and the API with `WaitForCompletion(migrator)`,
so once §1 is resolved the API will only start against a fully migrated database.

The generated database password is retrievable with:

```powershell
dotnet user-secrets list --project src/Convenciones.AppHost
```

---

## 5. Running the tests

```powershell
dotnet build Convenciones.slnx
dotnet test  Convenciones.slnx --no-build
```

The integration tests start their own `postgres:17-alpine` container, apply the **same** migration
files through the **same** runner the migrator uses, and host the real API pipeline. No credential is
configured anywhere: Testcontainers generates one for the container's lifetime.

Expected: **9 tests pass** — 3 unit (migration ordering and naming), 6 integration (2 health, 4 schema).

---

## 6. Troubleshooting

| Symptom | Cause | Action |
|---|---|---|
| API exits immediately with `Connection string 'convenciones' is not configured` | Missing `ConnectionStrings__convenciones` | Set it as in §3.2 |
| `/health/ready` returns 503 with "app schema is missing" | Migrations not applied | Run §3.3 |
| Migrator exits `2` | `ConnectionStrings__convenciones` or `MIGRATIONS_PATH` unset | Set both as in §3.2 |
| Migrator exits `1` | A migration failed and was rolled back; the message names the file | Fix or restore the database |
| Aspire starts but no project resources | §1 | Use §3 |
| Integration tests hang on first run | Pulling `postgres:17-alpine` | Pre-pull with `docker pull postgres:17-alpine` |
