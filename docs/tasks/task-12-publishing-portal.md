# T12 — Publishing and public portal API

**Status:** planned · **Depends on:** T9, T10 · **Blocks:** T14

## Objective

Give publication a consumer: published agreements and news readable **without an account**, and nothing
else.

## Scope

**In:** news CRUD with publish/unpublish (actor + timestamp); anonymous read projections for published
agreements, their clauses, and news; explicit allow-list of public fields.

**Out:** the portal UI (T14); bulletins and document repository screens (rejected as unreachable in
legacy); full-text search.

## Deliverables

| File | Purpose |
|---|---|
| `src/Convenciones.Publishing/` | News commands, public projections |
| `src/Convenciones.Api/Endpoints/PublicEndpoints.cs` | `/public/agreements`, `/public/news` |
| `tests/…/PublicApiTests.cs` | Anonymous access and leakage |

## Acceptance criteria

1. An anonymous caller reads published agreements and news, and gets **404** for an unpublished one.
2. The public projection contains **no** internal field: no `company_id` beyond the display name, no
   audit columns, no draft clauses, no storage paths. A test enumerates the response shape.
3. Publishing news requires `news.publish`; the actor and time are recorded by the database, not the
   client.
4. Unpublishing removes it from the public list immediately.
5. The RLS `anon` policies are what enforce this — the test proves the API is not the only guard.

## Risks

- The public surface is the only unauthenticated one in the product. Every field added to it later is a
  potential disclosure, so the projection must be an explicit allow-list, never a serialized entity.
