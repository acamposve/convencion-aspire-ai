# T18 — Deployment (staging)

**Status:** **implemented and live** — deployed 2026-10-31 to `http://138.197.15.40`.
**Depends on:** T3, T7, T15 · **Blocks:** T19

## 1. What was decided

The original task asked for five product decisions. The Product Owner chose:

| # | Decision | Value |
|---|---|---|
| D1 | Where the API runs | **DigitalOcean Droplet**, 1 vCPU / 2 GB / 50 GB, Ubuntu 24.04, **NYC** |
| D2 | Environments | **One**: `staging`. CI is separate and never deploys |
| D3 | Supabase plan | **Free** (500 MB database, 1 GB storage; pauses when idle) |
| D4 | Domain and DNS | **None**: the application is served from the droplet's IPv4, `http://138.197.15.40` |
| D5 | `service_role` holder | The GitHub `staging` environment; the API reads it as `Documents__SupabaseSecretKey` |

**Cost:** $12/month (droplet) + $0 (Supabase Free). Pro would add $25/month.

## 2. Architecture

```
Internet ──:80──► web (nginx) ──/api/──► api (.NET, :8080, not published)
                        └── /health/* ──► api
api ──TLS, IPv4 session pooler──► Supabase PostgreSQL
api ──service key, server-side──► Supabase Storage (private bucket `documents`)
SPA ──publishable key──► Supabase Auth (ES256 tokens; API validates via OIDC discovery)
```

Images live in GHCR, tagged with the commit SHA. `main` deploys through
`.github/workflows/deploy.yml`: build → push → write `.env` → migrate → `up -d` → verify readiness on
the droplet and over the public address → roll back to the previous images on failure.

## 3. Deliverables

| File | Purpose |
|---|---|
| `Dockerfile` | API + migrator + SQL migrations in one image; non-root (uid 1654); read-only root filesystem |
| `Dockerfile.web` | SPA built with the publishable key only, served by nginx |
| `deploy/docker-compose.yml` | One published port; API internal; health-gated web; migrator as a one-shot service |
| `deploy/nginx.conf` | SPA fallback, `/api/` proxy, 25 MB body limit, security headers |
| `deploy/bootstrap-droplet.sh` | One-time droplet provisioning: Docker, deploy user, ufw 22/80, key-only SSH |
| `deploy/.env.example` | Every deployment key, no values |
| `.github/workflows/deploy.yml` | Build, push, migrate, deploy, verify, roll back |
| `.dockerignore` | Small context; no secrets, no tests, no docs |
| `docs/operations/deployment.md` | Architecture, secrets inventory, procedures, rollback, limitations |
| `supabase/migrations/20260108000000_api_role_membership.sql` | `SET` membership the API's role switch needs |

Application changes the deployment required: `Api__PathBase=/api`, database assembled from
`Database__*` parts (base64 password or mounted file), and `SupabaseDocumentStorage`.

## 4. Acceptance — executed, not asserted

| # | Criterion | Evidence |
|---|---|---|
| 1 | API starts | `/health/live` 200; container `Up (healthy)` |
| 2 | Frontend on the IP | `GET /` 200, `<title>Convenciones</title>` |
| 3 | API ↔ Supabase | `/health/ready` 200; EF queries against `app.agreement` in the logs |
| 4 | Migrations execute | 20 files applied and recorded in `public.schema_migration`; 32 tables, 164 RLS policies, 23 capabilities |
| 5 | Supabase Auth works | Real sign-in through the SPA; `/api/me` 200; SuperAdmin bootstrapped with `{"configured":true}` |
| 6 | Uploads work | PDF `apgetpdfdatoscita_impl.pdf` (27,020 bytes) stored at `documents/5c47…/2026/10/….pdf`, row in `platform.document` with `extraction_status = Completed`, 470 extracted characters |
| 7 | Cross-tenant access denied | Second company/user over HTTPS: `GET /api/documents/{A-id}` **404**, `/text` **404**, own list **0**; probe data deleted afterwards |
| 8 | No secret in the bundle | Bundle contains no `service_role`, no `sb_secret_` token (only the library's `startsWith("sb_secret_")` literal), no database URL |
| 9 | No secret in Git/logs | `tools/scan-secrets.ps1` CLEAN; deployment logs contain no values |
| 10 | Health endpoint | `/health/ready` 200 over the public IP |
| 11 | Restart after reboot | `systemctl reboot`; containers came back healthy in ~50 s |
| 12 | Firewall | 22 and 80 reachable; **8080 and 5432 closed** |
| 13 | Rollback | Automatic on failure (previous `.env` restored); manual procedure documented; not exercised in production yet |

## 5. Defects found only by deploying

1. **Compose `command` does not replace `ENTRYPOINT`** — the migrator executed the API binary. Fixed with `entrypoint`.
2. **A 600 bind-mounted password is unreadable by uid 1654** — the password now travels base64-encoded and is decoded in-process.
3. **`set role "app_api"` → 42501** — PostgreSQL 16 gives the role creator `ADMIN` but not `SET`; new migration grants it.
4. **The migrator replayed every file** — it now records history in `public.schema_migration`; the live database was seeded once with the 19 files it had applied.
5. **JWT validation failed with "signature key not found"** — `Jwt:MetadataAddress` must point at the OIDC **discovery document**, not the JWKS.
6. **Selecting a PDF uploaded nothing** — the control cleared `input.value` before reading the `FileList`, which empties it in Chromium.

Each one has its own fix and evidence in the PRs (#32–#36).

## 6. Known limitations

1. **No TLS.** Public CAs do not certify a bare IP. Staging is HTTP; tokens and passwords travel unencrypted. A domain plus Let's Encrypt is the fix, and nginx is ready for it.
2. **Free Supabase pauses idle projects**; the API returns 503 until it is resumed.
3. **No company-creation screen.** The first company (`Empresa Demo`) and the SuperAdmin's membership were inserted directly in the database as a documented staging step. A UI belongs to a later increment.
4. **Single droplet**, no redundancy; backups only in Supabase.
5. The API connects as `postgres` (member of `app_api`/`anon`) and switches role per request; a connection opened without the interceptor would bypass RLS.

## 7. Operations

- **Deploy:** merge to `main`; the workflow does the rest.
- **Rollback:** automatic; manual procedure in [deployment.md](../operations/deployment.md) §6.
- **Secrets:** GitHub environment `staging` only; never in the repository, the image or the bundle.
- **Logs:** `docker compose logs api` on the droplet.
- **Health:** `http://138.197.15.40/health/ready`.

## 8. What T18 does not claim

The credentials of T16 were not rotated and do not need to be: their infrastructure was decommissioned
(the Product Owner's decision, recorded in T16). Nothing here revives them.
