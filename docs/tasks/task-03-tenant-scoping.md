# T3 — Tenant scoping and persistence cross-cutting concerns

**Status: complete — ready for review.** Branch `task/14-tenant-scoping`, PR #14.

## Decisions taken before starting

| Decision | Outcome |
|---|---|
| **Scope** | Option A: includes the company endpoints as the proving ground, because the tenant rule cannot be proven without a company-scoped endpoint. Confirmed by the Product Owner |
| **Audit writer** | An EF Core interceptor in the API — not a database trigger, not per-handler calls. Confirmed by the Product Owner |
| **Creating a company** | Requires **`company.write` AND `company.read_all`**. A user who could create a company without cross-company visibility would be granting themselves a new tenant. Confirmed by the Product Owner |

## What was built

### Read path

| Piece | File |
|---|---|
| `Company` entity mapped column by column, `xmin` as concurrency token | [Company.cs](src/Convenciones.Catalog/Company.cs) |
| `TenantScope` — the rule in one place | [TenantScope.cs](src/Convenciones.Catalog/TenantScope.cs) |
| `GET /companies`, `GET /companies/{id}` | [CompanyEndpoints.cs](src/Convenciones.Api/Endpoints/CompanyEndpoints.cs) |
| `ErrorCodes` matching the API contract | [ErrorCodes.cs](src/Convenciones.Identity/ErrorCodes.cs) |

### Write path

| Piece | Behaviour |
|---|---|
| `POST /companies` | 201 with `Location`; creator taken from the session, never the body |
| `PATCH /companies/{id}` | Requires `If-Match`; stale version → **409 concurrent_modification**; missing header → **400** |
| Audit interceptor | One `platform.audit_entry` row per change, same transaction, actor and correlation id recorded |
| Multi-capability enforcement | `RequiresCapability(...)` now requires **every** declared capability, not the first |
| CORS + transport limit | Allow-list with no wildcard; 21 MiB so an oversized upload is refused while reading (audit **H3**) |

## Verification

```
dotnet build Convenciones.slnx → Compilación correcta. 0 Advertencias, 0 Errores
dotnet test  Convenciones.slnx → UnitTests         10/10
                               → IntegrationTests  33/33
```

### The authorization matrix — all four cases

| Caller | `company.write` | `company.read_all` | `POST /companies` |
|---|---|---|---|
| Administrator | yes | yes | **201** |
| Editor | yes | no | **403** |
| Auditor (transient role) | no | yes | **403** |
| Negotiator | no | no | **403** |

The "auditor" role exists only in the test fixture, because no seeded role has the shape
"read across companies, write nothing" and the matrix is incomplete without it.

### Creation does not widen access

- The creator's `user_profile.company_id` is unchanged — asserted through `/me` after creating.
- Another company's user still gets **404** for the new company and still sees exactly **one** company
  in the list.

### Concurrency and audit

- `PATCH` with the current `rowVersion` → **200**, and the returned `rowVersion` differs.
- `PATCH` re-using the pre-update `rowVersion` → **409 concurrent_modification**.
- `PATCH` without `If-Match` → **400 validation_failed**.
- `PATCH` of another company → **404**, even for a caller holding `company.write` (a 403 would confirm it
  exists).
- Creating writes exactly **one** `audit_entry` row with `action = 'created'` and the actor's id; updating
  writes exactly **one** `action = 'updated'` row whose `changes` contains the modified property.

## Notes and limits

- `Result<T>` was not introduced. The endpoints return `IResult` directly, and an abstraction with no
  second caller would be an untested abstraction.
- `xmin` is the concurrency token because PostgreSQL maintains it and the schema has no `rowversion`
  column. It is coarse but correct, and the contract treats `rowVersion` as opaque.
- A missing `If-Match` returns **400** rather than 428: the contract defines `If-Match` as required but
  does not define 428, and inventing a code outside the contract would be worse than reusing one.

## What this task does not cover

- Contracts and agreements (T8). The same `TenantScope` and the same interceptor will apply; nothing
  here is company-specific except the entity.
- The audit **read** endpoint (`GET /audit`) is T13. Rows are written now, read later.
