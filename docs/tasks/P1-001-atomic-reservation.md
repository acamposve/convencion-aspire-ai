# P1-001 — Atomic quota reservation and run creation

**Status:** implemented · **Finding:** F-01 (production-v1 audit) · **Scope:** API only, no schema change

## Problem

`POST /documents/{id}/process` reserved quota and inserted the run through two different
transactions. `ProcessingQuotaService.TryReserveAsync` opened its own `NpgsqlConnection`, took the
company advisory lock, incremented `processing_usage.documents`, and **committed**. The endpoint then
inserted the `processing_run` and committed separately.

Two consequences:

- **Orphan charges** — any failure between the two commits consumed a document from the monthly quota
  with no run to account for it.
- **Active-limit race** — the "how many runs are active" count was read in the reservation's
  transaction and released before the insert, so two concurrent starts could both observe zero active
  runs, both be allowed, and both insert.

## Change

| File | Change |
|---|---|
| `ProcessingQuotaService.cs` | `TryReserveAsync` now takes the caller's `NpgsqlConnection` and `NpgsqlTransaction`, and neither opens nor commits anything. The company advisory lock is transaction-scoped, so it serialises starts until the caller commits. Denials build their `QuotaStatus` from the rows already read instead of calling `GetAsync` on a second connection. |
| `ProcessingEndpoints.cs` | Passes the EF connection and the ambient transaction into the reservation; on a denied reservation it rolls the transaction back. Everything — lock, counter increment, run insert — commits once. |

Idempotency is unchanged: an existing queued/running run is returned before any reservation, and the
`23505` path still rolls back and returns the winner.

## Tests

| Test | What it proves |
|---|---|
| `Reservation_rolls_back_with_the_callers_transaction` | A rollback after a successful reservation leaves `documents` at 0 — no orphan charge |
| `Concurrent_starts_cannot_exceed_the_active_run_cap` | Two documents, cap 1, barrier-synchronised transactions: exactly one allowed, one run row, one charge |
| `Concurrent_reservations_do_not_exceed_the_document_limit` | Barrier-synchronised: one slot, one survivor |
| `Active_run_cap_rejects_a_second_run_for_the_company` | Pre-existing run blocks a new one with `concurrency_limit` |
| `Starting_a_run_charges_once_and_a_repeat_returns_the_same_run` | HTTP path: 202, one run, one charge; repeat returns 202 without charging again |
| `A_second_document_is_refused_by_the_active_run_cap_without_being_charged` | HTTP path: 409 `concurrency_limit`, no run, no charge |

The concurrency tests use a `Barrier` so both attempts are inside their transaction before either
reserves; without it they could pass by running sequentially and prove nothing.

## Results

- `dotnet build Convenciones.slnx -c Release -warnaserror` — 0 warnings, 0 errors
- Unit — **239 passed**
- Integration — **253 passed**

## Also fixed here

`PromptCatalog.Hash` now normalises CRLF to LF before hashing. The pinned prompt digests were computed
on an LF checkout; with CRLF on Windows the pins failed for a reason unrelated to the prompt, which is
exactly the false signal the pin exists to avoid. A test covers it.

## Left undone

- `RecordUsageAsync` still writes pages/tokens after the stage on its own connection — that is P1-005,
  a different finding.
- The advisory lock is now held from the reservation until the endpoint commits, extending its hold
  across the run insert. Lock ordering (document lock, then company lock) is unchanged; the integration
  tests exercise the concurrent case.
