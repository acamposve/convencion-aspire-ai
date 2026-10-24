# T13 — Administration and audit API

**Status:** planned · **Depends on:** T2 · **Blocks:** T14

## Objective

Manage who exists and what they may do, and make the audit trail readable by those entitled to it.

## Scope

**In:** user CRUD (profile, company, roles, blocked, expiry) for `user.manage`; role and capability
matrix read for `role.manage`; the **bootstrap first SuperAdmin** path, which must work exactly once;
audit trail query with filters for `audit.read`.

**Out:** password handling of any kind — Supabase Auth owns credentials, the database has no password
column and must never gain one.

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Administration/` | User/role commands, audit queries |
| `src/Convenciones.Api/Endpoints/AdminEndpoints.cs` | `/users`, `/roles`, `/audit` |
| `tests/…/AdministrationTests.cs` | Bootstrap window, self-lockout, audit visibility |

## Acceptance criteria

1. The bootstrap path provisions the first SuperAdmin **once**; a second attempt is refused by the
   database policy (`not platform.has_any_user()`), and the test asserts the refusal.
2. Blocking a user takes effect on their **next request**, with a token already in hand.
3. An administrator cannot remove their own last administrative role — a **409** prevents lockout.
4. The audit trail is invisible without `audit.read` (already proven at SQL level; prove it over HTTP).
5. No endpoint accepts or returns a password, and a test greps the response schema to prove it.
6. Every user change is audited with actor and time.

## Risks

- Bootstrap is a one-shot window that is easy to leave open. If `has_any_user()` is ever wrong, the
  product has an open registration door — this deserves an adversarial test, not a happy path.
- Self-lockout is unrecoverable without database access, so it must be prevented rather than fixed.
