# P1-008 — Tenant isolation verification

**Status:** implemented and tested; one policy finding (**F-08-1**) remains open for the Product Owner.
**Finding:** F-08 (production-v1 audit) · **PR:** #113

## Objective

Prove that a user of one company cannot read or write another company's data — over HTTP with real
sessions, and at the PostgreSQL policy layer under the roles the API actually uses.

## What was delivered

| Component | Location |
|---|---|
| HTTP isolation suite | `tests/Convenciones.IntegrationTests/TenantIsolationHttpTests.cs` (10 tests) |
| RLS isolation suite | `tests/Convenciones.IntegrationTests/TenantIsolationRlsTests.cs` (5 tests) |
| Endpoint and table matrix | `docs/security/p1-008-tenant-isolation-matrix.md` |

## Design

1. **Two tenants with real callers.** An editor, an administrator holding `company.read_all`, a blocked
   user and anonymous; the database is exercised as `app_api` / `authenticated` with
   `request.jwt.claims` set, never as a superuser.
2. **Two layers, not one.** The API is not trusted as the only guard: the same cross-tenant probes run
   over HTTP and directly against the policies.
3. **Denial semantics are pinned.** A foreign identifier resolves to **404** (403 would confirm it
   exists); a missing capability is **403**; the policy layer returns **42501** or zero rows.

## Coverage

13 endpoint groups plus the public API; 14 tenant root tables exercised or policy-reviewed; blocked
and anonymous callers; a positive control that each caller reads and updates its own rows.

## Findings

- **F-08-1 — `company.read_all` reaches another tenant's raw content (policy, high, open).**
  `TenantScope.CanAccess` returns true for any company when the caller holds `company.read_all`, and
  `DocumentAccess` relies on it, so an administrator receives **200** for another tenant's document
  metadata and can follow the same helper into text, download, processing, agreements, negotiations
  and comparisons. That exceeds the capability's stated intent (aggregate/platform oversight, not raw
  documents). The test pins the current behaviour so a change is deliberate.
  **Decision required:** split the capability (aggregate oversight vs. an explicit, audited support
  access), or accept the behaviour for platform administrators. RLS redesign is out of scope.
- **F-08-2 — no cross-tenant leak found in the exercised paths (positive).**

## Residual gaps

`extract`/`extraction` share `DocumentAccess` but are policy-reviewed, not exercised over HTTP; child
tables rely on their parent's policy; SuperAdmin was not exercised separately (it also holds
`company.read_all`, so F-08-1 applies with wider scope).

## Results

PR #113: 15 new PostgreSQL integration tests plus the matrix; full CI green. The matrix records
expected vs. observed for every endpoint group and table.

## Left undone

The F-08-1 decision (see [pending owner sign-offs](../operations/owner-signoffs.md)); the policy-review
rows become executable assertions only if the policies change.
