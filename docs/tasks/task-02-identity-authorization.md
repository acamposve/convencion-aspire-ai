# T2 — Identity and authorization

**Status:** planned · **Depends on:** T1 · **Blocks:** everything

## Objective

Make every endpoint authenticate a real caller and check an explicit capability. Today there are zero
business endpoints and zero authentication code.

## Scope

**In:** Supabase Auth JWT validation against JWKS (cached), profile resolution from
`platform.user_account`, blocked/expired enforcement on **every** request, a capability filter that
every endpoint must declare, `GET /me` returning the caller's profile, authorised company and
effective capabilities, and a route-enumeration test.

**Out:** user administration (T13), tenant scoping in queries (T3), Supabase project provisioning.

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Identity/` | JWT validation, claims → profile, `ICurrentUser`, capability policies |
| `src/Convenciones.Api/Program.cs` | Auth pipeline, `/me`, endpoint filter registration |
| `tests/…/AuthorizationTests.cs` | Route enumeration: 401 without token, 403 without capability |
| `docs/tasks/task-02-authentication.md` | This record, updated with results |

## Acceptance criteria

1. No valid token → **401** on every endpoint except `/health/*`.
2. Valid token, no profile → **403**, never a default profile.
3. Blocked or expired account → **403** on every request, including with a token issued before blocking.
4. An endpoint registered without a declared capability **fails startup**, not a request.
5. `GET /me` returns profile, `companyId` and the effective capability list.
6. The route-enumeration test asserts 401/403 for **every** route the API exposes, and fails if a route
   cannot be enumerated.

## Risks

- The route-enumeration test is only as good as its enumeration; if it silently iterates zero routes it
  passes vacuously. It must assert a non-zero, expected count.
- `request.jwt.claims` must be set on the connection the same way the RLS tests simulate, or the
  database and the API will disagree about who the caller is — the most likely place for an
  authorization hole (release audit, residual risk 2).
