# T11 — Negotiation workflow API

**Status:** implemented · **Depends on:** T8 · **Blocks:** T14

## 1. Objective

Run a negotiation from case to promoted agreement, with the five stages from Q-4 and the database guard
on promotion. The hard part was not the CRUD: it was deciding what each state means, what promotion may
assume, and making the database enforce the same rules the aggregate does.

## 2. Scope

**In:** case lifecycle `Open → InProgress → Pending → Agreed → Closed` (Q-4); parties; petitions with
outcomes `Open/Agreed/Rejected/Withdrawn` (Q-5); employer offers; meetings and meeting agreements;
close/reopen; **promotion** creating an agreement atomically and recording `promoted_agreement_id` while
the case rests in `Closed`.

**Out:** negotiation history and print (R2-03); quantified economic outcomes (Q-3: free text); copying
meeting agreements into agreement clauses (T11 D5 — the case is the negotiation record, clauses are
authored in the agreement workspace).

## 3. Decisions

The design report is `docs/tasks/T11-negotiation-design.md`; the decisions were applied as authorised,
with two exceptions recorded in §9.

| # | Decision | Where it lives |
|---|---|---|
| D1 | `Agreed` is explicit, with no invented preconditions. `close` requires ≥ 1 meeting agreement: the rule is named by `api-contracts.md §2.1` ("close without agreements") and INV-N2, and the legacy server never enforced it (UI message only) — recorded with that distinction in ADR-025 | `NegotiationCase.Close`, ADR-025 |
| D2 | Cases are created in `Open`; `/open` does not exist; `/in-progress`, `/pending` and `/agree` are the explicit operations | `NegotiationEndpoints`, `NegotiationCase` |
| D3 | Reopen returns `Closed → InProgress`, clears `closed_at`, authenticates the actor through the audit row, preserves the promotion link and prevents re-promotion. **A promoted case cannot be reopened** — see §9 | `NegotiationCase.Reopen`, `app.guard_case_promotion()` |
| D4 | Promotion takes `{ validFrom, validTo?, scope }`, applies the agreement aggregate's own rules, rejects `*`-prefixed placeholders, and adds no second copy of agreement creation | `PromotedAgreementFactory`, `PromoteCaseHandler` |
| D5 | Header-only promotion: no clause copying; clause generation is future work | `PromoteCaseHandler` |
| D6 | Petition outcomes are explicit; no automatic change on a meeting agreement; `Open → Agreed \| Rejected \| Withdrawn` and `Agreed → Open` only | `UnionPetition.ChangeState`, `PUT …/state` |
| D7 | A closed case is read-only: child writes are refused until it is reopened | `NegotiationCase.EnsureEditable` |
| D8 | Promotion answers `200 { agreementId, caseId }` | `PromoteCaseResponse` |
| D9 | Party requirement at creation — **not implemented**, see §9 | — |

## 4. Endpoints

| Method | Path | Cap |
|---|---|---|
| `GET` `POST` | `/negotiation/cases` | `negotiation.read` / `.write` |
| `GET` | `/negotiation/cases/{id}` | `negotiation.read` |
| `POST` `DELETE` | `/negotiation/cases/{id}/parties[/{partyId}]` | `negotiation.write` |
| `POST` | `/negotiation/cases/{id}/petitions` | `negotiation.write` |
| `PATCH` `DELETE` | `/negotiation/cases/{id}/petitions/{petitionId}` | `negotiation.write` |
| `PUT` | `/negotiation/cases/{id}/petitions/{petitionId}/state` | `negotiation.write` |
| `POST` `DELETE` | `/negotiation/cases/{id}/petitions/{petitionId}/offers[/{offerId}]` | `negotiation.write` |
| `POST` `DELETE` | `/negotiation/cases/{id}/meetings[/{meetingId}]` | `negotiation.write` |
| `POST` `DELETE` | `/negotiation/cases/{id}/petitions/{petitionId}/agreements[/{agreementId}]` | `negotiation.write` |
| `POST` | `/negotiation/cases/{id}/in-progress` · `/pending` · `/agree` | `negotiation.write` |
| `POST` | `/negotiation/cases/{id}/close` · `/reopen` | `negotiation.write` |
| `POST` | `/negotiation/cases/{id}/promote` | **`negotiation.promote`** |

### 4.1 Response shapes

`GET /negotiation/cases` → `{ items: CaseSummary[], page, pageSize, total }`, ordered by `startedOn`
descending, filterable by `state`. `GET /negotiation/cases/{id}` → `CaseDetail` with `parties`,
`petitions` (each with `offers` and `agreements`) and `meetings`. Every mutation answers the child it
touched, except the transitions and promotion: the transitions answer the whole `CaseDetail`, and
promotion answers `{ agreementId, caseId }`.

`CompanyName` is deliberately absent: the negotiation module does not join a module it does not own, and
the client already has the company list.

### 4.2 Failures, and what produces them

| Status | Code | Cause |
|---|---|---|
| 400 | `validation_failed` | Missing required field, bad enum name, `*`-prefixed scope, agreement rules refused at promotion (`validTo < validFrom`, blank scope) |
| 403 | `forbidden` | No session; creating for another company; missing `negotiation.promote` on promotion (from the API declaration or the database guard, `42501`) |
| 404 | `not_found` | Unknown case or child; another company's case (a 403 would confirm it exists) |
| 409 | `invalid_state` | Illegal transition; closing without a meeting agreement; writing to a closed case; promotion from a non-`Closed` case; reopening a promoted case. Carries `state` with the current state |
| 409 | `conflict` | Already promoted; duplicate petition/offer/agreement number; illegal petition outcome; deleting a meeting that has agreements; the agreed-petition freeze |
| 409 | `concurrent_modification` | Two transitions raced on the case (`xmin`) |

## 5. Implementation

```
src/Convenciones.Negotiation/
  NegotiationCase.cs            aggregate root: parties, petitions, meetings, transitions
  UnionPetition.cs / EmployerOffer.cs / NegotiationMeeting.cs / MeetingAgreement.cs / NegotiationParty.cs
  NegotiationEnums.cs           CaseState, PetitionState, PartyRole (the database labels)
  NegotiationCommands.cs        one handler per use case + TransitionCaseHandler + PromoteCaseHandler
  NegotiationQueryHandlers.cs   ListCasesHandler, GetCaseHandler (AsNoTracking, tenant-filtered)
  INegotiationStore.cs          persistence port + IPromotedAgreementFactory
  NegotiationConfiguration.cs   six EF configurations (column names, xmin tokens)
  NegotiationResponses.cs       one mapping, so every route answers the same shape
src/Convenciones.Api/Endpoints/NegotiationEndpoints.cs
src/Convenciones.Api/Infrastructure/NegotiationStore.cs
src/Convenciones.Api/Infrastructure/PromotedAgreementFactory.cs
supabase/migrations/20260105000000_negotiation_workflow.sql
```

**One transaction for promotion.** `PromoteCaseHandler` loads the case, asks
`IPromotedAgreementFactory` for a new agreement (which calls `Agreement.Create`, so the rules are the
agreement module's) and attaches it to the shared `DbContext` **without saving**, then sets the link on
the tracked case and calls `SaveChanges` once. The agreement INSERT and the case UPDATE therefore share
a transaction: the forced-failure test proves the rollback (a trigger that rejects the case UPDATE
leaves neither the agreement nor the link).

**The database, not the handler, is the guarantee.** `app.guard_case_promotion()` was rewritten: it
fires on `INSERT` and on every `UPDATE`, and refuses (a) a case created already promoted, (b) a promotion
without `negotiation.promote` (`42501`), (c) a promotion whose **previous** state was not `Closed`
(`22023`), (d) a second promotion (`23505`), and (e) any change to a promoted row's state or link
(`22023`) — which is what makes "promoted is terminal" a database invariant rather than a handler
courtesy. The SQLSTATEs are distinct so the API maps them without parsing messages.

**Audit from/to.** `platform.audit_entry.from_state`/`to_state` existed since the first migration but the
interceptor ignored them. It now fills them from the changed `State` property, so every case and
petition transition records `actor, from → to` without the handlers remembering to. Promotion changes the
link, not the state: its audit row carries `PromotedAgreementId` in `changes` and no from/to pair — the
state is `Closed` before and after.

**Company scope.** The six negotiation tables were capability-only. The migration recreates their
policies with `platform.can_access_company(company_id)` (through the case for the children), and adds
`agreement_api_promotion_insert` so promotion can insert the agreement header it is authorised to
create — `Draft` only, since the seeded negotiator deliberately does not hold `agreement.write`.

## 6. Acceptance criteria

| AC | Evidence |
|---|---|
| 1. Only legal transitions; illegal is 409 with the current state | `An_illegal_transition_is_refused_with_409_and_the_current_state`, `NegotiationStateMachineTests`, R37 |
| 2. Promotion requires `negotiation.promote`; the guard fires independently | `Promotion_requires_the_promote_capability` (403), `The_database_refuses_promotion_without_the_capability_when_the_api_is_bypassed` (42501 via Npgsql), R36 |
| 3. Promotion creates the agreement and the link in one transaction | `Promotion_creates_the_agreement_and_the_link_together`, `A_forced_failure_after_the_agreement_is_created_leaves_neither` |
| 4. Not promotable from a non-`Closed` state — the database, not only the handler | `The_database_refuses_promotion_from_a_case_that_was_not_closed` (including close+promote in one statement), D5, D8, R37 |
| 5. Reopening clears `closed_at` and is audited | `Reopening_returns_to_in_progress_clears_the_timestamp_and_is_audited` |
| 6. A promoted case cannot be promoted twice | `A_promoted_case_is_terminal_and_cannot_be_promoted_again`, `The_database_refuses_repointing_or_clearing_a_promoted_case`, `Concurrent_promotions_produce_exactly_one_agreement`, R38, R39 |

## 7. Test matrix

| Suite | Count | What it covers |
|---|---|---|
| `Convenciones.UnitTests/NegotiationStateMachineTests` | 21 | Every legal and illegal transition, the close precondition, terminality, the agreed-petition freeze, unique numbering, the meeting/agreement link |
| `Convenciones.IntegrationTests/NegotiationTests` | 19 | Full lifecycle + audit trail, illegal transitions with `state`, close precondition, reopen, promotion (row contents, atomism, capability, terminality, payload validation), database guard through Npgsql, forced rollback, concurrent promotion, cross-company 404 and tenant SQL, closed-case writes, petition outcomes, list scoping |
| `supabase/tests/constraint-tests.sql` | D5, D8 | A non-`Closed` case cannot carry a link; a case cannot be created already promoted |
| `supabase/tests/rls-tests.sql` | R36–R40 | Capability at the database, promotion only from `Closed`, no re-pointing, terminality, company isolation |
| `Convenciones.UnitTests/MigrationFileTests` | 1 | The new migration is in the deployment order |

## 8. Risks

- **Q-5 does not define the petition transition graph.** `Open → {Agreed, Rejected, Withdrawn}` and
  `Agreed → Open` is the minimum the evidence supports; `Rejected`/`Withdrawn` are terminal. If the
  product wants `Rejected → Open`, it is one line in `UnionPetition.ChangeState` plus a test.
- **Legacy is two cases.** The workflow is designed from code reading (as the original risk said), so
  the state semantics are a reading of Q-4, now recorded in ADR-025 rather than inferred per handler.
- **No `If-Match` on the negotiation routes.** The contract does not ask for it; transitions are guarded
  by state and the database, and child edits are last-write-wins. Adding it means exposing `xmin` on the
  detail and threading it through every child route.
- **Promotion does not copy clauses.** Deliberate (D5), but it means the agreement starts empty; the
  workspace must make that obvious.

## 9. Implementation record

### Verified by execution

| Check | Result |
|---|---|
| `dotnet build Convenciones.slnx` | 0 warnings, 0 errors |
| `dotnet test Convenciones.slnx` | see §9.1 for the counts |
| `web` Vitest suite | 44 passed (unchanged; no web change in T11) |
| `supabase/tests/run-validation.ps1` | constraint **PASS=42 FAIL=0**, RLS **PASS=42 FAIL=0**, reproducible build, `VALIDATION PASSED` |

### 9.1 The counts

The full solution run after the implementation: **49 unit** (28 + 21 new), **132 integration**
(113 + 19 new), **44 web**. The database validation ran against a disposable PostgreSQL 17 container and
applied 17 migrations from scratch, twice, with identical schemas.

### Review point: where the implementation departs from the authorised instructions

**1. Reopening a promoted case (D3).** The instruction said to reopen a closed case to `InProgress`,
"preserve `promoted_agreement_id` and prevent re-promotion", and to stop and report if that conflicts
with an invariant. It does: `negotiation_case_promoted_consistency` requires
`promoted_agreement_id is null or state = 'Closed'`, so a promoted case cannot sit in `InProgress` with
its link preserved, and A-21/ADR-014 declare promotion final. Silently relaxing the CHECK was the one
thing the instruction forbade, so the invariant stands: **`reopen` on a promoted case is 409
`invalid_state`**, the link is never cleared and the database refuses the statement too (R39). If the
intent was to allow it, the change is an ADR amendment plus a migration that relaxes the CHECK and
decides what happens to the promoted agreement — not a handler edit.

**2. The party requirement (D9).** The design recommended enforcing INV-N1 at creation; the authorisation
did not include that decision, and D1 set the principle "do not invent preconditions". Since Q-4 removed
`Draft`, no state precedes `Open`, so enforcing INV-N1 at creation would be a new business rule. It is
**not enforced**: a case can be created and driven to `Closed` and promoted without parties. The code
and the contract say so explicitly. The decision needed is: add a pre-open state, require parties on
creation, or drop INV-N1.

**3. D7's tail was cut off** in the instruction ("Reject modifications to child resources …"). It was
implemented as the heading states it: every child write on a `Closed` case is refused with 409
`invalid_state`, and reopening is the only way to edit. If the intent was narrower (for example,
allowing deletions of meetings but not agreements), it is a change in `EnsureEditable`'s call sites.

### Deviations that were part of the design, restated

- **`/open` is gone** (D2). A client that calls it gets 404 — there is no state before `Open`.
- **Petition outcome updates get their own route** (`PUT …/state`) rather than a field on `PATCH`: the
  `PATCH` must be refused when the petition is `Agreed`, which is exactly the state from which the
  outcome has to be able to return to `Open`.
- **Promotion validates `*`-prefixed scopes.** The legacy placeholders were `*Inicio`, `*Fin`,
  `*Duración`, `*Ambito`; the dates are typed and cannot carry them, so the check is on `scope`, and it
  is promotion-specific rather than a change to the agreement model.
- **The agreement insert policy was extended** (`agreement_api_promotion_insert`). Without it promotion
  failed with `42501`: the seeded negotiator holds `negotiation.promote` but not `agreement.write`,
  deliberately. The policy admits the promotion path only, and only for a `Draft` header.

## 10. Left undone

- **INV-N1 / parties before opening** — awaiting a decision (§9).
- **Clause generation from meeting agreements** — future task (D5).
- **`If-Match` on the negotiation routes** — not required by the contract; see the risks.
- **Negotiation history and print (R2-03)** — out of scope.
- **No web screen** for negotiations yet; T14 consumes this API.
