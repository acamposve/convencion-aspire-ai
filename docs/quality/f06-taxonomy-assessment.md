# F-06 — Taxonomy assessment: working time

**Status:** recommendation only. The production taxonomy is **unchanged**; a taxonomy change needs a
separate decision and its own migration.

## 1. The gap

D6 processed a four-clause synthetic labour agreement. Three clauses classified; the working-hours
clause did not:

```
CLAUSULA SEGUNDA. JORNADA. La jornada semanal sera de cuarenta horas, distribuida de lunes a viernes.
→ categories: []
```

This was not a model failure. The taxonomy has **14 keys** (`20260109000100_ai_clause_taxonomy.sql`)
and none of them represents working time:

`parties` · `scope` · `pricing` · `duration` · `termination` · `confidentiality` ·
`data_protection` · `ip` · `liability` · `warranties` · `disputes` · `governing_law` ·
`force_majeure` · `other`

For a product built around **collective labour agreements**, working time is not an edge case — it is
one of the most frequently negotiated subjects (hours, shifts, breaks, overtime, rest days, on-call).

## 2. Options considered

| Option | Assessment |
|---|---|
| **A. Map working time to `scope`** | `scope` is "objeto, alcance y obligaciones de las partes". Working hours are an obligation of the employer, so it is *defensible* — but it dilutes a category that already absorbs object, scope and general obligations. Every labour agreement would put hours there, making `scope` the dominant bucket and destroying its discriminative value. |
| **B. Map to `pricing`** | Wrong. Overtime *pay* is pricing; the hours themselves are not. |
| **C. Leave unclassified** | Loses the single most negotiated topic in the domain, and the metrics will keep showing a systematic unclassified rate. |
| **D. Add `working_conditions`** | **Recommended.** |

## 3. Recommendation

Add one category:

```sql
insert into platform.clause_category (key, name, description, sort_order)
values ('working_conditions',
        'Jornada y condiciones de trabajo',
        'Horas de trabajo, turnos, descansos, pausas, trabajo extraordinario y modalidades de prestación',
        35);
```

Inserted between `pricing` (30) and `duration` (40) so it appears where a labour reviewer expects it.

### Inclusion rules

- length of the working day / week / month;
- shift systems, rotations, on-call and availability windows;
- breaks, rest periods, weekly rest, holidays **as time**;
- remote-work and hybrid arrangements (where they are performed);
- overtime **hours** and their authorisation (not their rate — see exclusions);
- attendance, punctuality and time recording.

### Exclusion rules

- **pay** for those hours, overtime premiums, bonuses → `pricing`;
- **vacation entitlement in days** → currently `other`; if the product later adds a benefits
  category, vacations should move there, not here (vacation is leave, not working time);
- occupational **health and safety** obligations → no category today; candidate for a future
  `health_and_safety` key, not this one;
- the parties' general obligations when hours are not mentioned → `scope`.

### Overlap analysis

| Existing category | Overlap | Resolution |
|---|---|---|
| `pricing` | overtime hours vs overtime pay | hours → `working_conditions`, pay → `pricing`; a clause doing both is **legitimately multi-label** |
| `scope` | working conditions are obligations | if the clause is *about* hours/shifts, prefer `working_conditions`; `scope` keeps object, definitions of the relationship and general obligations |
| `duration` | neither: duration is the term of the agreement | no conflict |
| `other` | vacations currently fall here | unchanged by this proposal |

### Representative examples

| Clause (abridged) | Expected |
|---|---|
| "La jornada semanal será de cuarenta horas, distribuida de lunes a viernes." | `working_conditions` |
| "El personal disfrutará de un descanso de treinta minutos por jornada." | `working_conditions` |
| "Las horas extraordinarias se abonarán con un recargo del 50 %." | `working_conditions` + `pricing` (primary `pricing`) |
| "La empresa podrá establecer turnos rotativos previa consulta al sindicato." | `working_conditions` |

## 4. Backward-compatibility and migration implications

| Area | Impact |
|---|---|
| **Migration** | one idempotent `insert … on conflict (key) do nothing`; no table change, no data rewrite. Must be a **new** migration, never an edit of `20260109000100`. |
| **Taxonomy version** | becomes a new version string; the evaluation corpus must record it and be re-annotated (new `datasetVersion`). |
| **Historical classifications** | untouched. Existing runs keep their old categories; the new key applies only to future runs. No backfill is required, and none should be attempted without a decision. |
| **Prompt** | the classification prompt already enumerates the taxonomy at runtime in the user message, so **no prompt change is needed** — the new key flows through automatically. This is a genuine benefit of the current design. |
| **UI** | categories are data-driven; no code change expected, but the admin/compare screens should be checked for hard-coded lists. |
| **Metrics** | macro-F1 changes meaning when the category set changes; comparisons across taxonomy versions are **not** valid and must be reported with the version. |
| **Corpus** | the D6 `JORNADA` annotation (currently empty) becomes a positive `working_conditions` example in the next corpus version — a concrete demonstration that the taxonomy, not the model, was the limiter. |

## 5. Decision required

Not implemented. Choosing between option A and option D is a product decision about how the catalogue
should behave for labour agreements, and it changes what every future classification means.
