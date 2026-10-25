# T11 — Negotiation workflow: Q-4 resolution and implementation design

**Status:** **implemented** — this document is the analysis that preceded T11 and the design that was
authorised. The implementation record, with the deviations from the authorised decisions, is
[`task-11-negotiation.md`](task-11-negotiation.md) §9; reproduced here because they contradict this
plan: reopening a **promoted** case is refused rather than allowed with the link preserved (the schema
CHECK and A-21 leave no other option), and the party requirement (D9) is **not** enforced because the
decision was not taken. Prepared against `task/23-legal-library` (`febf1d3` → `906270b`), when the
negotiation module did not exist yet.

Reading key for every rule in this document:

- **[Documented]** — stated in an approved artifact (a Q-resolution, an accepted approval item, the
  contract, the schema).
- **[Proposed]** — stated in `domain-model.md` / `api-contracts.md`, both of which are still marked
  *"proposed for approval"*, or recommended here.
- **[Legacy]** — observed in the legacy PHP/MySQL, evidence only.
- **[Conflict]** — two documents disagree; the disagreement is reported, not resolved.

---

## 1. Evidence

### 1.1 Requirements, decisions and contract

| Evidence | Where | What it establishes |
|---|---|---|
| **Q-4** five stages | `docs/architecture/decisions.md:21`, `:352` | `Open, InProgress, Pending, Agreed, Closed`; `Promoted` is no longer a state; promotion is recorded by `promoted_agreement_id` **while the case sits in `Closed`** |
| **Q-5** petition outcomes | `decisions.md:22`, `:353` | `Open, Agreed, Rejected, Withdrawn`, already implemented in the enum |
| **A-21** promotion is terminal | `decisions.md:86` | An agreement cannot be reopened after promotion; reopening would need explicit versioning |
| **A-24** repeats of promote return 409 | `decisions.md:95` | No idempotency-key machinery |
| **ADR-014** atomic, idempotent, terminal | `decisions.md:212-214` | One transaction across both modules; second attempt 409; a promoted case cannot reopen; fixes legacy duplicate-clause defect (BR-11) |
| **Q-17** promotion semantics | `decisions.md:365` | "ADR-014: atomic, idempotent, terminal" — marked **assumed**; it does not say *what content* is copied |
| **Contract §4.6** | `docs/architecture/api-contracts.md:359-375` | The endpoint list, `negotiation.promote` for promotion, `409 invalid_state` if the case is not `Closed`, `409 conflict` if already promoted; `PATCH` petition refused when `Agreed` (INV-N3) |
| **Error catalogue** | `api-contracts.md:56-68` | `409 invalid_state` exists for "close without agreements, reopen when promoted"; `403 capability_required`; `409 conflict`; `409 concurrent_modification` |
| **Task T11** | `docs/tasks/task-11-negotiation.md` | Six acceptance criteria, including DB-level enforcement and atomic rollback |
| **Domain model** | `docs/architecture/domain-model.md:300-311` | INV-N1…INV-N7 — **proposed**, and three of them are stale (see §2.3) |
| **MVP scope** | `docs/architecture/mvp-scope.md:131` | F-15 "Negotiation case lifecycle — in scope by your decision" |
| **Security model** | `docs/architecture/security-model.md:137`, `:257-265`, `:357` | Negotiator holds `negotiation.read/write/promote`; party data is personal; transitions are audited "actor, from → to" |

### 1.2 The schema as it stands

`supabase/migrations/20260101000700_app_negotiation.sql`, amended by `20260102000000_align_product_decisions.sql`:

- **`app.negotiation_case`**: `company_id` NOT NULL (FK → company RESTRICT), `started_on` date NOT NULL,
  `state` `app.negotiation_case_state`, `closed_at`, `promoted_agreement_id` (FK → agreement RESTRICT),
  audit columns.
- **CHECK `negotiation_case_closed_consistency`** — `(state = 'Closed') = (closed_at is not null)`.
- **CHECK `negotiation_case_promoted_consistency`** — `promoted_agreement_id is null or state = 'Closed'`
  (`20260102000000:113-118`). This is the constraint T11 AC 4 refers to. Note its shape: it constrains the
  **new row**, not the transition.
- **Unique index `negotiation_case_promoted_agreement_key`** — on `promoted_agreement_id` where not null
  (`…0700:37-38`). This makes *an agreement* the product of at most one case; it does **not** stop *a
  case* from acquiring a second agreement.
- **Guard trigger `negotiation_case_promotion_guard`** — `BEFORE UPDATE OF promoted_agreement_id`, body
  `app.guard_case_promotion()` (`20260102000000:126-144`):

  ```sql
  if new.promoted_agreement_id is not null
     and old.promoted_agreement_id is null
     and not platform.has_capability('negotiation.promote') then
      raise exception 'capability negotiation.promote is required …' using errcode = 'insufficient_privilege';
  end if;
  ```

  Three consequences, all verified by reading the definition:
  1. It fires **only on UPDATE**, never on INSERT.
  2. It fires only when `old.promoted_agreement_id is null`; **re-pointing** a promoted case at a
     different agreement needs no capability.
  3. It does not require `old.state = 'Closed'`, so **closing and promoting in the same statement**
     satisfies the CHECK and is never refused — the database does not yet enforce "promoted from an
     already-closed case".
- **Children**: `negotiation_party`, `union_petition` (unique `(case_id, number_int)`), `employer_offer`
  (unique `(petition_id, number_int)`), `negotiation_meeting`, `meeting_agreement` (two composite FKs
  forcing petition and meeting to the same case, `…0700:226-229`).
- **RLS**: the negotiation tables have the generic capability-only policies from
  `20260101001000_rls_policies.sql:148-153` — read = active user, write = `negotiation.write`, **no
  company predicate**. Only `document`, `agreement`, `agreement_clause` and `agreement_annex` are
  company-scoped (`20260102000100`, `20260104000100`). This is a gap T11 must close (§5.4).
- **Existing tests**: `constraint-tests.sql` D1–D7 cover composite FKs, `closed_at` consistency, the
  "promoted implies Closed" CHECK and the removal of `Promoted`; `rls-tests.sql` R16/R17 cover
  "`negotiation.write` needed" and "`negotiation.promote` holder can promote a closed case". Nothing
  covers promotion twice, promotion from a non-Closed case via SQL, or reopening a promoted case.

### 1.3 Legacy behaviour (evidence, not a specification)

| Behaviour | Evidence | Limitation |
|---|---|---|
| Closing **was** promotion: one submit inserted an agreement, copied every `acuerdos` row into `articulos_contratos` as a clause, set the status to closed, then created a `0777` directory | `lib/funciones/php/contratos/discusion/cerrar_discusion.php:28-51`; `docs/legacy/business-rules.md:240` (BR-11) | The agreement was created with literal placeholders `"*Inicio"`, `"*Fin"`, `"*Duración"`, `"*Ambito"` — the case had no validity period to give it; no validation, no transaction |
| Reopening only rewrote the status | `reabrir_discusion.php:7` (`actualizarDiscusiones_status($id, 1)`) | No idempotency guard: **reopen + close again created a second agreement and duplicated every clause** (BR-11) — the defect ADR-014 exists to prevent |
| "At least one agreement before closing" was a **UI-only** gate ("No Existen Acuerdos en este Contrato") | `admin/…/discusion_contratos_cerrar.php:36-41`; `business-rules.md:225-236` | The handler `cerrar_discusion.php` accepts a POST with no such check; a closed case stayed fully editable because edit/delete handlers check nothing |
| Status codes were `''`, `1`, `10`; `1` labelled "Modificación" and written only by reopen | `business-rules.md:204-216`; `lib/objetos/discusion.php:399-410` | Ambiguous: `1` reads as "open" *and* "modified"; no case in the dump reached `10` |
| Petition `estatus_peticion` was `''` or `9`; `9` set when an agreement was produced, cleared by "reopen petition" | `business-rules.md:269-272`; `discusion.php:1126-1190` | No rejected/withdrawn outcome existed; a closed case's petition could be "reopened" with no check on the case state |
| Parties were 12 fixed columns with `NOT NULL DEFAULT ''` | `presenci_ccol.sql`, `bitacoras` DDL | No enforcement of "at least one union and one employer party"; an empty-string party is indistinguishable from an absent one |
| No promotion record existed | `docs/database/legacy-to-target-mapping.md:128` | Nothing linked a case to the agreement it produced, so a second close could silently duplicate it |

**Conclusion from legacy:** close and promotion were one operation because there was no state to hold
"agreed but not yet formalised". Q-4 splits them, which is the only way to make promotion atomic and
idempotent. Legacy gives evidence for two preconditions (≥ 1 agreement before closing; petitions with an
agreement cannot be edited) and **no evidence** for what `Agreed` or `Pending` mean.

### 1.4 Existing implementation conventions T11 must follow

- **Aggregate + handlers** (Art. IV.4): distinct command and query handler types per use case; queries
  project from `IQueryable` with `AsNoTracking` (see `src/Convenciones.Legal/`).
- **Persistence**: one `DbContext` (`src/Convenciones.Api/Infrastructure/ConvencionesDbContext.cs`);
  modules reference it only through their own store interface (`IStatuteStore`/`StatuteStore`).
- **Optimistic concurrency**: `xmin` mapped as a concurrency token, `If-Match` required on `PATCH`/`DELETE`,
  stale → `409 concurrent_modification` (T8 entities).
- **Transitions**: `POST …/publish` takes no `If-Match`, returns **200** with the resource, and is refused
  with `409 invalid_state` (T8). T11's transitions should match.
- **Errors**: RFC 9457 with `code`, `status`, `correlationId`; codes in
  `src/Convenciones.Identity/ErrorCodes.cs`; unknown/unreadable bodies → `400 malformed_request`.
- **Auditing**: `IAuditable` + `AuditInterceptor` (same transaction, one row per change, `changes` JSON of
  field names); `platform.audit_entry` is append-only and already has **`from_state` / `to_state`
  columns** (`20260101000200_platform_documents_audit.sql:77-78`) that the entity does **not** map and
  the interceptor does **not** fill (`src/Convenciones.Api/Infrastructure/AuditEntry.cs:26-47`).
- **Tenancy**: API scopes by `TenantScope` (404 for another company), database scopes with RLS.
- **Tests**: disposable PostgreSQL container per class, Shouldly, DB assertions through Npgsql, and
  database-level assertions that bypass the API (`AgreementTests`, `StatuteTests`).

---

## 2. Q-4: the ambiguity, answered from evidence

### 2.1 The five questions

**Q1 — Does Q-4 establish that `Agreed` is a pre-closure state and `Closed` is the state from which
promotion occurs?**
**Half yes, half inference.** Q-4 explicitly establishes `Closed` as the promotion state:
"promotion is recorded by `promoted_agreement_id` while the case sits in `Closed`"
(`decisions.md:21`, `:352`), and the contract returns `409 invalid_state` if the case is not `Closed`
(`api-contracts.md:374-375`), with the schema CHECK enforcing the same. It does **not** define `Agreed`:
no sentence anywhere says what `Agreed` means, what must be true to enter it, or that it must precede
`Closed`. Its position in the declared sequence is the only evidence, and a sequence in a decision table
is weaker than a rule.

**Q2 — Is promotion a separate operation after `Closed`, or does it close the case itself?**
**Separate, per every document that speaks to it** — but the database only half-enforces it. Contract:
promote is `409` unless the case is `Closed`. ADR-014/Q-4: promotion records a result "while the case
rests in `Closed`". The CHECK, however, evaluates the *new row*, so a single statement that sets
`state='Closed'` **and** `promoted_agreement_id` passes. To make "promotion only from an already-closed
case" a database invariant (§4), the guard must also require `old.state = 'Closed'`.

**Q3 — What must be satisfied before `Agreed` or `Closed`?**
Only one condition has any grounding: **closing requires at least one meeting agreement**. It is
INV-N2 **[Proposed]**, the schema comment repeats it as a domain invariant (`…0700:54-57`), and it
matches the one business gate legacy had (UI-only) **[Legacy]**. For `Agreed` **nothing is documented** —
neither "all petitions resolved" nor anything else. Treating it as free-form would be inventing nothing;
treating it as "all petitions closed" would invent a rule. This is decision **D1**.

**Q4 — May a promoted case be reopened?**
**No.** A-21 **[Documented]** says promotion is terminal and an agreement cannot be reopened after
promotion; ADR-014 says a promoted case cannot reopen; the contract lists "reopen when promoted" as an
`invalid_state` example (`api-contracts.md:64`). The database today prevents the *bare* reopen (the CHECK
rejects `state <> 'Closed'` while the link is set) but permits **clearing the link and reopening**, and
permits **re-pointing** the case at a different agreement. Both must be closed at the database layer
(§4).

**Q5 — Which transitions are documented, and which need a new decision?**
Documented operations: `open`, `close`, `reopen`, `promote` (contract §4.6) and the legal-state chain
(Q-4). Not documented at all:

| Gap | Why it matters | Decision |
|---|---|---|
| No operation enters **`InProgress`** | The Q-4 chain cannot be traversed with the documented endpoints | **D2** |
| No operation enters **`Pending`** | Same | **D2** |
| No operation enters **`Agreed`** | Same | **D1/D2** |
| `POST /open` has **no source state** — Q-4 removed `Draft`, so a new case is already `Open` | The endpoint is a leftover from the `Draft → Open` model | **D2** |
| Reopen **target** state undefined (`Open` or `InProgress`) | Two different lifecycles; the legacy `1` is labelled "Modificación", which supports neither conclusively | **D3** |
| Promotion **payload** undefined — the agreement needs `validFrom` (NOT NULL) and `scope` (NOT NULL), and the case has neither | Legacy used literal placeholders, which the new schema cannot accept | **D4** |
| Whether promotion **copies** meeting agreements into clauses | Legacy did; new clauses require a non-null heading, and meeting agreements' headings are nullable | **D5** |
| Petition state changes: explicit, automatic on meeting agreement, or both | Legacy set `9` automatically and "reopened" by clearing it | **D6** |

### 2.2 Answers in one line

> **Recommended interpretation (matches the preferred one):** a case is created in `Open`; the
> negotiation is worked through `InProgress` and `Pending`; `Agreed` records that the parties reached
> terms; `Closed` records that the case is administratively finished (and requires ≥ 1 meeting
> agreement); **promotion is a separate, authorised operation available only for an already-`Closed`
> case**, creates the agreement and links it, and is terminal. The database must enforce all of it,
> because today it enforces only part.

That interpretation is supported for `Closed → promote` and for "promotion is terminal", inferred for
`Agreed`-before-`Closed`, and silent on the four operational gaps below. **The gaps are business
decisions, not design choices, so they are asked before coding.**

### 2.3 Conflicts between existing artifacts (reported, not silently resolved)

| # | Conflict | Evidence | Smallest consistent fix |
|---|---|---|---|
| C1 | **INV-N4** says "closing is only allowed from `Open`", contradicting Q-4's five-stage chain (closing from `Open` would skip three states) | `domain-model.md:307` vs `decisions.md:21` | Restate as "closing is only allowed from `Agreed`" (or from the last state reached, pending **D1**) and mark the domain model amended |
| C2 | **INV-N5/N6** are written in terms of a `Promoted` **state** that Q-4 removed | `domain-model.md:308-309` | Restate: "a case with `promoted_agreement_id` is terminal"; "reopen is allowed from `Closed` only while `promoted_agreement_id` is null" |
| C3 | **INV-N7** claims the guarantee comes from "a unique index on `PromotedAgreementId`"; that index stops one agreement serving two cases, not one case acquiring two agreements | `domain-model.md:310` vs `…0700:35-38` | Add the guard clause "already promoted" (§4); keep the index |
| C4 | The guard's capability check is skipped when the link is **re-pointed** (`old` not null), and on **INSERT** | `20260102000000:126-144` | Fire the guard on INSERT and on any change of the link (§4) |
| C5 | Contract lists `/open` although no state precedes `Open` | `api-contracts.md:366` vs `decisions.md:21` | **D2** |
| C6 | Contract's `409 invalid_state` detail must "include the current state"; the error format has no state field today | task T11 AC 1 vs `ErrorCodes`/`Results.Problem` usage in T8/T10 | Add `extensions["state"]` (additive) and name the state in `detail` (§5.3) |
| C7 | Audit rows carry `from_state`/`to_state` columns that nothing writes, while the security model requires "actor, from → to" for transitions | `20260101000200:77-78`, `AuditEntry.cs:30-47`, `security-model.md:357` | Map the columns and fill them in the interceptor when the changed property is named `State` (§5.5) |
| C8 | Negotiation tables have no company predicate in RLS, while every other business aggregate is company-scoped | `20260101001000:148-153` vs `20260104000100` | Add company-scoped policies for the six negotiation tables (§5.4) |

---

## 3. Transition matrix

Notation: **[D]** documented, **[P]** proposed here (needs approval), **[?]** needs a business decision
(numbered in §7). Capability: `write` = `negotiation.write`; `promote` = `negotiation.promote`.
Every success is **200** with the case detail unless stated; every refusal is `application/problem+json`.

### 3.1 Case lifecycle

| # | From | Operation | To | Preconditions | Cap | DB invariant | Success | Failure |
|---|---|---|---|---|---|---|---|---|
| 1 | — | `POST /negotiation/cases` | `Open` | company visible to caller **[D]**; `startedOn` present **[D]**; **[P]** at least one `Union` and one `Employer` party supplied with the case (INV-N1, moved from "before open" to creation, since no pre-open state exists) **[?D9]** | write | `started_on` NOT NULL; `closed_at` null; link null | **201** + `Location` | 400 validation; 403; 404 company |
| 2 | `Open` | `POST …/open` | — | none exists — **contradiction C5** | write | — | — | **recommend: remove the endpoint [D2]** |
| 3 | `Open` | `POST …/in-progress` | `InProgress` | none defined **[?D2]** | write | `closed_at` stays null | **200** | 409 invalid_state (wrong source state) |
| 4 | `InProgress` | `POST …/pending` | `Pending` | none defined **[?D2]** | write | `closed_at` stays null | **200** | 409 invalid_state |
| 5 | `Pending` | `POST …/in-progress` | `InProgress` | resume after a hold **[?D2]** | write | `closed_at` stays null | **200** | 409 invalid_state |
| 6 | `Pending` | `POST …/agree` | `Agreed` | **[?D1]** — nothing documented; recommend: none beyond the source state | write | `closed_at` stays null | **200** | 409 invalid_state |
| 7 | `Agreed` | `POST …/close` | `Closed` | ≥ 1 `MeetingAgreement` **[P — INV-N2, legacy intent]**; sets `closed_at = now()` | write | `(state='Closed') = (closed_at not null)` | **200** | 409 invalid_state (no agreements, or wrong source state) |
| 8 | `Closed` (link null) | `POST …/reopen` | `InProgress` **[?D3]** (or `Open`) | link must be null (see 9) | write | `closed_at` cleared | **200** | 409 invalid_state |
| 9 | `Closed` (link set) | `POST …/reopen` | — | A-21/ADR-014: terminal **[D]** | write | **new:** guard refuses (link set) — today the CHECK can be bypassed by clearing the link | — | **409 invalid_state** naming the state |
| 10 | `Closed` (link null) | `POST …/promote` | stays `Closed`, link set | `validFrom`/`scope` provided **[?D4]**; optional clause copy **[?D5]**; agreement identity free (`(company, validFrom)` unique, T8) | **promote** | **new:** guard requires `old.state='Closed'`, `old.link null`, capability; CHECK `link ⇒ Closed`; unique index on link | **200** `{ agreementId }` **[?D8]** + `Location` maybe | 403 (no capability); 409 invalid_state (not Closed); 409 conflict (already promoted / agreement dates collide) |
| 11 | any ≠ `Closed` | `POST …/promote` | — | promotion requires `Closed` **[D]** | promote | CHECK (new row) + **new** guard (`old.state`) | — | **409 invalid_state** (names current state) |
| 12 | `Closed` (link set) | `POST …/promote` | — | already promoted **[D — A-24/INV-N7]** | promote | **new** guard (`old.link is not null`) | — | **409 conflict** (naming the promoted agreement) |
| 13 | `Closed` | `POST …/close` | — | already closed | write | — | — | 409 invalid_state |
| 14 | any ≠ `Closed` | `POST …/reopen` | — | reopen only from `Closed` | write | — | — | 409 invalid_state |
| 15 | any | `PATCH …/cases/{id}` (startedOn? scope?) | unchanged | **[?D7]** — editing a closed case | write | — | 200 (draft states) | 409 invalid_state when Closed |

Transitions 3–6 are the only invented *operations*; they carry **no invented business rule**. If you
prefer not to add endpoints, the alternative is a single `POST …/advance` with a target state restricted
to the next in the chain — same rule, one route.

### 3.2 Parties, petitions, offers, meetings, meeting agreements

**[?D7]** — legacy allowed editing a closed case's children and that is one of its documented defects
(`business-rules.md:232`). Proposal: every write below requires the case **not** `Closed`; deletion of a
meeting agreement stays allowed while the case is open (mistakes must be fixable), and becomes 409 once
closed.

| From | Operation | To | Preconditions | Cap | DB invariant | Success | Failure |
|---|---|---|---|---|---|---|---|
| not Closed | add/remove party | unchanged | role enum; non-blank name; email shape | write | `party_role` enum; `negotiation_party_email_valid`; CASCADE on case delete | 201 / 204 | 400; 409 invalid_state when Closed |
| not Closed | `POST …/petitions` | unchanged | positive integer number unique in case; non-blank title/body; optional heading | write | unique `(case_id, number_int)`; CHECKs | 201 | 400; 409 conflict (duplicate number) |
| not Closed | `PATCH …/petitions/{id}` | unchanged | **INV-N3 [P]**: refused when petition is `Agreed` | write | enums; CHECKs | 200 | 409 invalid_state (petition Agreed); 409 conflict (renumber) |
| not Closed | `DELETE …/petitions/{id}` | unchanged | INV-N3; CASCADE removes its offers and meeting agreements | write | CASCADE | 204 | 409 invalid_state (Agreed) |
| not Closed | petition state change `Open→Agreed/Rejected/Withdrawn`, `Agreed→Open` | changed | **[?D6]**; legacy: `9` set on agreement, cleared on repoen | write | `petition_state` enum; `union_petition_case_state_idx` | 200 | 409 invalid_state (illegal petition transition) |
| not Closed | `POST …/petitions/{id}/offers` | unchanged | offer number unique in petition | write | unique `(petition_id, number_int)`, FK CASCADE | 201 | 400; 409 conflict |
| not Closed | `POST …/meetings` | unchanged | `heldOn` present | write | NOT NULL; composite unique `(id, case_id)` | 201 | 400 |
| not Closed | `POST …/petitions/{id}/agreements` | unchanged | petition and meeting belong to **this** case | write | composite FKs; unique `(case_id, number_int)` | 201 | 400; 404 (petition/meeting not in this case); 409 conflict (number) |
| not Closed | delete meeting agreement | — | **[?D7]** | write | CASCADE | 204 | 409 invalid_state when Closed |

### 3.3 Definitions (the four operations that are easy to conflate)

- **Closing** — `Agreed → Closed`. Records `closed_at`, requires at least one meeting agreement, does
  **not** create an agreement, does **not** set `promoted_agreement_id`. The case becomes read-only.
- **Promoting** — `Closed` (link null) → same state, link set. Creates **one** agreement (header, plus
  clauses only if D5 says so), links it, requires `negotiation.promote`, is **terminal**, and is
  idempotent by refusal (second attempt 409), never by silently returning the first result.
- **Reopening** — `Closed` (link null) → working state. Clears `closed_at`, is audited with
  from/to, and is refused once the case is promoted.
- **Editing/withdrawing petitions and offers** — content changes inside an open case. Not a state
  transition of the case; a petition *outcome* change (`Agreed`/`Rejected`/`Withdrawn`) is a transition of
  the petition, and `Agreed` petitions are frozen (INV-N3).

---

## 4. Atomic promotion and database guards

### 4.1 The operation

```
PromoteCaseHandler.HandleAsync(caseId, PromoteRequest{validFrom, validTo?, scope, [copyClauses?]}, actor)
 1. load the case tracked (INotificationStore.FindAsync)          → 404 when missing/invisible
 2. require case.State == Closed                                   → 409 invalid_state (API-level clarity)
 3. require case.PromotedAgreementId is null                       → 409 conflict
 4. agreementId = agreementFactory.CreateAndAttach(companyId, validFrom, validTo, scope, actor, now)
       (Agreement.Create + db.Agreements.Add — attached, not saved)
 5. case.Promote(agreementId, actor, now)   // sets promoted_agreement_id, updated_at/by; state stays Closed
 6. store.SaveChangesAsync()                // ONE SaveChanges = ONE transaction: INSERT agreement, UPDATE case
```

`SaveChangesAsync` emits the INSERT before the UPDATE and wraps both in one transaction (EF Core's
default when more than one command is involved). The guard trigger runs on the UPDATE — i.e. **after** the
agreement INSERT, inside the same transaction — so a refusal rolls the INSERT back. The handler must be
the only writer; a second `SaveChanges` anywhere in the path would break atomicity and is called out in
the code comment.

The cross-module dependency is a port in the negotiation module (`IPromotedAgreementFactory`), implemented
in the API over the shared `DbContext` (Art. IV.6, ADR-003/A-07). This mirrors `IStatuteStore`.

### 4.2 Guard changes required (new migration, forward-only)

The present guard is insufficient for AC 4/6 and for concurrency (C3/C4). Replace the trigger with one
that fires on INSERT **and** on UPDATE of `state`/`promoted_agreement_id`:

```sql
-- app.guard_case_promotion()  (shape, not final SQL)
if tg_op = 'INSERT' and new.promoted_agreement_id is not null then
    raise 'a case cannot be created already promoted' using errcode = 'check_violation';
end if;

if tg_op = 'UPDATE' then
    -- promotion transition
    if new.promoted_agreement_id is distinct from old.promoted_agreement_id
       and new.promoted_agreement_id is not null then
        if not platform.has_capability('negotiation.promote') then
            raise 'capability negotiation.promote is required to promote a case'
                using errcode = 'insufficient_privilege';
        end if;
        if old.state <> 'Closed' then
            raise 'a case can only be promoted from Closed' using errcode = 'invalid_parameter_value';
        end if;
        if old.promoted_agreement_id is not null then
            raise 'the case is already promoted' using errcode = 'unique_violation';
        end if;
    end if;

    -- terminality: a promoted case cannot be reopened or re-pointed
    if old.promoted_agreement_id is not null
       and (new.promoted_agreement_id is distinct from old.promoted_agreement_id
            or new.state <> old.state) then
        raise 'a promoted case is terminal' using errcode = 'invalid_parameter_value';
    end if;
end if;
```

The existing CHECKs stay (they are the cheap declarative half): `closed_at` consistency and
"link ⇒ Closed". The trigger is the transition half. **Selection of SQLSTATE matters for the API's
mapping**: `insufficient_privilege` → 403; `invalid_parameter_value`/`check_violation` → 409 invalid_state;
`unique_violation` → 409 conflict. Those codes must be asserted in the database tests, not inferred from
HTTP.

### 4.3 Why this is concurrency-safe without an application lock

Two concurrent promotions both read `Closed`/link-null, both INSERT an agreement, then both UPDATE the
case. PostgreSQL serialises the two UPDATEs on the same row: the second waits for the first to commit,
then its trigger sees `old.promoted_agreement_id` set and raises; its transaction — **including its
agreement INSERT** — rolls back. Exactly one agreement survives. The same outcome is reached earlier if
both requests use the same `(company_id, valid_from)`: the second INSERT blocks on
`agreement_company_valid_from_key` and fails with 23505 → 409 conflict, and its case update never runs.

Two independent mechanisms guard the duplicate: the row-level lock + trigger, and the agreement identity
index. Neither is an application-level pre-check; both are in the database. Mapping `xmin` on
`negotiation_case` as a concurrency token adds a friendlier `409 concurrent_modification` for the same
race and is recommended for consistency with T8, but it is not what makes promotion safe.

**Limitation to record:** the transaction is per `DbContext`/connection. If a future change makes the
agreement factory save on its own or run on a second connection, the two writes stop being atomic. The
plan therefore keeps creation inside the same scoped `DbContext`, exactly as `IStatuteStore` does.

---

## 5. API, authorization, auditing

### 5.1 Endpoints (contract §4.6, plus the additions the state machine needs)

| Method | Path | Cap | Notes |
|---|---|---|---|
| `GET` `POST` | `/negotiation/cases` | `read` / `write` | list (paged, company-scoped) / create → `Open` with parties |
| `GET` | `/negotiation/cases/{id}` | `read` | case + parties + petitions + meetings (+ offers, meeting agreements) |
| `PATCH` | `/negotiation/cases/{id}` | `write` | header fields, `If-Match` **[?D7]** |
| `POST` `DELETE` | `/negotiation/cases/{id}/parties[/{partyId}]` | `write` | add / remove |
| `POST` | `/negotiation/cases/{id}/in-progress` · `/pending` · `/agree` | `write` | **[new, D2]** the three missing transitions |
| `POST` | `/negotiation/cases/{id}/close` · `/reopen` | `write` | documented |
| `POST` | `/negotiation/cases/{id}/promote` | **`promote`** | documented; body per **D4** |
| `GET` `POST` | `/negotiation/cases/{id}/petitions` | `read` / `write` | |
| `PATCH` `DELETE` | `/negotiation/cases/{id}/petitions/{petitionId}` | `write` | `If-Match`; refused when `Agreed` |
| `POST` `DELETE` | `/negotiation/cases/{id}/petitions/{petitionId}/offers[/{offerId}]` | `write` | |
| `POST` `DELETE` | `/negotiation/cases/{id}/meetings[/{meetingId}]` | `write` | |
| `POST` `DELETE` | `/negotiation/cases/{id}/petitions/{petitionId}/agreements[/{agreementId}]` | `write` | meeting agreement |

`/open` is removed (C5/D2); `InProgress`, `Pending` and `Agreed` get one operation each. If you prefer to
keep the contract exactly as it stands, the alternative is a single `POST …/advance` — but the chain then
cannot be walked with the documented endpoints, which is the real defect.

### 5.2 Authorization

- `negotiation.read` on every GET; `negotiation.write` on every other write; **`negotiation.promote`
  only on `/promote`** (contract, seed grants: negotiator, administrator, superadmin — **not** editor).
- Capability enforced twice: endpoint declaration (`RequiresCapability`, startup gate) and RLS
  (`platform.has_capability`), plus the promotion guard for the transition itself.
- Company scoping: `TenantScope` in the API, `404` for another company's case (T8/T9 convention), and
  new company-scoped RLS policies (§5.4).
- Personal data (party names, emails, phones) never appears in audit `changes`, logs or error `detail`
  (`security-model.md:257-265`). Promotion and transitions affect the case only, not party rows.

### 5.3 Error format

Existing conventions (`Results.Problem` + `ErrorCodes`) plus one additive extension:

```jsonc
// 409 on an illegal transition
{
  "type": "https://convenciones.app/errors/invalid-state",
  "title": "Invalid state",
  "status": 409,
  "code": "invalid_state",
  "detail": "The case is 'InProgress'; it can only be closed from 'Agreed'.",
  "instance": "/negotiation/cases/{id}/close",
  "state": "InProgress",            // additive extension; AC 1 requires the current state
  "correlationId": "…"
}
```

| Situation | Status | Code |
|---|---|---|
| illegal transition (wrong source state) | 409 | `invalid_state`, `state` extension |
| close without a meeting agreement | 409 | `invalid_state`, detail names the missing requirement |
| promote not `Closed` / promoted case reopened | 409 | `invalid_state`, `state` extension |
| promote already promoted | 409 | `conflict`, detail names the existing `agreementId` |
| promote without capability | 403 | `forbidden`, detail names `negotiation.promote` (middleware), or `42501` at the DB |
| another company's case | 404 | `not_found` (never 403 — it would confirm existence) |
| stale `If-Match` on PATCH/DELETE | 409 | `concurrent_modification` |
| invalid body/parameters | 400 | `validation_failed` / `malformed_request` |

### 5.4 Database scope (new migration, same as §4.2)

Company-scoped RLS for the six negotiation tables, following `20260104000100_agreement_tenant_rls.sql`:
the case by `company_id`; `negotiation_party`, `union_petition`, `negotiation_meeting` and
`meeting_agreement` through an `exists` on their parent; `employer_offer` through its petition. Without
it, a bug in a future handler could read another company's negotiation while still satisfying the
capability policy. This is the same gap T8 closed for agreements.

### 5.5 Auditing

The existing mechanism is an interceptor, not per-handler calls: `IAuditable` gives module/entity/id, the
`AuditInterceptor` records `created`/`updated`/`deleted` with a JSON of changed property names **in the
same transaction**, and `platform.audit_entry` rejects UPDATE/DELETE. What is missing is state:

- map `from_state` / `to_state` on `AuditEntry` (columns already exist, `…0200:77-78`);
- in the interceptor, when the changed property is named `State` (all three stateful aggregates use that
  name), set `from_state`/`to_state` from `OriginalValue`/`CurrentValue`;
- keep the action vocabulary (`created`/`updated`); the transition is identified by from/to plus the
  changed fields (`ClosedAt`, `PromotedAgreementId`);
- audit module value `negotiation` is already allowed by `audit_entry_module_allowed`.

Tests then assert: close → one row with `from_state='Agreed'`, `to_state='Closed'`, actor and
`ClosedAt` in `changes`; reopen → `Closed → InProgress` with `ClosedAt` cleared; promote → `Closed →
Closed` with `PromotedAgreementId` in `changes` and the actor set.

---

## 6. Test plan

**Unit (`Convenciones.UnitTests`) — the state machine only, no server, no container**

1. Every legal transition in §3.1 produces the expected state/fields.
2. Every illegal transition raises a rule exception with the right kind (validation / conflict /
   invalid state) — including close from `Open`, reopen from `Agreed`, promote from every state ≠ `Closed`.
3. Close requires ≥ 1 meeting agreement; reopen clears `closed_at`; promote sets the link and leaves the
   state `Closed`; promote twice returns conflict; promote on a promoted case after reopen is refused.
4. Petition `Agreed` is immutable (INV-N3); petition outcome transitions per **D6**.
5. Party requirements (INV-N1) per **D9**.

**Integration (`Convenciones.IntegrationTests`) — disposable PostgreSQL, real API pipeline**

6. Full happy path: create → in-progress → pending → agree → close (with agreement) → promote; assert the
   agreement row, the link, the case state, and the audit `from_state/to_state` of each step.
7. **403 without `negotiation.promote`** on `/promote` (a caller holding `negotiation.write`), and the
   case/agreement unchanged — this is the API half; the DB half is test 10.
8. **409 invalid_state with the current state** for each illegal transition over HTTP, including
   close-without-agreements, promote-when-`Agreed`, reopen-when-promoted.
9. **Already promoted**: second `/promote` → 409 `conflict`, one agreement, one link.
10. **Database guard independently of the API**: as `app_api` with a non-promote token,
    `update app.negotiation_case set promoted_agreement_id = …` on a `Closed` case →
    `PostgresException` `42501` with the capability message (assert SQLSTATE/constraint, not HTTP).
    Also: promote on a non-`Closed` case → the guard's invalid-state SQLSTATE; INSERT with a link →
    refused; clearing the link of a promoted case → refused.
11. **Atomic rollback under forced failure**: install a temporary `BEFORE UPDATE` trigger on
    `app.negotiation_case` that raises, call `/promote`, then assert **no agreement row** and **no link**
    (HTTP 5xx/409 is not the assertion — the row counts are). Drop the trigger in a `finally`.
12. **Concurrency**: two clients call `/promote` simultaneously (two `ApiFactory` hosts or two
    `HttpClient`s); assert exactly one 200, one 409, **one** agreement, link = that agreement. Repeat
    with identical `validFrom` to exercise the unique-index path as well.
13. **Cross-company**: another company's case is 404 on every route (read, write, promote).
14. **Tenant RLS** (database): a company-B caller sees 0 rows of company A's case and children.
15. **Personal data**: party names/emails/phones never appear in `audit_entry.changes`, error `detail`
    or logs (assert the JSON of the audit rows).
16. Petitions/offers/meetings/meeting agreements: create, update, delete; duplicate numbers → 409;
    petition from another case in a meeting agreement → 404/409; `Agreed` petition edit → 409.
17. **Regression**: the whole existing suite stays green (140 .NET + 44 web at this commit); no existing
    endpoint changes behaviour.

**Database suite (`supabase/tests`)**

18. `constraint-tests.sql`: new D-section assertions for the hardened guard — promotion from non-`Closed`,
    insert-with-link, re-pointing, clearing a promoted link, and the closed/reopen `closed_at` round trip.
    The existing D4/D5/D7 stay.
19. `rls-tests.sql`: company-scoped reads/writes for the negotiation tables (the R34/R35 pattern), plus a
    promotion-capability assertion (`42501` for a write-only caller, allowed for a negotiator).

**What is not claimed:** no test asserts a database guarantee through a mocked repository or a unit test.
Every DB-level claim above is exercised through Npgsql against the real container.

---

## 7. Unresolved business decisions (approval needed before coding)

| ID | Decision | Recommendation | Impact if different |
|---|---|---|---|
| **D1** | **What must be true to reach `Agreed`?** Nothing is documented. | No precondition beyond the source state: `Agreed` is an explicit act by an authorised user. Do **not** invent "all petitions resolved". | If it must require all petitions resolved (no `Open` petitions), the rule is one line in `Agree()` plus a test — but it is a business rule nobody wrote |
| **D2** | **How do `InProgress`, `Pending` and `Agreed` get entered, and what happens to `/open`?** The chain cannot be walked with the documented endpoints, and `/open` has no source state. | Create the case in `Open`; add `/in-progress`, `/pending`, `/agree` (no preconditions); remove `/open`. | A single `/advance` endpoint is the same rule with one route; keeping `/open` requires reintroducing `Draft`, which contradicts Q-4 |
| **D3** | **Reopen target state:** `InProgress` or `Open`? | `InProgress` — the case is past its creation and work resumes. | Choosing `Open` makes `InProgress` reachable twice per lifecycle and weakens the chain's meaning |
| **D4** | **Promotion payload:** the agreement needs `validFrom` (NOT NULL) and `scope` (NOT NULL); the case has neither (legacy wrote placeholders). | `/promote` body: `validFrom` (required), `validTo?`, `scope` (required), mirroring `POST /agreements`. | Deriving from `started_on` would invent a validity rule; a headerless agreement is impossible |
| **D5** | **Does promotion copy meeting agreements into agreement clauses?** Legacy did (BR-11); new clauses require a non-null heading, and meeting-agreement headings are nullable (legacy hardcoded 0). | **Header-only promotion for the MVP**: the case's agreements are the negotiation record; clauses are authored in T8's workspace. Copy is a separate feature (R2-03 prints the case). | If clauses must be copied: only classified meeting agreements can be (heading required) — an unclassified one would block promotion, which contradicts A-12's spirit |
| **D6** | **How do petition outcomes change?** Legacy set "agreed" automatically when an agreement was produced and cleared it on reopen. | Explicit `PATCH` with `state`: `Open → {Agreed, Rejected, Withdrawn}` and `Agreed → Open`; **and** recording a meeting agreement transitions that petition to `Agreed` automatically, as legacy did. | Automatic-only would make `Rejected`/`Withdrawn` unreachable; explicit-only diverges from legacy without a requirement |
| **D7** | **Are child writes allowed on a `Closed` case?** Legacy allowed them (documented defect). | Refuse with 409 `invalid_state` while `Closed` (reopen first). | Allowing them would let a promoted case's source record change after the agreement exists |
| **D8** | **Promotion response:** contract says "Returns `{ agreementId }`". | **200** with `{ agreementId }` (and the case detail if you prefer); no `Location`, matching T8's `publish` being an action returning 200. | 201 + `Location` is defensible but diverges from the contract's wording |
| **D9** | **INV-N1 (party requirement) location:** the domain model says "before a case can open", but nothing precedes `Open`. | Enforce at **creation**: at least one `Union` and one `Employer` party supplied with the case. | Allowing creation without parties makes `Open` a draft in disguise, which Q-4 removed |

Until D1–D9 are answered, Q-4 is **resolved for promotion** (`Closed` is the promotion state, promotion is
separate and terminal) and **ambiguous for the middle of the chain**. I am not coding against guesses.

---

## 8. Recommended implementation sequence and affected files

1. **Decisions D1–D9** (this document) — no code until answered.
2. **Migration** `supabase/migrations/<ts>_negotiation_workflow.sql`: hardened promotion guard (§4.2) and
   company-scoped RLS (§5.4). Write its database tests first (constraint D-section, RLS section) so the
   database guarantee is red before the code exists.
3. **Domain module** `src/Convenciones.Negotiation/`: `NegotiationCase` (aggregate with parties,
   petitions, offers, meetings, meeting agreements), `CaseState`, `PetitionState`, `PartyRole`,
   `NegotiationRuleException`, EF configuration, contracts, command handlers (create, add/remove party,
   transitions, petitions, offers, meetings, meeting agreements) and query handlers (list, detail).
   Ports: `INegotiationStore`, `IPromotedAgreementFactory`.
4. **API**: `NegotiationEndpoints.cs`, `NegotiationStore.cs`, `PromotedAgreementFactory.cs`, registration in
   `Program.cs` and `ConvencionesDbContext.cs`.
5. **Auditing**: map `from_state`/`to_state`, extend `AuditInterceptor` for the `State` property, and add
   the audit assertions (test 6).
6. **Tests**: unit state machine, then integration (§6), then database suite additions.
7. **Docs**: `api-contracts.md §4.6` (endpoints, preconditions, the `state` extension, D2/D4/D8),
   `decisions.md` (an ADR for the state machine + the three stale invariants corrected), `domain-model.md`
   INV-N4/N5/N6/N7 restated, `target-schema.md` note, `task-11-negotiation.md` execution record.

Estimated shape: one module (~12 files), one migration, one endpoint file, two API infrastructure files, a
test fixture and two test classes, plus documentation. No change to T8's agreement module is required
beyond calling `Agreement.Create` through the factory port.
