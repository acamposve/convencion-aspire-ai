# Current processing run policy

The current run for a document is the run that satisfies all of the following:

1. `status = Completed`;
2. `finished_at is not null`;
3. ordered by `finished_at DESC`, then `created_at DESC`, then `id DESC`;
4. only the first row is current.

The `created_at` and `id` tie-breakers make the result deterministic when two runs share the
same completion timestamp.

Consequences:

- `ListClausesAsync` filters clauses by the current run id and never mixes clauses from older runs.
- `GetClauseAsync` returns 404 for a clause outside the current run.
- `ReviewAsync` rejects a clause outside the current run with `409 clause_not_current`; no audit row
  is appended and no review state is changed.
- The classification version includes the clause's `run_id`, so a new current run changes
  `ExpectedVersion` even when the JSON of the human correction is unchanged.
- Runs that are `Queued`, `Running`, `Failed` or `Cancelled` never displace the last completed run.
- If the document has no completed run with a completion timestamp, the clause list is an empty page,
  clause detail is 404 and review is rejected with `clause_not_current`.
- Historical runs and their clauses remain stored; they are simply not treated as current.
