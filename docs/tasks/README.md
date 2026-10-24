# Task records

One Markdown file per task, so the state of the work is visible without reading a pull request
thread or asking.

## Convention

| Rule | Detail |
|---|---|
| File name | `task-NN-<slug>.md`, where `NN` is the task number from the implementation plan (T1, T2, …) |
| Created | **Before** implementation starts, with objective, scope, planned files and risks |
| Updated | As the task proceeds — every check that was actually run is recorded with its real result |
| Committed | In the same pull request as the task, so the record and the code arrive together |
| Never | A result is written before it was executed. "Expected to pass" is not a result |

## Required sections

1. **Objective** — what this task must achieve.
2. **Scope** — what is in, and what is deliberately out.
3. **Files touched** — every file added or modified, and why.
4. **Risks** — what could go wrong, before starting.
5. **Checks executed** — commands run and their actual output, including failures.
6. **Status** — done / partial / blocked, with the evidence.
7. **Left undone** — explicitly, so nothing is assumed complete.

## Why

A pull request shows the final diff; it does not show what was attempted, what failed first, or
which claims are verified. These files do. They are the difference between "tests pass" and
"these tests ran against this tree and here is the output".

## Index

| Task | File | PR | Status |
|---|---|---|---|
| T0 | baseline revalidation | — | done (in audit) |
| T1 | [task-01-document-isolation.md](task-01-document-isolation.md) | #9 | verified, awaiting merge |
| T2 | authentication (next) | — | not started |
