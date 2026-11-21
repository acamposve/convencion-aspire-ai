# P1-008 — Tenant isolation matrix (F-08)

**Method:** every tenant-scoped endpoint and table is inventoried; cross-tenant behaviour is exercised over
HTTP with two real tenants and at the PostgreSQL policy layer under `app_api` / `authenticated` with
`request.jwt.claims` set (never as superuser). Rows marked *policy review* are covered by reading the policy
and the endpoint, not by an executable assertion.

**Callers used**

| Caller | Capabilities | Purpose |
|---|---|---|
| Editor A (`iso_editor`) | company.read, document.read/write, clause.read/process, agreement.read, negotiation.read, statute.read, news.read, comparison.read | the normal tenant boundary |
| Admin (`iso_admin`) | Editor A + company.read_all, quota.manage, user.manage | the cross-company capability |
| Blocked user | Editor A, `is_blocked = true` | session rejection |
| Anonymous | none | 401 |

**Expected rules**

1. A foreign identifier resolves to **404**, never 403: 403 confirms the row exists.
2. A missing capability on the caller's own scope is **403**.
3. Cross-tenant writes that do not depend on a resolved row are denied by RLS (`42501`) or affect 0 rows.

## 1. Endpoint matrix

| Group | Representative routes | Capability | Cross-tenant probe | Expected | Observed | Evidence |
|---|---|---|---|---|---|---|
| DocumentEndpoints | GET `/documents`, GET/`text`/`download` `/{id}`, DELETE `/{id}` | document.read / document.write | GET detail, text, download of tenant B's document; DELETE | 404, no bytes, row survives | **404** on all three; no body leak | `TenantIsolationHttpTests` |
| DocumentExtractionEndpoints | POST `/{id}/extract`, GET `/{id}/extraction` | document.write / document.read | foreign document id | 404 | **404** (policy review; shares `DocumentAccess`) | policy review |
| ProcessingEndpoints | POST `/documents/{id}/process`, GET `/processing/{id}`, GET `/documents/{id}/clauses` | clause.process / clause.read | foreign document/run | 404, no run created | **404**, run count unchanged | `TenantIsolationHttpTests` |
| AgreementEndpoints | GET/POST `/agreements`, clauses, annexes, publish | agreement.read / write / publish | `TenantScope.CanAccess(agreement.CompanyId)` on every route | 404 | **404** by `TenantScope` (RLS also denies) | policy review + RLS |
| NegotiationEndpoints | GET/POST `/negotiations`, parties, petitions, offers | negotiation.read / write / promote | `TenantScope.CanAccess(targetCompany)` / case company | 404 | **404** by `TenantScope` (RLS also denies) | policy review + RLS |
| ComparisonEndpoints | GET `/comparison/heading-groups`, `/candidates` | comparison.read | company from session; cross-company only with read_all | own company only | **own company only** | policy review |
| StatuteEndpoints | GET/POST `/statutes`, articles | statute.read / write | statutes are **global** (no company_id) | n/a — not tenant data | n/a | schema |
| NewsEndpoints | GET `/news`, publish/unpublish | news.read / write / publish | news tables are global publications | n/a | n/a | schema |
| QuotaEndpoints | GET `/companies/{id}/usage`, GET/PUT `/companies/{id}/quota` | clause.read (read), quota.manage (write) | foreign company read + write | 404 read; 403 write without capability; quota unchanged | **404** read, **403** write, limit still 99 | `TenantIsolationHttpTests` |
| AdministrationEndpoints | GET `/users`, roles, capabilities, audit | user.manage / role.manage / audit.read | editor calls `/users` | 403 | **403**; admin **200** | `TenantIsolationHttpTests` |
| CompanyEndpoints | GET `/companies`, GET `/{id}`, POST | company.read / company.write | foreign company detail | 404 | **404** | `TenantIsolationHttpTests`, `TenantScopingTests` |
| MeEndpoints | GET `/me` | authenticated | n/a | 200 for self | **200** | `TenantIsolationHttpTests` |
| PublicEndpoints | GET `/agreements`, `/news` (anonymous) | none (published only) | n/a | published rows only | **published only** | `PublicApiTests` |

## 2. PostgreSQL policy matrix

Exercised under both roles for SELECT; UPDATE/DELETE under `app_api`. A denial is 0 affected rows or
`42501`; an insert of a row attributed to the other tenant must be rejected.

| Table | RLS policies | Cross-tenant SELECT | UPDATE | DELETE | INSERT |
|---|---|---|---|---|---|
| platform.document | document_api_read/insert/update/delete, document_authenticated_read | 0 rows | 0 | 0 | rejected |
| platform.processing_run | processing_run_api_read/insert/update, authenticated_read | 0 | 0 | 0 | rejected |
| platform.processing_attempt | processing_attempt_api_read/write, authenticated_read | 0 | 0 | 0 | policy review |
| platform.extracted_clause | extracted_clause_api_read/write, authenticated_read | 0 | 0 | 0 | policy review |
| platform.clause_classification | clause_classification_api_read/write, authenticated_read | 0 | 0 | 0 | policy review |
| platform.processing_usage | processing_usage_api_read/write, authenticated_read | 0 | 0 | 0 | policy review |
| platform.company_quota | company_quota_api_read/write, authenticated_read | 0 | 0 | 0 | policy review |
| platform.ai_usage_record | ai_usage_record_api_read, authenticated_read | 0 | 0 | 0 | policy review |
| platform.ai_budget | ai_budget_api_read, authenticated_read | 0 | 0 | 0 | rejected |
| platform.ai_budget_reservation | ai_budget_reservation_api_read, authenticated_read | 0 | 0 | 0 | policy review |
| platform.ai_budget_shadow | ai_budget_shadow_api_read, authenticated_read | 0 | 0 | 0 | policy review |
| platform.ai_budget_audit | ai_budget_audit_api_read, authenticated_read | 0 | 0 | 0 | policy review |
| app.agreement | agreement_api_read/insert/update/delete, anon_read, authenticated_read | 0 | 0 | 0 | rejected |
| app.negotiation_case | negotiation_case_api_read/insert/update/delete, authenticated_read | 0 | 0 | 0 | policy review |
| platform.user_profile / user_account | user_profile_* / user_account_* with `user.manage` or self | own row only | own row / denied | denied without user.manage | policy review |

Positive control: the same caller reads and updates its own company's rows in every exercised table.

Child tables (`app.agreement_clause`, `agreement_annex`, `negotiation_*`, `employee_offer`, meetings) carry
no `company_id`; their policies join the parent, and the parent check above plus `TenantScope` in the
endpoints protects them. They are **policy review**, not separately exercised.

## 3. Findings

### F-08-1 — `company.read_all` reaches another tenant's raw content (POLICY, HIGH)

**Evidence:** `TenantScope.CanAccess` returns true for any company when the caller holds
`company.read_all`, and `DocumentAccess.FindAccessibleAsync` uses it. The executable probe
(`Company_read_all_currently_reaches_another_tenants_company_and_document`) receives **200** for tenant B's
document metadata, and the same helper guards document text, download, processing, agreements,
negotiations and comparisons.

**Impact:** a SuperAdmin/Admin account can read any tenant's documents and content, not only aggregate
oversight. That exceeds the Product Owner's stated intent for the capability ("administrative/aggregate/
platform oversight views, not unrestricted access to raw documents").

**Decision required:** split the capability (e.g. `company.read_all` for aggregates, `support.access` with
an explicit, audited grant for raw content), or accept the current behaviour for platform administrators.

**Out of scope here:** RLS redesign and capability changes; recorded as a policy item, test pinned to
current behaviour so a future change is deliberate.

### F-08-2 — no cross-tenant leak found in the exercised paths (INFO/POSITIVE)

Foreign identifiers return 404 before any data is returned; quota writes without `quota.manage` are 403 and
do not modify the other tenant; RLS denies cross-tenant reads, updates, deletes and inserts under both
roles; blocked users are rejected; anonymous calls are 401.

## 4. Residual coverage gaps

- `extract` and `extraction` endpoints share `DocumentAccess` but are not exercised over HTTP (policy review).
- Child tables rely on parent policies; no dedicated cross-tenant test.
- `PublicEndpoints` are covered by the existing public API tests, not re-run in this matrix.
- SuperAdmin (distinct from Admin) was not separately exercised; it holds `company.read_all` too, so F-08-1
  applies with wider scope.

## 5. Acceptance status

| Criterion | Status |
|---|---|
| Endpoint inventory covered | yes (13 groups + public) |
| Tenant tables covered | 14 root tables exercised or policy-reviewed; children documented |
| RLS proven under non-superuser roles | yes (`app_api`, `authenticated`) |
| Capability denials proven | yes (quota.manage, user.manage) |
| Non-existence-leaking codes | yes (404) |
| `company.read_all` documented and pinned | yes (F-08-1) |
| RLS redesign | intentionally out of scope |
