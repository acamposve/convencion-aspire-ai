# T17 — Aspire orchestration investigation

**Status:** planned · **Depends on:** nothing · **Blocks:** nothing (a documented fallback exists)

## Objective

Resolve or formally retire the failure where Aspire starts the containers but never starts the API and
migrator. Reproduced **six times**: DCP logs `Starting -> Waiting`, then its watch stream dies with
`http2: response body closed`.

## Scope

**In:** reproduce with full DCP and admin logs at the failing moment; inspect the DCP version shipped
with the Aspire CLI bundle; test the container-vs-project distinction (project resources only); try a
minimal AppHost with a single project resource to isolate whether it is the model or the environment;
capture the exact error for a written report. If a reliable fix is not found, adopt the direct-run path
as the **official** procedure.

**Out:** changing unrelated project files to chase the symptom; swapping orchestration frameworks.

## Deliverables

| File | Purpose |
|---|---|
| `docs/operations/aspire-investigation.md` | Evidence, what was tried, conclusion |
| `docs/operations/local-development.md` | Updated run procedure (one command, or documented two-command fallback) |

## Acceptance criteria

1. The root cause is identified, or the investigation is documented as inconclusive **with the evidence**.
2. Either `dotnet run --project src/Convenciones.AppHost` brings up postgres + migrator + api, verified
   from a clean state — or the runbook states plainly that it does not, and why.
3. No claim of a working one-command stack that was not demonstrated on a clean machine state.
4. The investigation does not leave orphaned `dcp` processes or containers behind.

## Risks

- This is the kind of failure that invites guesswork. The rule for this task: one hypothesis, one test,
  recorded either way — and no more than one configuration change at a time.
- Time sink: it blocks nothing. If it resists, stop and document.
