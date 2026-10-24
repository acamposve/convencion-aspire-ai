# Proposal — schema changes required before the document workflow

**Status: awaiting approval. Nothing here is applied.** Per the Phase 5 working method, the schema is
not changed without a proposal and a justification, so this document is the gate.

Two independent batches. **A** is the set of decisions you already made; **B** is new and needed by
the first vertical slice.

---

## A. Outstanding deltas from the Task 6 decisions

These are your approved decisions (Q-1, Q-2, Q-4, Q-9) that the merged schema does not yet reflect.
Listed in [decisions.md §7](../architecture/decisions.md) and not applied then so they could land as
their own reviewable migration.

| Delta | Decision |
|---|---|
| Drop `number_suffix` from `agreement_clause`, `statute_article`, `union_petition`, `employer_offer`, `meeting_agreement` | Q-2 — clause numbers are integers |
| Replace `app.negotiation_case_state` with `Open, InProgress, Pending, Agreed, Closed` | Q-4 |
| Retarget `app.guard_case_promotion()` to fire on `promoted_agreement_id` | Q-4 (no `Promoted` state) |
| Drop `agreement.title` and `agreement.number` | Q-1 — title is composed from company + period |
| Seed five roles: `superadmin`, `administrator`, `editor`, `transcriptor`, `negotiator` | Q-9 |

**Why it matters for the slice:** Q-9 changes the role set, and the slice's authorisation tests assert
against roles. Building authentication on four roles and then reworking it is wasted work.

**Proposed migration:** `20260102000000_align_product_decisions.sql`, one forward migration. No data
migration is required — no environment has ever held a row outside a disposable container.

---

## B. New schema needed by the document workflow

The slice requires three things the current schema does not express. Each is proposed separately so it
can be accepted or rejected on its own merits.

### B.1 A document must belong to a company — **required**

**Problem.** `platform.document` has no owner. It carries `uploaded_by` but no business scope, so
"documents this user may see" has no predicate. The workflow step *"identify the company or tenant
they can access"* cannot be enforced without it.

**Proposal.**

```sql
alter table platform.document
    add column company_id uuid null references app.company (id) on delete restrict on update restrict;

create index document_company_idx on platform.document (company_id) where deleted_at is null;
```

Nullable rather than mandatory: a document may be uploaded before it is attached to a company
(statutes and news images have no company). The workflow requires a company, so the API rejects
`null` for contract uploads — a domain rule, not a schema rule, because the schema serves all document
kinds.

**RLS.** Two policies change:

```sql
-- read: same company, or an administrator
using (
    platform.is_active_user()
    and (
        company_id is null
        or company_id = (select up.company_id from platform.user_profile up
                         where up.user_id = platform.current_user_id())
        or platform.has_capability('company.read')
    )
)
```

**This is a real isolation decision, and it exposes an unresolved one:** [A-37](../architecture/decisions.md)
asks whether negotiators are scoped to their own company. The policy above scopes *everyone* to their
company unless they hold `company.read`. If you want scoping to apply only to negotiators, the policy
needs a capability check instead of a blanket rule. **This needs your answer before B.1 is applied.**

### B.2 Processing state must be auditable — **required**

**Problem.** `platform.document` already has `extraction_status`
(`NotRequested | Requested | InProgress | Completed | Failed`). It has nowhere to record *why* a
extraction failed, *how many times* it was attempted, or *when*. Without those, "controlled retries"
and "traceability" from the Phase 5 requirements are not implementable, and a failed extraction is
indistinguishable from one that never ran.

**Proposal.**

```sql
alter table platform.document
    add column extraction_attempts   integer     not null default 0,
    add column extraction_started_at timestamptz,
    add column extraction_completed_at timestamptz,
    add column extraction_error      text;

alter table platform.document
    add constraint document_extraction_attempts_non_negative check (extraction_attempts >= 0),
    add constraint document_extraction_error_only_on_failure
        check (extraction_error is null or extraction_status = 'Failed');
```

**Why columns and not a status table:** an extraction history table would be the right shape if
retries were numerous or concurrent. They are neither — one document has one current extraction, and
the audit trail already records each transition (`platform.audit_entry`). Columns keep the read path
simple and the audit trail authoritative.

### B.3 Extracted text must be stored somewhere — **required**

**Proposal.** A separate table, not a column on `document`:

```sql
create table platform.document_text (
    document_id  uuid        primary key references platform.document (id) on delete cascade on update restrict,
    content      text        not null,
    char_count   integer     not null,
    extracted_at timestamptz not null default now(),

    constraint document_text_char_count_matches check (char_count = length(content))
);
```

**Why a separate table.** Extracted text can be hundreds of kilobytes. Putting it on `document` would
make every document listing — which selects metadata for every row — pull the full text whether or not
it is needed. A one-to-one side table keeps `document` narrow and lets the text be fetched on demand.

`char_count` is stored *and* constrained to equal `length(content)`, so it cannot drift. That is
deliberate: the legacy schema stored counters that were maintained by application code and silently
went wrong (DS-29).

### B.4 Not proposed

| Idea | Why not |
|---|---|
| Page images / thumbnails | No requirement yet; the workflow needs text, not renderings |
| Full-text search index on the extracted text | The slice retrieves *one* document's text. Search is R2-02 |
| A `document_kind` enum | The workflow handles one kind. Add it when a second kind needs different validation |
| Storing the original PDF bytes in the database | Files belong in Supabase Storage (ADR-017) |
| AI-derived data (embeddings, classifications) | Explicitly out of scope for this slice |

---

## C. What is required to unblock the next task

1. **Approve or reject B.1, B.2, B.3** individually.
2. **Answer A-37:** is company scoping for everyone, or only for negotiators?
3. **Confirm A** is applied as one migration together with B, or separately.

On approval these become `20260102000000_align_product_decisions.sql` (A) and
`20260102000100_document_processing.sql` (B), with the RLS policies updated in the same PR and the
constraint and RLS suites extended and re-run — the same validation discipline as Phase 4.
