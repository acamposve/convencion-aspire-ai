# T13 — Administration and audit API

**Status:** implemented · **Depends on:** T2 · **Blocks:** T14

## 1. Objective

Manage who exists and what they may do, and make the audit trail readable by those entitled to it.

## 2. Scope

**In:** user CRUD (profile, company, roles, blocked, expiry) for `user.manage`; the role and capability
matrix read for `role.manage`; the **bootstrap first SuperAdmin** path, which must work exactly once; the
audit trail query for `audit.read`.

**Out:** password handling of any kind — Supabase Auth owns credentials, the database has no password
column and must never gain one. Also out: writing roles or capability grants (the matrix is read-only in
the MVP), user invitations by email, and password reset.

## 3. Decisions

| # | Decision | Where it lives |
|---|---|---|
| 1 | Provisioning takes the Supabase **subject**, never a password; the API does not create the auth user | `BootstrapRequest`, `CreateUserRequest`, ADR-027 |
| 2 | The first-run path is one locking `SECURITY DEFINER` function that writes account, profile, role grant and audit row together | `platform.bootstrap_first_admin`, ADR-027 |
| 3 | Self-lockout is prevented in the aggregate: no self-block, no self-delete, no self-de-admin (**409**) | `UpdateUserHandler`, `ReplaceUserRolesHandler`, `DeleteUserHandler` |
| 4 | Blocking is live: the session is resolved from the database on every request | existing `PostgresSessionResolver` + `SessionContext.InactiveReason` |
| 5 | The role matrix and the capability catalogue are read-only | `GET /roles`, `GET /capabilities` |
| 6 | Every account change writes an audit row in the same transaction, without personal data in the payload | `IAuditTrail` → `AuditTrail` |
| 7 | `AuditEntry` moved to the shared kernel so the audit query can filter on columns | `Convenciones.Catalog.AuditEntry` |

## 4. Endpoints

| Method | Path | Cap |
|---|---|---|
| `GET` | `/auth/bootstrap/status` | — (anonymous) |
| `POST` | `/auth/bootstrap` | — (anonymous, one-time) |
| `GET` `POST` | `/users` | `user.manage` |
| `GET` `PATCH` `DELETE` | `/users/{id}` | `user.manage` |
| `PUT` | `/users/{id}/roles` | `user.manage` |
| `GET` | `/roles` · `/capabilities` | `role.manage` |
| `GET` | `/audit` | `audit.read` |

**Bootstrap** body: `{ subject, email, displayName }` → `201` with the account and
`roles: ["superadmin"]`; a second call is `409 invalid_state`. **Users** list is paged and filterable by
`q` (email or display name); `PATCH` is partial (absent keeps, `null` clears a nullable field, `null` for
a required field is `400`). **Audit** filters by `module`, `entityType`, `entityId`, `actorId`, `from`,
`to` and pages; each row carries the actor (id and display name), the resource, the action,
`fromState`/`toState` and `changes`.

### 4.1 Failures

| Status | Code | Cause |
|---|---|---|
| 400 | `validation_failed` | Bad email/display name, unknown role key |
| 401 | — | Anonymous caller on an administration route |
| 403 | `forbidden` | Missing `user.manage`, `role.manage` or `audit.read`; blocked/expired account |
| 404 | `not_found` | Unknown user |
| 409 | `invalid_state` | Bootstrap attempted on a configured database |
| 409 | `conflict` | Duplicate subject; self-block, self-delete, self-de-admin; deleting a referenced account |

## 5. Implementation

```
src/Convenciones.Administration/
  UserAccount.cs                 account + profile + role grants, with the anti-lockout rules
  AdministrationContracts.cs     requests/responses, IAdministrationStore, IAuditTrail, results
  AdministrationCommands.cs      bootstrap, create, update, replace roles, delete
  AdministrationQueries.cs       users, roles, capabilities, audit, bootstrap status
  AdministrationConfiguration.cs EF mappings for the platform identity tables
src/Convenciones.Api/Endpoints/AdministrationEndpoints.cs
src/Convenciones.Api/Infrastructure/AdministrationStore.cs   store + AuditTrail
src/Convenciones.Catalog/AuditEntry.cs                        moved from the API project
supabase/migrations/20260107000000_administration_bootstrap.sql
```

**Why the bootstrap is a function.** The one-time window is a property of the table, not of the caller.
A per-table INSERT policy admits the account while no user exists, but not the profile, the role grant
and the audit row that must accompany it: after the account is inserted, `has_any_user()` is already true
inside the same transaction, so each child would need a looser rule — an open door. Instead
`platform.bootstrap_first_admin` takes an exclusive lock on `platform.user_account`, re-checks under the
lock, refuses with `22023`, and writes all four rows in one statement. Two concurrent bootstraps serialise
and the loser is refused.

**Live blocking.** The resolver reads the account on every request (`is_blocked`, `expires_on`), so a
token issued before the block stops working on the next call. The integration test blocks and unblocks
the same token to prove the check is live in both directions.

**Audit without personal data in the payload.** `changes` carries field names and non-personal values
(`isBlocked`, `expiresOn`, role keys); `displayName`, `email` and `phone` are recorded as
`{ "changed": true }`, per `security-model.md` §5.5.

## 6. Acceptance criteria

| AC | Evidence |
|---|---|
| 1. Bootstrap provisions the first SuperAdmin once; a second attempt is refused by the database | `The_bootstrap_creates_the_first_superadmin_once`, `constraint-tests` G1–G2, `rls-tests` R47; R1–R2 keep asserting the policy refusal for a direct insert |
| 2. Blocking takes effect on the next request, with a token already in hand | `Blocking_takes_effect_on_the_users_next_request` (403 `account_inactive`, then restored) |
| 3. An administrator cannot remove their own last administrative role — **409** | `An_administrator_cannot_remove_their_own_last_administrative_role` (also self-block and self-delete) |
| 4. The audit trail is invisible without `audit.read` | `The_audit_trail_is_invisible_without_the_capability`, `rls-tests` R45 |
| 5. No endpoint accepts or returns a password; a test greps the schema | `No_response_carries_a_password` — every response body's property names, plus a create carrying a `password` field that is ignored |
| 6. Every user change is audited with actor and time | `Every_user_change_is_audited_with_the_actor_and_the_time` (created/updated/updated/deleted, actor and timestamp asserted) |

## 7. Test matrix

| Suite | Count | What it covers |
|---|---|---|
| `Convenciones.UnitTests/AdministrationDomainTests` | 5 | Account validation, defaults, role replacement, rename/email rules, profile replacement |
| `Convenciones.IntegrationTests/BootstrapApiTests` | 2 | Status before/after, first bootstrap, second refused, payload validation before any write |
| `Convenciones.IntegrationTests/AdministrationTests` | 10 | Provisioning, live blocking, capability refresh, anti-lockout 409s, audit trail visibility and contents, password absence, role matrix, 400/409 mapping, capability gates |
| `supabase/tests/constraint-tests.sql` | G1–G2 | The function creates account + profile + grant + audit atomically; a second call is refused |
| `supabase/tests/rls-tests.sql` | R45–R47 | `audit.read` gates the trail; `user.manage` widens the account list; the function is inert on a configured database |

## 8. Risks

- **Bootstrap is the only unauthenticated write path.** It is inert after the first account, enforced
  inside the database function under a lock. The remaining exposure is that anyone may call it on a fresh
  deployment before the operator does; that is inherent to first-run provisioning and is why the
  first deployment step is the bootstrap itself.
- **Self-lockout is not enforced by the database.** The rule depends on who is calling, not on the row,
  so a direct SQL change by someone with database credentials can still remove the last administrative
  role. The supported path is the endpoint, where the check is.
- **An audit-only role would see rows without actor names.** Reading another account requires
  `user.manage`; no seeded role has `audit.read` without it. If one is added, the user read policy needs
  the same widening.
- **Deleting a user is irreversible** and the audit trail keeps their id (the actor becomes null after
  the delete, by the foreign key's `set null`). Blocking is the recommended action and the conflict
  message says so.

## 9. Implementation record

### Verified by execution

| Check | Result |
|---|---|
| `dotnet build Convenciones.slnx` | 0 warnings, 0 errors |
| Unit tests | **64 passed** (59 + 5 new) |
| Integration tests | **154 passed** (142 + 12 new) |
| Web tests (Vitest) | 44 passed, unchanged |
| `supabase/tests/run-validation.ps1` | constraint **PASS=47 FAIL=0**, RLS **PASS=49 FAIL=0**, 19 migrations applied twice with identical schemas, `VALIDATION PASSED` |

### Findings while implementing

1. **The contract's bootstrap body had a password** (`{ email, displayName, password }`, "forwarded to
   Supabase Auth"). This service holds no Supabase Admin key and T13 forbids password handling, so the
   body became `{ subject, email, displayName }`: the operator creates the auth user and provisions the
   account with its subject. The contract is corrected in §4.1; ADR-027 records why.
2. **The one-time window could not be widened safely.** Extending `not has_any_user()` to the profile,
   role and audit policies fails after the account insert (the predicate flips inside the transaction),
   and any looser rule leaves a door open. That is why the window became a locking function.
3. **Filtering over a projected record is not translatable.** The first audit query projected into a
   record and filtered on it; EF refused. The handler now filters the columns and shapes afterwards, and
   `AuditEntry` moved to `Convenciones.Catalog` so the module can see it.
4. **`MigrationFileTests` pins the migration list**; the new migration is added there, as with every
   previous one.

### Deviations

- **Role writes are out of scope** (the task asks for the matrix *read*). `POST /roles` and
  `PUT /roles/{id}/capabilities` are documented as deferred; the database policies for them already exist.
- **No password-reset or invitation flow.** Both require Supabase Auth's Admin API, which this service
  does not call.
- **`GET /agreements/{id}/audit` from the older contract is covered by
  `/audit?entityType=agreement&entityId=…`** rather than a second route.

## 10. Left undone

- **Portal/admin UI (T14)** consumes this API.
- **Role and capability writes** — deferred, with the schema ready.
- **Audit retention and export** — a policy question, not a missing query.
