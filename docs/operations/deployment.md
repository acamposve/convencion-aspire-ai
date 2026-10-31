# Deployment — staging on DigitalOcean and Supabase

**Status:** implemented; first deployment pending the droplet (Phase 4).
**Environment:** exactly one, `staging`. Continuous integration is separate and never deploys.
**Public address:** `http://<droplet-ip>` (no domain; see the HTTPS limitation in §7).

---

## 1. What runs where

```
                    Internet
                       │  :80 (only published port)
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
| Reverse proxy | nginx, inside the `web` image (`deploy/nginx.conf`) |
| API | `Dockerfile` image (API + migrator), non-root (`uid 1654`), read-only root filesystem |
| Database | Supabase PostgreSQL via the **session pooler** (IPv4, port 5432) |
| Auth | Supabase Auth; the API validates JWTs against the project JWKS |
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

1. Triggered by a push to `main` (i.e. after the Product Owner merges a PR whose CI is green) or
   manually.
2. Builds and pushes the API image and the web image to GHCR, tagged with the commit SHA.
3. Writes `.env` from the environment's secrets and variables.
4. Copies `.env` (mode 600) and `docker-compose.yml` to `/opt/convenciones` on the droplet, keeping the
   previous `.env` for rollback.
5. Logs the droplet into GHCR with the job's short-lived token.
6. Runs the migrator (`docker compose run --rm --no-deps migrator`), then `docker compose up -d`.
7. Waits for `http://127.0.0.1/health/ready` on the droplet and then for the same endpoint over the
   public address.
8. On any failure, restores the previous `.env` and brings the previous images back up.

## 4. First run on a fresh environment

1. **Bucket.** Supabase → Storage → New bucket → name `documents`, **private** (no public access).
2. **First user.** Supabase → Authentication → Users → Add user: the SuperAdmin's email and a password
   kept in a password manager. Copy the user's UID — that UUID is the `subject` the API knows.
3. **Bootstrap.** Open `http://<ip>/setup`, sign in with that user, and complete the one-time setup.
   The bootstrap is inert once a SuperAdmin exists.
4. **Sign in** at `http://<ip>/login` and verify the first screen.
5. **Further users** are created inside the application by an administrator, each linked to a Supabase
   Auth user created the same way (dashboard → Add user → provision with its UID). Sign-ups stay
   disabled.

## 5. Verification (what "deployed" means)

| Check | How |
|---|---|
| API process is up | `docker compose ps`, `/health/live` |
| Database reachable and schema applied | `/health/ready` returns 200; it checks `app` schema presence |
| Migrations applied | The deploy job fails if the migrator exits non-zero |
| Web served | `curl -I http://<ip>/` returns 200 with `text/html` |
| Auth works | Sign in through the SPA; `GET /api/me` returns the profile |
| Uploads work | Upload a PDF; object appears in the `documents` bucket |
| Tenant isolation | Company A user cannot read company B documents or agreements |
| No secrets in the bundle | `pnpm build` fails on a service-role marker; verified again in CI |
| No secrets in logs | The workflow never echoes secret values; `docker compose config` is not printed |
| Container restart | `sudo reboot`; `docker compose ps` shows both services up (restart policy) |
| Firewall | Only 22 (or the chosen SSH port) and 80 are reachable from outside |

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

## 7. Known limitations

1. **No TLS.** Public CAs do not issue certificates for a bare IP address, so staging is served over
   HTTP. Tokens and passwords travel unencrypted. Acceptable only for staging with synthetic data.
   The fix is a domain plus Let's Encrypt (~10 minutes once a domain exists); nginx is ready for it.
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
| Domain | none (not used) |
| **Total** | **$12/month** (or $37 with Supabase Pro) |
