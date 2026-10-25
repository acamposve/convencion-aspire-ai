# T18 — Deployment

**Status:** planned · **Depends on:** T3 (API + auth), T7 (web), T15 (CI) · **Blocks:** T19
**Requires:** the decisions and credentials listed below, plus your approval before touching Supabase

## Objective

Take the MVP from "works on this machine" to "works at a URL, against a real database, with real
sign-in, and with no secret in the repository".

This is the task that answers the question *"when do you need Supabase?"* — see §2.

---

## 1. What deployment actually means here

Three things ship, and they have different homes:

| Thing | What it needs |
|---|---|
| **API** (ASP.NET Core container) | A container host, env configuration, a connection to PostgreSQL, a public HTTPS hostname |
| **Web** (React/Vite static build) | A static host or CDN, the API base URL, the Supabase project URL and **publishable** key |
| **Database** (PostgreSQL + migrations) | Supabase project; migrations applied by `Convenciones.Migrator`, never by hand |

---

## 2. When Supabase is actually needed

Nothing in T3–T6 requires it: those tasks run against the disposable PostgreSQL container and the local
storage adapter, and are verified that way.

| Point | What is needed | Blocking? |
|---|---|---|
| **T4** document storage, production adapter | Bucket name and private policy; `service_role` key for the **server** | No — the local adapter keeps the slice runnable. Deferred to this task |
| **T7** web sign-in | Project URL + publishable key, and a real user to sign in with | **Yes, in practice** — the browser cannot authenticate against a project that does not exist. T7 can be built and tested against a local stand-in, but cannot be *demonstrated* without the project |
| **T18** deployment | Everything below | **Yes** |
| **T19** operations | Backups, monitoring, restore rehearsal | **Yes**, but only after T18 |

So: **I need it for T7's demonstration, and I must have it for T18.** Giving me the information before
T7 starts removes a stall; giving it at T18 is workable but delays the first usable demo.

### Exactly what I need, and how sensitive it is

| # | Item | Why | Sensitivity |
|---|---|---|---|
| 1 | **Project URL** (`https://<ref>.supabase.co`) | JWT issuer/audience, CORS, browser client | Public |
| 2 | **Publishable / anon key** | Browser client signs in and calls the API | Public by design — still restricted by RLS |
| 3 | **JWKS / metadata URL** (`/auth/v1/.well-known/jwks.json`) | The API validates token signatures | Public |
| 4 | **`service_role` key** | Server-side only: storage writes, and any operation that must bypass RLS | **Secret — bypasses every RLS policy.** Never in the browser, never in the repo, only in a secret store |
| 5 | **Direct connection string** (port 5432) | Applying migrations | Secret |
| 6 | **Pooled connection string** (port 6543, transaction mode) | The API's runtime connection | Secret |
| 7 | **Bucket name + policy** | Private bucket, per-company object keys | Public name, policy matters |
| 8 | **Region** | Latency to Venezuela; backup location | Public |
| 9 | **Plan tier** | Free pauses idle projects and has no automated backups; Pro enables both, plus real branch protection on the repo | Decision |

**How to hand these over:** put the secrets in the host's secret store and tell me the variable names. Do
not paste them into this conversation or a file — the repository already has 17 committed credentials and
does not need an eighteenth.

---

## 3. Decisions I need before starting

| # | Decision | Why it must be yours |
|---|---|---|
| **D1** | **Where does the API run?** Azure Container Apps · Fly.io · Render · Railway · a VPS · your own server | Cost, operational ownership, whether you want a managed platform or a machine to maintain |
| **D2** | **Environments:** just production, or staging as well? | A staging copy roughly doubles Supabase cost and adds a promotion step; skipping it means every test happens against live data |
| **D3** | **Supabase plan:** Free or Pro | Free: no automated backups, projects pause when idle, and **GitHub branch protection is unavailable** — the same 403 that has blocked enforcement since Task 6 |
| **D4** | **Domain and DNS** — who owns them, and which hostnames | TLS, CORS allow-list, cookies |
| **D5** | **Who holds the `service_role` key** | It bypasses RLS. One holder, rotated on a schedule, stored in a secret manager |

---

## 4. Scope

**In:** container image for the API (multi-stage, non-root, no secret baked in); static build for the web;
environment configuration per environment; Supabase project provisioning **with your approval**; migrations
applied through the migrator and verified by query; private storage bucket; CORS restricted to the web
origin; HTTPS only; `/health/ready` wired to the platform's health check; a deploy workflow in CI.

**Out:** backups, restore rehearsal, monitoring and incident runbooks — those are T19. Also out: any
action on the remote database that you have not explicitly approved.

---

## 5. Deliverables

| File | Purpose |
|---|---|
| `Dockerfile` (API) | Reproducible image, non-root, no secrets |
| `.github/workflows/deploy.yml` | Build, test, migrate, deploy — or deploy only on `main`, your call |
| `docs/operations/deployment.md` | How to deploy, what each environment is, how to roll back |
| `src/Convenciones.Web/.env.example` | Which variables exist, with no values |

## 6. Acceptance criteria

1. `/health/ready` returns **200** at the deployed URL with the real database, and **503** when the
   database is unreachable — proven by stopping connectivity, not by assumption.
2. Migrations applied by the migrator, and the result verified by querying `information_schema` — not by
   trusting the exit code.
3. A browser signs in against real Supabase Auth and calls `/me` successfully.
4. A user of company A cannot read company B's data **over HTTPS in the deployed environment** — the
   first time tenant isolation is proven against something that is not a test container.
5. No secret appears in the repository, the image, or the JavaScript bundle — proven by running the scan
   and inspecting the bundle, not by inspection "looking fine".
6. Redeploying the previous version restores service, and the procedure is written down and timed.
7. The API refuses to start if a required secret is missing — verified by deploying without it once, in
   staging.

## 7. Risks

| Risk | Mitigation |
|---|---|
| **`service_role` reaching the browser.** It bypasses RLS entirely; one mistake turns the tenant model off | Server-only, never in a `VITE_` variable, and a build-time check failing on a `service_role` string in the bundle |
| **RLS silently not applying** because the runtime connects as the project owner | The API connects with a non-owner role; a deployment test asserts a cross-company read fails |
| **Free tier pauses the project**, and the demo fails at the worst moment | Decide D3 before the demo; know how to unpause |
| **Migrations applied by hand** and drifting from the repository | Only the migrator applies them; the deploy pipeline runs it, and a manual change is treated as an incident |
| **Pooled connections and `SET LOCAL ROLE`** | Transaction-mode pooling still scopes `SET LOCAL` correctly; verify explicitly in the deployed environment rather than assuming |

## 8. What this task does **not** claim

Deploying does not make the credentials in T16 safe, and it does not rotate them. Those stay live until
someone rotates them, and the git history keeps them retrieveable.
