# T3 — Tenant scoping and persistence cross-cutting concerns

**Status: in progress — read path done, write path pending.** Branch `task/14-tenant-scoping`.

## Decisions taken before starting

| Decision | Outcome |
|---|---|
| **Scope** | Option A: this task includes `GET /companies` and `GET /companies/{id}` as the proving ground, because the tenant rule cannot be proven without a company-scoped endpoint. Confirmed by the Product Owner |
| **Audit writer** | An EF Core interceptor in the API — not a database trigger, not per-handler calls. Confirmed by the Product Owner |

## Objective

Make the tenant rule real in the application layer, not only in SQL, and close the persistence gaps the
release audit listed as high priority (H1, H2, H3).

## Scope

**In:** the company read path as the proving ground; a company-scoped rule in one place; audit-column
interceptor; optimistic concurrency (`xmin` / `If-Match`); `Result<T>` → RFC 9457 mapping; request size
limits; CORS policy.

**Out:** domain aggregates (T8); comparison read models (T9); user administration (T13).

## Progress

### Done — the tenant scope, proven over HTTP

| Piece | File |
|---|---|
| `Company` entity mapped column by column, `xmin` as concurrency token | `src/Convenciones.Catalog/` |
| `TenantScope` — the single place the rule lives | `src/Convenciones.Catalog/TenantScope.cs` |
| `GET /companies`, `GET /companies/{id}` | `src/Convenciones.Api/Endpoints/CompanyEndpoints.cs` |
| `ErrorCodes` matching the API contract | `src/Convenciones.Identity/ErrorCodes.cs` |
| CORS allow-list and a 21 MiB transport limit (audit H3) | `src/Convenciones.Api/Program.cs` |

Verified: build **0 warnings, 0 errors**; **33 tests passing** (10 unit, 23 integration).

The six new tests prove:

1. The caller's own company is readable.
2. **Another company is 404, not 403** — a 403 would confirm it exists and let a caller enumerate tenants.
3. The list returns only the caller's company for an editor, who holds `company.read` but not
   `company.read_all`.
4. `company.read_all` unlocks the full list.
5. `company.read_all` can read any company.
6. Both endpoints are **401** without a token.

### Remaining — the write path

`POST /companies`, `PATCH /companies/{id}` with `If-Match` returning **409 concurrent_modification**,
the audit interceptor writing `platform.audit_entry`, and `Result<T>` → RFC 9457 mapping.

These are precisely the criteria that need a write to exist before they can be demonstrated. They are not
"nearly done": nothing has been written to `audit_entry` by this code yet, and the concurrency path has
never executed.

## Risks

- RLS filters silently (audit §5.1): a cross-tenant write returns **0 rows without an error**. The write
  path must inspect affected row counts or it will report success for a write that never happened.
- `xmin` as a concurrency token is cheap but coarse: any update to the row invalidates it, which is
  correct, and any update to a *different* row does not, which is also correct. It does mean the value is
  not stable across a `VACUUM FULL`; the contract's `rowVersion` is opaque to clients, so that is
  acceptable but worth knowing.
