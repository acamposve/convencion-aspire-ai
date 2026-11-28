-- =====================================================================================
-- 20261128000200 — published agreements are readable across companies (#138b)
--
-- The comparison workspace stops being "my agreements" and becomes a directory of published
-- agreements: the client compares his convention against another company's, which is what the
-- original system did and what the screen was asked to reproduce.
--
-- Row-level security, not the query, is what makes that safe. Until now `agreement_api_read` required
-- `can_access_company(company_id)`, so a handler that forgot the tenant predicate would simply read
-- nothing. This migration keeps that policy exactly as it is and **adds** a second, narrower one:
-- `app_api` may also read a row whose state is Published. Policies for the same command are combined
-- with OR, so the result is "mine, or published".
--
-- What this deliberately does NOT do:
--   * it does not touch insert/update/delete — writing stays tenant-scoped and capability-gated;
--   * it does not relax `authenticated`, so a direct Supabase client is unaffected;
--   * it does not widen any other module (documents, audit, negotiation, news);
--   * it never admits a Draft: the predicate is the publication state alone, with no OR that could
--     let an unpublished row through.
--
-- Clause visibility follows its agreement for the same reason it did before: the child policy is an
-- EXISTS over the parent, and the parent policies are what decide which agreements are visible.
--
-- Reversible: drop the two `*_published` policies; the pre-existing tenant policies remain untouched.
-- =====================================================================================

-- ---- agreement ----------------------------------------------------------------------------

create policy agreement_api_read_published on app.agreement
    for select to app_api
    using (
        platform.is_active_user()
        and state = 'Published'::app.publication_state
    );

-- ---- agreement_clause ---------------------------------------------------------------------
--
-- A clause of a published agreement is readable for the same reason the agreement is. The EXISTS is
-- evaluated under the caller's policies, which now admit published parents.

create policy agreement_clause_api_read_published on app.agreement_clause
    for select to app_api
    using (
        platform.is_active_user()
        and exists (
            select 1 from app.agreement a
            where a.id = agreement_clause.agreement_id
              and a.state = 'Published'::app.publication_state
        )
    );
