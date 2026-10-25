# Convenciones

A **contract intelligence platform** for collective bargaining agreements (*convenciones
colectivas*): authoring agreement clauses, comparing them across agreements and against labour
legislation, tracking the negotiation that produced them, and publishing the results.

> **Status: pre-alpha — the document, agreement-authoring and comparison workflows are implemented and
> tested.** The API (T1–T9) and the first web slice (T7) build and run locally. Real sign-in needs the
> Supabase project that T18 provisions, and nothing is deployed yet.

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
  legacy/README.md        Archived description of the legacy tree
src/                      ASP.NET Core API, modules, migrator, AppHost, web client
tests/                    .NET unit and integration tests
admin/ lib/ plantillas/   Legacy PHP application — READ ONLY
  css/ img/ new_img/ ...
index.html                Legacy public portal — READ ONLY
presenci_ccol.sql         Legacy MySQL dump — analysis only, never migrated
```

The legacy PHP application and the dump are **evidence, not a codebase**. They are never
modified, and **no PHP code is ported** (constitution Art. IX). The legacy tree is expected to be
removed from the repository once the functional analysis is complete; until then it stays
untouched at the root.

**T16 is in progress.** The dead vendor assets (`SpryAssets/`, `Scripts/`) were removed: nothing in the
new stack referenced them, and `SpryAssets/` contained a stale nested copy of itself. Twelve legacy PHP
pages still reference those assets, so those pages no longer render as they once did — they remain
readable as code, which is all the analysis needs from them.

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

- .NET SDK **10**
- Node.js **22+** and **pnpm**
- Docker (local PostgreSQL via Aspire)

## Running it locally

```bash
# API + PostgreSQL + migrations, orchestrated by Aspire
dotnet run --project src/Convenciones.AppHost

# Web client, in a second terminal
cd src/Convenciones.Web
pnpm install
cp .env.example .env.local    # set VITE_API_BASE_URL to the API address
pnpm dev                      # http://localhost:5173
```

The API validates JWTs on every request; `Jwt:Issuer`, `Jwt:Audience` and a signing key or JWKS
metadata must be configured (T2). Until T18 provisions the Supabase project, the sign-in screen
reports `auth_not_configured` rather than pretending to authenticate. See the
[web README](src/Convenciones.Web/README.md) for the client's configuration, security notes and
limitations.

## Working in this repository

- Never commit directly to `main`. One task = one branch = one pull request
  (constitution Art. VIII).
- Test reports must reflect tests actually executed (constitution Art. VII.1).
- Uncertainties are logged, not resolved silently (constitution Art. XIII).

## Legacy reference

See [docs/legacy/README.md](docs/legacy/README.md) for what the legacy tree contains and why it is
retained. Credentials in `local/` are excluded from version control and must never be reused.
