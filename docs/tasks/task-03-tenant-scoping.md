# T3 — Tenant scoping and persistence cross-cutting concerns

**Status:** planned · **Depends on:** T2 · **Blocks:** T4, T8, T10, T11

## Objective

Make the tenant rule real in the application layer, not only in SQL, and close the three persistence
gaps the release audit listed as high priority (H1, H2, H3).

## Scope

**In:** EF Core mappings for the entities the slices touch; a company-scoped query helper used by every
read and write; audit-column interceptor; optimistic concurrency (`xmin` / `If-Match`); `Result<T>` →
RFC 9457 mapping; request size limits; CORS policy; `/me` company resolution consumed by every scoped
query.

**Out:** domain aggregates (they arrive with T8); read models for comparison (T9).

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Api/Infrastructure/` | DbContext mappings, interceptors, concurrency, exception→problem mapping |
| `src/Convenciones.SharedKernel/` | `Result<T>`, error types, `CompanyScope` |
| `tests/…/TenantIsolationTests.cs` | **API-level** cross-tenant denial: company A cannot read/write company B over HTTP |

## Acceptance criteria

1. A user of company A calling any company-scoped endpoint for company B's resource gets **404**, not
   403 — existence is not leaked.
2. Cross-tenant **write** attempts leave the row unchanged, and the test asserts the row afterwards.
3. Audit columns are populated on every insert/update, with one `audit_entry` row per meaningful change,
   written in the same transaction; a forced failure leaves neither.
4. A `PATCH` with a stale `If-Match` returns **409 concurrent_modification**.
5. Every expected failure returns a problem body with `code` and `correlationId`; no stack trace.
6. Upload/request size limits are configured, not left at framework defaults.

## Risks

- Mapping EF entities to a schema whose constraints are stricter than the model produces late runtime
  failures; the mappings must be exercised against the real schema, not SQLite.
- RLS filters silently (audit §5.1): a cross-tenant write returns 0 rows **without an error**. The API
  must inspect affected row counts, or it will report success for a write that never happened.
