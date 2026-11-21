# A-02 — Architecture boundaries enforced in the build

**Status:** implemented and tested.

*Tracking: recommendation 2 of the 2026-11-21 project assessment. The `P1-/P2-` numbers belong to the
production-v1 audit; `A-` marks an assessment recommendation.*

## Defect

The constitution (Art. IV.2) mandates dependency direction, and
[solution-architecture.md](../architecture/solution-architecture.md) §3.4 states the module dependency
rules, §4 states the per-layer guarantee and §10 says an architecture test enforces both. It also
lists `Convenciones.ArchitectureTests` in the layout. No such project existed.

`Directory.Build.props` said so explicitly: "there are NO Domain projects and NO architecture tests,
so nothing enforces it yet". The documentation claimed a guarantee the build did not have. A new
`ProjectReference` could join any project to any other with only review to stop it, and nothing
noticed when a listed project disappeared.

## Change

**New project `tests/Convenciones.ArchitectureTests`** (xUnit + Shouldly, no references to production
projects). It reads the real `ProjectReference` items of every `src/**/*.csproj` and checks them
against an explicit permitted graph, so the test cannot drift from the build and a failure names the
project and the edge.

| Test | Rule |
|---|---|
| `Every_source_project_is_classified` | a new `src` project must be added to the graph before it can merge |
| `Project_references_match_the_permitted_graph` | every reference must be listed and every listed reference must exist |
| `Modules_do_not_reference_composition_projects` | no module → API/Worker/Backup/AppHost |
| `The_kernel_does_not_reference_modules` | Catalog/Identity never point back at a module |
| `Modules_reference_the_kernel_or_a_documented_peer_only` | module → kernel, plus Comparison/Publishing → Agreements only |
| `The_worker_references_only_the_modules_it_executes` | Worker → Ai, Clauses, Documents |
| `The_api_references_modules_the_kernel_and_service_defaults_only` | the API composes, it does not reach into hosts or tools |
| `Hosts_do_not_reference_other_hosts` | Api/Worker/Backup never reference each other or the AppHost |
| `Project_references_are_acyclic` | no reference cycle, with the cycle printed on failure |
| `Every_declared_namespace_stays_inside_its_own_project` | a file cannot define another project's namespace |

The project is added to `Convenciones.slnx`, so the existing CI step `dotnet test Convenciones.slnx`
runs it — no new workflow step and no separate gate to forget.

**Documentation corrected to match.** `Directory.Build.props` now states what the tests enforce;
§3.4 distinguishes the enforced project graph from the read-only review rules; §4 no longer claims a
per-layer guarantee that has no `.Domain` project to enforce it on; the CQRS paragraph and §10 table
name the actual test and what it checks; §4.1 marks the layout tree as intended rather than current.

## Not enforced, deliberately

- **Domain layer purity** — there is no `.Domain` project (A-06, one project per module). The rule
  becomes testable when a module is split.
- **Comparison/Publishing read-only access** — a reference cannot reveal whether it writes; review of
  the query interfaces remains the check.
- **CQRS handler shape** — a test cannot reliably tell a command handler from a query handler.
- **Table-name prefixes** — the schema rule stays a review rule.

## Mutation checks

Both mutations were applied, the architecture tests run, and the changes reverted.

| Mutation | Expected | Observed |
|---|---|---|
| Add `Agreements → Negotiation` to `Convenciones.Agreements.csproj` | the graph and the module rule fail | **2 failed** / 10, message `Convenciones.Agreements references Convenciones.Negotiation, outside its permitted graph` |
| Add `src/Convenciones.Api/NegotiationLeak.cs` declaring `namespace Convenciones.Negotiation` | the namespace rule fails | **1 failed** / 10, message names the file and `outside project 'Convenciones.Api'` |

## Evidence

| Gate | Result |
|---|---|
| `dotnet build Convenciones.slnx -c Release -warnaserror` | 0 warnings, 0 errors |
| Architecture tests | **10 passed** |
| `dotnet test Convenciones.slnx -c Release --no-build` | 248 unit + 365 integration + 10 architecture |
| CI run [`44929548779`](https://github.com/acamposve/convencion-aspire-ai/actions/runs/44929548779) (commit `b652ba6`) | all five jobs pass; the .NET job builds and runs the new project |

## Notes

- The tests read files, not assemblies: the boundary that matters is the MSBuild reference, and an
  unused project reference is invisible in IL. They cost about 13 s on a cold test host and no
  database or container.
- They need a repository checkout; they locate the root by walking up to `Convenciones.slnx` and fail
  with an explicit message if it is missing. That is the same artifact CI builds, so it is the
  intended environment.
- The permitted graph is intentionally exact rather than a subset: adding a reference or removing one
  both fail, so a dependency change is always a reviewed edit to this file.
