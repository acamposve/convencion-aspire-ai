# T12 — Publishing and public portal API

**Status:** implemented · **Depends on:** T9, T10 · **Blocks:** T14

## 1. Objective

Give publication a consumer: published agreements and news readable **without an account**, and nothing
else. The hard parts were not the news CRUD but the three things the schema could not do by itself —
running anonymous reads as an unprivileged role, making news publication a privileged transition, and
sanitising rich text, which no module did.

## 2. Scope

**In:** news CRUD with publish/unpublish (actor + timestamp recorded by the database); anonymous read
projections for published agreements, their clauses and annexes, and news; an explicit allow-list of
public fields.

**Out:** the portal UI (T14); bulletins and the document repository (rejected as unreachable in legacy);
full-text search; news images (a public image needs a document download route — a disclosure decision
nobody has taken); migrating the legacy category rows.

## 3. Decisions

| # | Decision | Where it lives |
|---|---|---|
| 1 | Public reads are explicit projection records, never serialised entities | `NewsContracts.cs`, ADR-026 |
| 2 | Anonymous reads run on the **`anon`** database role, opted into per endpoint; handlers filter by id/paging only, never by state | `ServesPublicData`, `DatabaseSessionInterceptor`, ADR-026 |
| 3 | News publication is guarded at the database and records its own actor and time | `app.guard_news_publication()`, ADR-026 |
| 4 | Rich text is sanitised on write **and** again at the public boundary | `RichText`, `NewsResponses`, ADR-026 |
| 5 | News is global (no company); categories are reference data created through the API | schema as designed |
| 6 | Partial updates reuse `PatchField<T>`, moved from Legal to Catalog so both modules share one type | `Convenciones.Catalog.PatchField` |

## 4. Endpoints

| Method | Path | Cap |
|---|---|---|
| `GET` | `/public/agreements` · `/public/agreements/{id}` | — (anonymous, `anon` role) |
| `GET` | `/public/news` · `/public/news/{id}` · `/public/news/categories` | — (anonymous, `anon` role) |
| `GET` `POST` | `/news` | `news.read` / `news.write` |
| `GET` | `/news/{id}` | `news.read` |
| `PATCH` | `/news/{id}` | `news.write` |
| `POST` | `/news/{id}/publish` · `/news/{id}/unpublish` | `news.publish` |
| `GET` `POST` | `/news/categories` | `news.read` / `news.write` |

### 4.1 Public shapes (allow-list)

```
GET /public/agreements      → { items: [{ id, title, companyName, validFrom, validTo, scope, publishedAt }], page, pageSize, total }
GET /public/agreements/{id} → { id, title, companyName, validFrom, validTo, scope, publishedAt,
                                clauses: [{ number, title, summary, body }],
                                annexes: [{ name, description, body }] }
GET /public/news            → { items: [{ id, title, summary, categoryName, publishedAt }], page, pageSize, total }
GET /public/news/{id}       → { id, title, summary, body, categoryName, publishedAt }
GET /public/news/categories → [{ id, name, description, sortOrder, isActive }]
```

No `companyId`, no publisher identity, no audit columns, no heading or document ids, no storage paths, no
draft content. A draft read by id is **404**.

### 4.2 Failures

| Status | Code | Cause |
|---|---|---|
| 400 | `validation_failed` | Missing title/body, unknown category, clearing the category, bad `state` filter |
| 401 | — | An anonymous caller on an admin news route |
| 403 | `forbidden` | `news.publish` missing on publish/unpublish (API **and** database guard) |
| 404 | `not_found` | Unknown article; an unpublished article on the public surface |
| 409 | `conflict` | Publishing an already-published article; unpublishing a draft; duplicate category name |
| 409 | `concurrent_modification` | Two edits raced on `xmin` |

## 5. Implementation

```
src/Convenciones.Publishing/
  NewsArticle.cs          aggregate: Draft → Published, sanitised body, publication columns
  NewsRuleException.cs    validation/conflict + the text helpers (sanitise before the required check)
  NewsContracts.cs        admin requests/responses and the public allow-list records
  NewsCommands.cs         create/update/publish/unpublish + category create
  NewsCommandResult.cs    one result type + the category-name helper
  NewsQueryHandlers.cs    admin list/detail/categories, public news and public agreements
  NewsResponses.cs        the two mappings, including the re-sanitisation at the public boundary
  NewsConfiguration.cs    EF mappings (xmin token on news_article)
  INewsStore.cs           persistence port
src/Convenciones.Catalog/
  RichText.cs             the sanitiser (A-33), shared by clauses, annexes and news
  PatchField.cs           moved from Convenciones.Legal so both modules share one partial-update type
src/Convenciones.Api/Endpoints/PublicEndpoints.cs
src/Convenciones.Api/Endpoints/NewsEndpoints.cs
src/Convenciones.Api/Infrastructure/NewsStore.cs
src/Convenciones.Identity/EndpointSecurity.cs   PublicDataAttribute + ServesPublicData()
supabase/migrations/20260106000000_news_publication_guard.sql
```

**Anonymous request → `anon` role.** The session middleware sets a flag for endpoints that declare
`ServesPublicData()`; `DatabaseSessionInterceptor` then issues `set role anon` instead of `reset role`.
That is the difference between a projection that is trusted and one that is enforced: the public handlers
have no publication predicate, so if the role were wrong the draft test would return 200.

**Rich text.** `RichText.Clean` sanitises with the library's allow-list and returns `null` when nothing
survivable remains, so the existing "required" checks refuse a body that was only a script tag. It is
applied to clause bodies, annex bodies and news bodies; the public projections sanitise again because the
portal may serve rows written before the rule.

**Publication guard.** `app.guard_news_publication()` refuses an `INSERT` of a published article
(`22023`), requires `news.publish` for any state change (`42501`), and assigns or clears
`published_by`/`published_at` itself. The API sets the same values; the trigger overwrites them, so the
row records the database's actor.

## 6. Acceptance criteria

| AC | Evidence |
|---|---|
| 1. Anonymous reads published agreements and news; **404** for an unpublished one | `Anonymous_reads_a_published_agreement_and_gets_404_for_a_draft`, `Anonymous_news_reads_are_limited_to_published_articles`, R43, R44 |
| 2. The public projection contains no internal field; a test enumerates the shape | `The_public_agreement_projection_is_an_allow_list`, `The_public_news_projection_is_an_allow_list` |
| 3. Publishing news requires `news.publish`; actor and time recorded by the database | `Publishing_requires_the_publish_capability`, `The_database_records_the_publisher_and_the_publication_time`, R41, R42, N2 |
| 4. Unpublishing removes it from the public list immediately | `Unpublishing_removes_the_article_from_the_public_reads_immediately` |
| 5. The `anon` policies are what enforce this, not the API | R43, R44 and the `CountAsAnonAsync` assertions in the public tests; the handlers have no state predicate |

## 7. Test matrix

| Suite | Count | What it covers |
|---|---|---|
| `Convenciones.UnitTests/PublishingTests` | 10 | Sanitiser configuration (scripts, handlers, `javascript:`, images, styles, formatting, links + `rel`), the empty-after-sanitisation rule, the news state machine, required fields |
| `Convenciones.IntegrationTests/PublicApiTests` | 10 | Anonymous agreement/news reads, AC-S7 404, field-by-field allow-lists, re-sanitisation of a pre-existing script tag, publisher recorded by the database, capability split, unpublish immediacy, category uniqueness |
| `supabase/tests/constraint-tests.sql` | N1–N3 | A published article cannot be inserted; publishing requires `news.publish`; a no-op state write is not a transition |
| `supabase/tests/rls-tests.sql` | R41–R44 | The guard refuses a writer without `news.publish`; the database records the publisher; `anon` reads published news only and cannot write; `anon` reads no unpublished agreement |

## 8. Risks

- **The public surface is the only unauthenticated one.** Every field added to a public record is a
  disclosure decision; the projections exist so that decision is visible in one type.
- **Anonymous traffic is unthrottled.** Rate limiting and edge caching are not part of the MVP; a hostile
  caller can page through published content. Acceptable while the portal is small, but it is a gap.
- **Sanitisation changes stored content.** Clause and annex bodies written before this task were not
  sanitised; the public boundary re-sanitises and new writes are cleaned, but there is no backfill. With
  no production data yet that is moot — it would not be after a legacy import.
- **HtmlSanitizer is a new dependency.** A-33 had already accepted the approach ("a maintained
  allow-list sanitiser"); this is the first code that uses it.

## 9. Implementation record

### Verified by execution

| Check | Result |
|---|---|
| `dotnet build Convenciones.slnx` | 0 warnings, 0 errors |
| Unit tests | **59 passed** (49 + 10 new) |
| Integration tests | **142 passed** (132 + 10 new) |
| Web tests (Vitest) | 44 passed (unchanged; T14 consumes this API) |
| `supabase/tests/run-validation.ps1` | constraint **PASS=45 FAIL=0**, RLS **PASS=46 FAIL=0**, 18 migrations applied twice with identical schemas, `VALIDATION PASSED` |

### What the implementation found and fixed outside its own scope

1. **AC-S10 had no implementation.** The security model requires an allow-list sanitiser and names clause
   bodies; no sanitiser existed in the API, so clause bodies were stored and echoed raw. T12 introduces
   `RichText` and applies it to clause bodies, annex bodies and news bodies. That is a behaviour change in
   T8's module, recorded in ADR-026 rather than slipped in.
2. **Anonymous requests ran with the login's privileges.** `DatabaseSessionInterceptor` reset the role
   when no session existed, so the obvious `AllowAnonymous` public endpoint would have bypassed every
   `anon` policy. The role is now opt-in through `ServesPublicData()`, which keeps bootstrap (anonymous,
   but writing) on the login's role.
3. **`news.publish` was API-only.** RLS cannot see a column change, so `news.write` was enough to publish
   with a direct statement. The migration adds the guard; R41 proves it.
4. **`PatchField<T>` lived in the wrong module.** It is a general partial-update type; it moved to
   `Convenciones.Catalog`, and Legal's files gained the import. No behaviour changed.

### Deviations

- **News images are not exposed.** The schema has `primary_image_document_id` and
  `secondary_image_document_id`; serving them publicly would need a document download route, which is a
  disclosure decision. The entity does not map them, so no projection can publish them by accident.
- **No legacy categories were migrated.** The dump could not be read in this environment, and inventing
  category rows would be inventing reference data. `POST /news/categories` unblocks the admin; importing
  the real list is a data task.
- **`PATCH /news/{id}` is allowed in both states.** Editing a published article keeps its publication
  record, which is what a CMS does; the alternative would be an invented "unpublish before edit" rule.
  The audit row records the edit, and the test asserts the publisher does not change.

## 10. Left undone

- **Portal UI (T14)** — this task is the API it consumes.
- **News images / public document download** — a disclosure decision, not an oversight.
- **Legacy category import** and **full-text search** — out of scope.
- **Anonymous rate limiting** and edge caching — MVP gap, noted above.
