# Deployment — staging on DigitalOcean and Supabase

**Status:** implemented and live — `https://138-197-15-40.sslip.io` (first deployed 2026-10-31;
HTTPS since 2026-11-01; last deploy 2026-11-14 at `97a2cac`). Work merged to `main` after that commit is
**not** on staging until the workflow is dispatched again.
**Environment:** exactly one, `staging`. Continuous integration is separate and never deploys.
**Public address:** `https://138-197-15-40.sslip.io` — Caddy terminates TLS; port 80 answers with a 308
redirect.
**AI worker:** `Worker__Enabled=false` by default. A scoped window is enabled only through the
`worker-enable` stage, with an allowlist, a UTC deadline and an independent watchdog; see the
[staging AI runbook](staging-ai-runbook.md).

---

## 1. What runs where

```
                    Internet
              :80 and :443 (published)
                ┌──────▼───────┐
                │    Caddy     │  TLS termination, HTTP → HTTPS redirect
                └──────┬───────┘
                       │ internal docker network
                ┌──────▼───────┐
                │   web (nginx)│  serves the SPA, proxies /api/ and /health/
                └──────┬───────┘
                       │ internal docker network
                ┌──────▼───────┐
                │  api (.NET)  │  :8080, never published
                └──────┬───────┘
                       │ TLS, IPv4 session pooler
                ┌──────▼───────────────────────┐
                │ Supabase: PostgreSQL, Auth,  │
                │ Storage (private bucket)     │
                └──────────────────────────────┘
```

| Piece | Implementation |
|---|---|
| Host | One DigitalOcean Droplet, Ubuntu 24.04, Docker Engine + Compose plugin |
| TLS termination | Caddy in front of nginx, automatic Let's Encrypt certificate for the `sslip.io` hostname |
| Reverse proxy | nginx, inside the `web` image (`deploy/nginx.conf`) |
| API | `Dockerfile` image (API + migrator), non-root (`uid 1654`), read-only root filesystem |
| Database | Supabase PostgreSQL via the **session pooler** (IPv4, port 5432) |
| Auth | Supabase Auth; the API validates JWTs through the project OIDC discovery document (which points at the JWKS) |
| Storage | Supabase Storage, private bucket `documents`, server-side secret key only |
| Images | GHCR, tagged with the commit SHA, pulled by the droplet |
| Migrations | `Convenciones.Migrator` inside the API image, run as a one-shot service before `up` |

The API is addressed through `/api`; `Api__PathBase=/api` makes the API strip the prefix, so no route
changed and the SPA keeps `/documents`, `/agreements`, … as its own client-side routes.

## 2. Configuration

Everything is injected at deploy time; nothing is baked into an image and nothing is committed.

### GitHub Environment `staging` — secrets

| Name | Purpose | Where it comes from | Consumer |
|---|---|---|---|
| `SUPABASE_DB_PASSWORD` | Database authentication | The password saved when the Supabase project was created | API and migrator (`Database__Password`) |
| `SUPABASE_SECRET_KEY` | Server-side Storage access (bypasses storage RLS) | Supabase → Settings → API Keys → Secret keys | API (`Documents__SupabaseSecretKey`) |
| `SSH_PRIVATE_KEY` | Deploy key | Generated for this deployment, stored outside the repository | Deploy workflow |
| `SSH_KNOWN_HOSTS` | Host-key pinning for SSH | `ssh-keyscan -H <ip>` after the droplet exists | Deploy workflow |

### GitHub Environment `staging` — variables

| Name | Value |
|---|---|
| `SUPABASE_URL` | `https://<project-ref>.supabase.co` |
| `SUPABASE_PUBLISHABLE_KEY` | Publishable (browser) key |
| `SUPABASE_DB_HOST` | `aws-0-us-east-1.pooler.supabase.com` |
| `SUPABASE_DB_PORT` | `5432` |
| `SUPABASE_DB_NAME` | `postgres` |
| `SUPABASE_DB_USER` | `postgres.<project-ref>` |
| `SSH_HOST` | Droplet public IPv4 |
| `SSH_USER` | Deployment user on the droplet |

The database password is passed as a separate value and assembled into the Npgsql connection string
inside the process (`NpgsqlConnectionStringBuilder`), so an env-file quoting mistake cannot corrupt it
or leak it into a log.

## 3. Deployment flow (`.github/workflows/deploy.yml`)

1. Dispatched manually (`workflow_dispatch` with `confirm=deploy`) after the PR is merged and its CI is
   green. A merge to `main` never deploys by itself.
2. Builds and pushes the API image and the web image to GHCR, tagged with the commit SHA.
3. Writes `.env` from the environment's secrets and variables.
4. Copies `.env` (mode 600) and `docker-compose.yml` to `/opt/convenciones` on the droplet, keeping the
   previous `.env` for rollback.
5. Logs the droplet into GHCR with the job's short-lived token.
6. Runs the migrator (`docker compose run --rm --no-deps migrator`), then `docker compose up -d`.
7. Waits for `http://127.0.0.1/health/ready` on the droplet and then for the same endpoint over the
   public HTTPS address.
8. On any failure, restores the previous `.env` and brings the previous images back up.

### 3.1 Migrations are tracked now

The migrator records every file it applies in `public.schema_migration` and skips what is already
there. This was added after the first deployment proved the original assumption wrong: the runner
replayed every file, so a second deploy would have failed on `create table` for a schema that already
existed.

The already-deployed database was seeded once with the 19 files it had applied, so the next deploy
applies only what is new. A database created from scratch needs no seeding: the table is created on the
first run.

### 3.2 Role membership

PostgreSQL 16 grants the *creator* of a role `ADMIN` but not `SET`, and the API's per-request
`set role app_api` / `set role anon` requires `SET`. Migration `20260108000000_api_role_membership.sql`
grants it explicitly for `app_api`, `anon` and `authenticated`; without it the first deployment
answered `42501 permission denied to set role "app_api"`.

## 4. First run on a fresh environment

1. **Bucket.** Supabase → Storage → New bucket → name `documents`, **private** (no public access).
2. **First user.** Supabase → Authentication → Users → Add user: the SuperAdmin's email and a password
   kept in a password manager. Copy the user's UID — that UUID is the `subject` the API knows.
3. **Bootstrap.** Open `https://138-197-15-40.sslip.io/setup`, sign in with that user, and complete the
   one-time setup. The bootstrap is inert once a SuperAdmin exists.
4. **Sign in** at `https://138-197-15-40.sslip.io/login` and verify the first screen.
5. **Further users** are created inside the application by an administrator, each linked to a Supabase
   Auth user created the same way (dashboard → Add user → provision with its UID). Sign-ups stay
   disabled.

## 5. Verification (what "deployed" means)

| Check | How |
|---|---|
| API process is up | `docker compose ps`, `/health/live` |
| Database reachable and schema applied | `/health/ready` returns 200; it checks `app` schema presence |
| Migrations applied | The deploy job fails if the migrator exits non-zero |
| Web served | `curl -I https://138-197-15-40.sslip.io/` returns 200 with `text/html` |
| TLS | Certificate issued by Let's Encrypt for `138-197-15-40.sslip.io`; port 80 answers 308 to HTTPS |
| Auth works | Sign in through the SPA; `GET /api/me` returns the profile |
| Uploads work | Upload a PDF; object appears in the `documents` bucket |
| Tenant isolation | Company A user cannot read company B documents or agreements |
| No secrets in the bundle | `pnpm build` fails on a service-role marker; verified again in CI |
| No secrets in logs | The workflow never echoes secret values; `docker compose config` is not printed |
| Container restart | `sudo reboot`; `docker compose ps` shows both services up (restart policy) |
| Firewall | 22 and 80 opened by the bootstrap script; Docker publishes 443 (host UFW rule still pending). 8080 and 5432 closed |

## 6. Rollback

Automatic: a failed deploy restores the previous `.env` and starts the previous images.

Manual:

```bash
ssh <user>@<ip>
cd /opt/convenciones
cp .env.previous .env        # the previous commit's images
docker compose up -d --remove-orphans
curl -fsS http://127.0.0.1/health/ready
```

The previous images stay on the droplet until `docker image prune`. Migrations are forward-only; a
rollback restores the binary, not the schema, so a schema-changing release needs its own reverse
migration (none in this MVP).

## 6.1 Verified on 2026-10-31 (HTTP)

| Check | Result |
|---|---|
| `/health/ready` over the public IP | 200 |
| SPA | 200, `<title>Convenciones</title>` |
| Sign-in (real user) | works; `/api/me` 200 with capabilities |
| Upload + extraction | 27,020-byte PDF stored in `documents`, extraction `Completed`, 470 characters |
| Cross-tenant read | **404** on another company's document and text; own list empty |
| Migration history | 20 applied and recorded |
| Firewall | 22 and 80 open; 8080 and 5432 closed |
| Reboot | containers healthy ~50 s after `systemctl reboot` |
| Bundle | no `service_role`, no `sb_secret_` token, no database URL |

**First-run gap:** there is no company-creation screen yet. On staging, the first company
(`Empresa Demo`) and the SuperAdmin's membership were inserted directly in the database as a
documented step: an `app.company` row plus `platform.user_profile.company_id`. A UI for this belongs
to a later increment; until then, a new environment needs the same one-time SQL.

## 6.2 HTTPS and worker-off verification (2026-11-01)

Source: [https-staging-results.md](https-staging-results.md) — deploy run `41325213422`, smoke run
`41325470122`.

| Check | Result |
|---|---|
| DNS | `A 138-197-15-40.sslip.io → 138.197.15.40` |
| Ports | 80 and 443 open; Caddy terminates TLS; HTTP answers `308` |
| Certificate | `CN=138-197-15-40.sslip.io`, issuer Let's Encrypt, expires 2027-01-30 |
| `/health/ready` over HTTPS | `Healthy` |
| Worker | `Worker__Enabled=false`; the container stays healthy while disabled |
| Dotfiles | `https://…/.env` returns **403** |
| Config names | present in the API container (values never printed) |
| Secrets | none printed in logs or responses |

This verification did **not** process a document, create the two staging tenants or enable the worker.
Scoped worker windows were used later for the synthetic F-06.5 baseline (7 documents, 23 pages,
$0.041, worker disabled after each run); the two-tenant isolation check and the kill-switch drill
remain open — see [pending owner sign-offs](owner-signoffs.md).

## 7. Known limitations

1. **No domain of our own.** Staging is public at `https://138-197-15-40.sslip.io`, a name a wildcard
   DNS service resolves to the droplet's IP; Let's Encrypt issues the certificate for that name. There
   is no custom domain, so changing the IP changes the public hostname. A custom domain is a deployment
   decision, not a code change.
2. **Free Supabase tier pauses idle projects.** After about a week without use the project pauses and
   the API returns 503 until it is resumed from the dashboard. Upgrade to Pro ($25/mo) to remove this.
3. **Single droplet.** No redundancy; a reboot takes the service down for seconds. Backups exist only
   in Supabase (Pro plan).
4. **Tenant isolation is enforced by RLS** through `SET ROLE app_api`, not by a separate database user
   per tenant. The connection user is `postgres` (member of `app_api` and `anon`), and every request
   switches role; a code path that opened a connection without the interceptor would bypass RLS.

## 8. Monthly cost

| Item | Cost |
|---|---|
| DigitalOcean Droplet 1 vCPU / 2 GB | **$12** |
| Supabase Free | **$0** (Pro: $25) |
| GitHub Actions (private repo, 2,000 min/month) | **$0** |
| Domain | none (`sslip.io` subdomain; certificate via Let's Encrypt) |
| **Total** | **$12/month** (or $37 with Supabase Pro) |
