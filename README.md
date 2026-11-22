# Convenciones

A **contract intelligence platform** for collective bargaining agreements (*convenciones
colectivas*): authoring agreement clauses, comparing them across agreements and against labour
legislation, tracking the negotiation that produced them, and publishing the results.

> **Status: pre-alpha — every MVP workflow is implemented and tested, and CI is green on `main`.**
> One staging deployment runs over HTTPS with the AI worker **disabled by default**. There is no
> production environment and no production approval: no real contract data may be processed until the
> pending owner sign-offs are resolved. See [Current status](#current-status) and
> [pending owner sign-offs](docs/operations/owner-signoffs.md).

## Current status

*As of 2026-11-22.*

| Area | State |
|---|---|
| MVP workflows (T1–T14) | implemented and tested: documents and processing, agreements/clauses, comparison, legal library, negotiation, publishing/portal, administration/audit, web client |
| CI (T15) | green on `main` (`d0ada82`): Release build with warnings as errors, unit/integration/architecture tests on real PostgreSQL containers, migrations and seed on a fresh database, web typecheck/tests/build with bundle scan, secret scan |
| Staging (T18) | live at <https://138-197-15-40.sslip.io> (Caddy terminates TLS; port 80 redirects); AI worker `Worker__Enabled=false` by default; last deploy 2026-11-14 at `97a2cac`, so later work on `main` is **not** deployed |
| Production-v1 remediation (P1/P2) | implemented and merged; the AI quality gate (P1-006) is still open — see [the task index](docs/tasks/README.md#production-readiness-remediation-production-v1-audit) |
| Production | **not approved.** No production environment, no real contract data, no signed release gate |
| Open decisions | [pending owner sign-offs](docs/operations/owner-signoffs.md) |

---

## Read this first

[docs/constitution.md](docs/constitution.md) is the **authoritative** engineering rulebook —
mandated stack, architecture rules, data policy, security, testing, and Git workflow. Any
practice that conflicts with it is a defect, not a preference.

## Target architecture

| Concern | Choice |
|---|---|
| Backend | C# / ASP.NET Core on .NET |
| Orchestration & observability | .NET Aspire |
| Database | PostgreSQL on Supabase |
| Data access | Entity Framework Core + Npgsql |
| Frontend | React + Vite + Material UI (MUI) |
| Styling | MUI only — **no Tailwind CSS** |
| Identity & storage | Supabase Auth, Storage, and Row Level Security |
| CI | GitHub Actions |

Architecture: a **modular monolith** with Clean Architecture and feature-oriented slices —
no microservices for the MVP (constitution Art. IV).

## Repository layout

```
docs/                     Governance and analysis
  constitution.md         Authoritative engineering rules — start here
  tasks/                  One record per implementation task
  legacy/                 Archived analysis of the legacy system (the tree itself was removed)
src/                      ASP.NET Core API, modules, migrator, AppHost, web client
tests/                    .NET unit and integration tests
supabase/                 Migrations, seed and the disposable-database test suites
tools/                    Developer scripts: secret scan, git hooks installer
```


The legacy PHP application and its MySQL dump were **evidence, not a codebase**. They were never
modified as a codebase and no PHP code was ported (constitution Art. IX). **They were removed from
the repository in T16** because they carried committed database credentials and personal data; the
analysis under [docs/legacy](docs/legacy/README.md) and [docs/database](docs/database/) is the
retained evidence, and the removed material is archived outside Git.

The credentials belonged to MySQL infrastructure the Product Owner confirmed decommissioned on 2026-10-31,
so rotation is not applicable. The values survive in the pre-rewrite backup and GitHub's PR refs. See
[docs/operations/legacy-removal.md](docs/operations/legacy-removal.md).

## Product scope (MVP)

| Capability | Status |
|---|---|
| Collective agreements — clauses, annexes, publication state | In scope |
| Negotiation workflow — petitions → offers → meetings → agreements | In scope |
| Legal library — labour law and statutes at article level | In scope, **starts empty** (no legacy content) |
| Clause comparison across agreements and against legislation | In scope |
| Public portal — published agreements and news | In scope |
| Administration — master data, users, roles, permissions | In scope |

Out of scope: data migration from MySQL, microservices, mobile apps, offline mode.

## Key product decisions

- **A single SuperAdmin is provisioned at first run.** This is the only account created outside
  the application. **Every other user, administrator, role, and permission is created inside the
  application** by an authorised administrator (constitution Art. VI.6).
- **Reference catalogs are seeded** from curated, hand-reviewed data — countries, states,
  municipalities, comparison headings. This is reference data authored for the new system, never
  business records and never a MySQL migration (constitution Art. V.3).
- **User-facing copy is Spanish**; all code, schema, and documentation are in English
  (constitution Art. X).

## Prerequisites

- .NET SDK **10** — `global.json` pins `10.0.401` with `rollForward: latestFeature`, so any 10.0.4xx
  SDK satisfies it. CI installs `10.0.x` and the images build with
  `mcr.microsoft.com/dotnet/sdk:10.0`. Do not raise the pinned major to accommodate a machine with a
  different SDK: install SDK 10 instead, or CI and every container build will fail.
- Node.js **22+** and **pnpm**
- Docker (the verified local PostgreSQL path runs a disposable `postgres:17-alpine` container)

## Running it locally

Aspire is the target orchestration, but its **project resources do not start** (T17, open): the
containers run while the API and migrator stall. The path demonstrated end to end is the direct run in
[docs/operations/local-development.md](docs/operations/local-development.md#3-verified-path--run-the-api-directly-against-a-local-postgresql)
— a disposable PostgreSQL container, the migrator, then the API.

```bash
# API + PostgreSQL (verified fallback — see local-development.md §3)
dotnet run --project src/Convenciones.Migrator
dotnet run --project src/Convenciones.Api

# Aspire experiment only: defines the resources but does not start the app resources (T17)
# dotnet run --project src/Convenciones.AppHost

# Web client, in a second terminal
cd src/Convenciones.Web
pnpm install
cp .env.example .env.local    # set VITE_API_BASE_URL to the API address
pnpm dev                      # http://localhost:5173
```

The API validates JWTs on every request; `Jwt:Issuer`, `Jwt:Audience` and a signing key or JWKS
metadata must be configured (T2). Without them — on a fresh local checkout, for example — the sign-in
screen reports `auth_not_configured` rather than pretending to authenticate. On staging Supabase Auth
is provisioned and sign-in is verified. See the
[web README](src/Convenciones.Web/README.md) for the client's configuration, security notes and
limitations.

## Working in this repository

- Never commit directly to `main`. One task = one branch = one pull request
  (constitution Art. VIII).
- Test reports must reflect tests actually executed (constitution Art. VII.1).
- Uncertainties are logged, not resolved silently (constitution Art. XIII).

## Legacy reference

The legacy tree is no longer in the repository. [docs/legacy/README.md](docs/legacy/README.md)
describes what it contained and why the analysis is retained;
[docs/operations/legacy-removal.md](docs/operations/legacy-removal.md) records the removal, the
credential inventory, the decommissioning decision and what deletion does not fix.

