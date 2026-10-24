# T2 — Identity and authorization

**Status: implemented and verified.** Branch `task/12-identity-authorization`.

---

## 1. Objective

Make every endpoint authenticate a real caller and check an explicit capability. Before this task the
API had zero business endpoints and zero authentication code; the release audit recorded that as
blocking finding B1.

## 2. Scope

**In:** JWT validation (symmetric for local/test, JWKS metadata for a real deployment); session
resolution from `platform.user_account`/`user_profile`/`user_role`/`role_capability`; blocked and
expired enforcement on every request; a fallback authorization policy; a capability declaration every
endpoint must make; a startup gate that refuses to boot when one is missing; `GET /me`.

**Out:** user administration (T13), company-scoped queries (T3), tenant tests over HTTP (T3),
Supabase project provisioning, password handling of any kind.

## 3. No schema change was required

The identity policies already let `app_api` read its own `user_account`, own `user_profile`, own
`user_role`, and the role/capability tables. Session resolution therefore runs entirely under RLS with
no new table, column or function — verified by reading
`20260101001000_rls_policies.sql` before writing code, rather than adding a `SECURITY DEFINER` helper
by reflex.

## 4. Design decisions worth reviewing

| Decision | Reason |
|---|---|
| **Capabilities are read from the database on every request**, not embedded in the JWT | A revoked capability takes effect on the next request instead of when the token expires. A token that carries its own permissions is a permission system you cannot withdraw |
| **Fallback policy requires authentication** | Endpoints are protected by default; an endpoint has to opt out with `[AllowAnonymous]`, which is visible in the diff |
| **Startup refuses to boot if any endpoint lacks a security declaration** | A missed declaration is a data leak. Found by the author in their pull request, not by whoever notices later |
| **The session query switches to `app_api` inside the transaction** (`SET LOCAL ROLE`) | RLS protects nothing if the API connects as a superuser and merely believes it is protected. `SET LOCAL` reverts with the transaction, so nothing leaks into the pooled connection |
| **Fail fast on missing JWT configuration** | Without a way to validate signatures there is no authentication, only a claim that there is |

## 5. Files added or changed

| File | Purpose |
|---|---|
| `src/Convenciones.Identity/*` | New module: options, session, resolver, middleware, attributes, validator, DI |
| `src/Convenciones.Api/Program.cs` | Auth pipeline, capability middleware, startup gate, `/me` |
| `src/Convenciones.Api/Endpoints/MeEndpoints.cs` | `GET /me` |
| `src/Convenciones.ServiceDefaults/Extensions.cs` | Probes are explicitly anonymous |
| `tests/…/EndpointSecurityValidatorTests.cs` | 7 unit tests, including the vacuous-pass case |
| `tests/…/AuthorizationTests.cs` | 10 integration tests |
| `tests/…/IdentityFixture.cs` | Isolated database — `SchemaTests` asserts the seed creates no user |
| `tests/…/TestAuth.cs`, `ApiFactory.cs` | Token minting, test host settings |

## 6. Checks executed

```
dotnet build Convenciones.slnx   → Compilación correcta. 0 Advertencias, 0 Errores
dotnet test  Convenciones.slnx   → UnitTests          10/10
                                 → IntegrationTests   17/17
```

The 10 authorization tests:

| Test | Result |
|---|---|
| `Every_route_declares_its_security_requirement` | PASS — enumerates the real route table and asserts it is not empty |
| `Request_without_a_token_is_refused_with_401` | PASS |
| `Token_signed_with_a_foreign_key_is_refused_with_401` | PASS |
| `Token_for_another_issuer_or_audience_is_refused_with_401` | PASS |
| `Expired_token_is_refused_with_401` | PASS |
| `Token_for_a_subject_without_an_account_is_refused_with_403` (`no_profile`) | PASS |
| `Blocked_account_is_refused_with_403` (`account_inactive`) | PASS |
| `Expired_account_is_refused_with_403` (`account_inactive`) | PASS |
| `Active_user_receives_company_and_capabilities` | PASS — company id and `agreement.read` present |
| `Probes_stay_anonymous` | PASS |

### What failed first, and why it mattered

1. **The startup gate threw `found no routed endpoint`** in every test. The validator was reading
   endpoints from DI, which returns nothing at that point in the host's life. The gate caught its own
   blindness — exactly the vacuous-pass failure mode it was written to prevent. Fixed by reading the
   application's own `IEndpointRouteBuilder.DataSources`, and the validator now refuses an empty set
   rather than passing.
2. **The expired-token test failed as a malformed token, not an expired one** (`Expires must be after
   NotBefore`) because the helper built `notBefore = now-1min` with a negative lifetime. Fixed by
   constructing the expired token explicitly. The first version would have passed for the wrong reason
   if the exception had been swallowed.

## 7. Not claimed

- No Supabase project was contacted. Production token validation is configured but **unproven against
  a real issuer** — only against locally minted tokens.
- Tenant isolation over HTTP is **not** tested here. T2 proves who the caller is and what they may do;
  T3 proves they cannot reach another company's rows through an endpoint.

## 8. Left undone

- `docs/architecture/security-model.md` still describes the four-role model and `analyst`; it should be
  updated with `company.read_all` and the five roles. Deferred to T3, where scoping lands.
- Capability checks answer "may this user do this kind of thing", not "may they do it to this company".
  That is T3.
