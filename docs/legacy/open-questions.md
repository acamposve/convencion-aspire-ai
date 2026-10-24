# Open questions — decisions required before design

Every question here is one the legacy code cannot answer. Each states **why it blocks**, the
**evidence** that exists, the **options**, and a **recommendation** where I have one.

Answers change the schema, so the ones marked **BLOCKING** must be resolved before Task 3
(domain model + PostgreSQL schema). Others can wait for the feature slice that needs them.

Per constitution Art. XII.4, none of these will be guessed: an unanswered question stays a
question.

---

## Already resolved — no action needed

| ID | Decision | Source |
|---|---|---|
| D-1 | Reference catalogs may ship as curated, hand-authored seed (countries, states, municipalities, comparison headings) | constitution Art. V.3 |
| D-2 | Root README rewritten; legacy description archived | constitution Art. XIII |
| — | One SuperAdmin bootstrapped at first run; every other account created in-app | constitution Art. VI.6 |
| — | Negotiation workflow is in MVP scope | Product Owner, Task 1 |
| — | Legal library in scope but **starts empty**; no legacy statutes, articles or PDFs carried over | Product Owner, Task 1 |
| — | Public portal in scope | Product Owner, Task 1 |
| — | Supabase Auth + Storage + RLS | Product Owner, Task 1 |

---

## BLOCKING — needed before the schema

### Q-1 — What identifies an agreement?

**Why it blocks:** `contratos` has no title, number or code — only company, dates, scope and a PDF
([business-rules.md](business-rules.md) §BR-01). If agreements need to be selectable and
referenced by humans (and comparison shows several per company side by side), the entity needs an
identity the legacy schema cannot express. This determines whether `Agreement` gets a `title`
and/or a `number`, and whether that number is unique per company or globally.

**Evidence:** 73 rows; zero name-like columns. The UI identifies agreements as
"company — start date — end date".

**Options:** (a) no title, identify by company+period; (b) free-text title; (c) structured
number + title; (d) title auto-composed from company+period.

**Recommendation:** (c) if agreements are ever cited in documents or reports — which is the norm
for collective agreements. Should we ask what users actually cite?

---

### Q-2 — Is the article number a number, and what does the `NNN-N` suffix mean?

**Why it blocks:** determines the column type (integer vs structured string) and the ordering
strategy. The legacy stores `varchar(11)` and sorts lexicographically, producing 1, 10, 100, 11, 2
(§BR-02, §BR-03). The intended format is three-digit zero-padded, optionally suffixed
(`"012-3"`). Whether `012-3` means "clause 12, sub-clause 3" or "clause 12 bis" is unknown.

**Evidence:** the padding intent appears in four working copy-pasted handlers and in one broken
helper (`discusion.php:1141`); dump values include `'004'`, `'01'`, `'01-A'`, and one obviously
junk `'5 555'`.

**Options:** (a) integer; (b) integer + suffix; (c) free text with numeric-aware sort; (d) ordered
position column independent of the printed number.

**Recommendation:** (d) plus (b) — a separate sort key, because legal documents get renumbered and
inserted out of sequence. Requires confirmation that renumbering happens.

---

### Q-3 — What is `campo_comparativo`, and what does "requires economic comparison" mean?

**Why it blocks:** this is the **only structured comparison dimension** in the system, and it is
inert. `categorias_titulos.requiere_campo_comparacion_economica char(2)` marks certain heading
categories as needing an economic value; the clause column `campo_comparativo varchar(250)` is
where it would go. No code ever reads `campo_comparativo` for comparison, and nothing enforces the
flag (§BR-05). Modelling it as free text versus a typed amount/percentage changes both the schema
and the comparison UI.

**Evidence:** the column and flag both exist; the flag is displayed in the heading-category list;
`campo_comparativo` is written by the clause form and read nowhere.

**Options:** (a) free text; (b) a single currency amount; (c) amount + unit (%, days, Bs.); (d)
structured per-category schema.

**Recommendation:** cannot recommend — the domain semantics are unknown. This may be the most
valuable feature in the product (comparing *value* of clauses, not just their text), so I would
rather ask than model it wrong.

---

### Q-4 — What do the three negotiation states mean?

**Why it blocks:** the negotiation lifecycle is the core workflow and its states are ambiguous.
`estatus_bitacora` is `char(3)` with `''` (labelled "Pendiente"), `1` (labelled "Modificación")
and `10` ("Cerrado"). Only `1` and `10` are ever written, and `1` is written **only by reopen**
(§BR-09). So `1` may mean "open" or "reopened" — different state machines.

**Evidence:** one writer statement (`discusion.php:399`), two callers
(`cerrar_discusion.php:46`, `reabrir_discusion.php:7`), three UI labels, and a dump containing only
`'1'` and `''` — **no case ever reached `10` in the sample data**.

**Options:** (a) two states — pending → closed, reopen returns to pending; (b) three states —
pending → in-progress → closed; (c) richer: draft → negotiating → agreed → closed, with reopen as
a transition from closed.

**Recommendation:** model as an explicit state machine with named transitions regardless of choice.
Need to know whether "agreed" is distinct from "closed".

---

### Q-5 — Does petition status need to distinguish "agreed" from "withdrawn" or "rejected"?

**Why it blocks:** `estatus_peticion char(1)` only ever holds `''` or `9`, and `9` means "an
agreement was produced for this petition" (§BR-12). In a negotiation, petitions are also rejected,
withdrawn or deferred — none of which is representable. If the domain needs those outcomes, the
model needs a real enum plus a reason.

**Evidence:** `9` written at `discusion.php:1133`; reset at `:1185`; the UI only uses it to show a
"reopen" link.

**Recommendation:** an enum with at least `Open`, `Agreed`, `Rejected`, `Withdrawn` — but confirm
which outcomes actually occur.

---

### Q-6 — Does "comparison" mean adjacency or true alignment? **(most consequential)**

**Why it blocks:** it decides whether the MVP is a viewer or an analysis engine. The legacy
renders each selected agreement's clauses for a heading side by side, ordered by article number,
with **no alignment, no pairing, no diff, no gap detection** (§BR-15). If the Product Owner
expects "clause 5 of agreement A vs clause 5 of agreement B, and which agreement is silent on
clause 7", that is a new capability built on top of headings — a materially larger scope.

**Evidence:** `comparador.php:238-449` — three independent per-column loops, no cross-column logic
anywhere in the codebase.

**Options:** (a) adjacency only (legacy parity); (b) alignment by article number; (c) alignment by
explicit clause mapping; (d) semantic/text similarity matching.

**Recommendation:** (a) for the MVP to reach demonstrable parity fast, with (b) as the next
increment — but this must be an explicit Product Owner decision, because a user who expects (b)
will judge (a) as broken.

---

### Q-7 — Who may publish an agreement, and what does publishing mean?

**Why it blocks:** publication is a two-state flag now (§BR-06), but it gates the comparison
feature *and* the public portal. That implies a review/approval step that the legacy code does not
model. If publication is a governance act, it needs an actor, a timestamp and possibly a reason.

**Evidence:** `status_publicacion` `PE`/`PU`; comparison filters `PU`; nothing records who
published or when.

**Recommendation:** keep two states, but record `published_at` / `published_by` — cheap now,
impossible to backfill later. Confirm whether a second person must approve.

---

### Q-8 — Are per-screen permission grants the real requirement?

**Why it blocks:** the legacy grants access **per page filename** (`pantallas.url_pantalla` =
`tbl_paises_list.php`), i.e. the permission model is coupled to the UI's file layout (§BR-18).
Reproducing that in a React SPA is meaningless — there are no filenames. The model must become
capability-based, and that redefines what a role *is*.

**Evidence:** 146 grants, 136 screens, 6 types; grant admin screens missing entirely.

**Options:** (a) capability-based (`agreement.publish`, `catalog.manage`); (b) entity-based
(read/write per aggregate); (c) module-based (coarse).

**Recommendation:** (a) — but I need the real user roles first, which is Q-9.

---

### Q-9 — What are the real roles?

**Why it blocks:** the six configured types mix **work functions** (Master, Operador,
Transcriptor) with **feature bundles** (Perfil 2/3, DEMO) on one axis, and their grants contradict
their names — the "Transcriptor" holds 45 grants including deletes on companies and statutes, while
the "Operador" holds 6 (§BR-19). Type `5` has grants but no record. Nothing states intent.

**Evidence:** the role table and grant counts; no documentation anywhere in the repo.

**Recommendation:** a fresh roles workshop. My reading is that the real distinctions are roughly
*administrator*, *editor/transcriber* (authors and maintains agreement text), *negotiator*
(works the petition/offer/meeting flow), and *read-only/public*. Ask whether that matches the
business.

---

### Q-10 — Which reference deletes should be restricted rather than cascade?

**Why it blocks:** legacy deletes cascade by hand — deleting a country removes its states and
municipalities in three untransacted statements (§BR-22) — but business records are not protected
at all: a company can be deleted while agreements still point at it.

**Evidence:** `eliminarpais.php:10-12`; `eliminarempresa.php` checks nothing.

**Recommendation:** `RESTRICT` for anything referenced by business data (company with agreements,
heading with clauses); `CASCADE` only for pure taxonomy (country → state → municipality). Confirm
whether deleting a country with no agreements should really wipe its municipalities.

---

## Non-blocking — needed for a specific feature slice

### Q-11 — Must a negotiation case have both PDFs to be created?
§BR-08/F3.1: `agregar_discusion.php:46` refuses to save unless **both** the petitions PDF and the
offers PDF are attached. That looks like an accident (`if($ext!==""||$ext2!=="")`), not a rule.
Recommendation: optional attachments, each with its own upload. Confirm.

### Q-12 — Must an agreement have an act PDF?
The agreement carries `pdf_auto_acta` and lists link to it, but no code requires it. Same question
as Q-11 for the `contratos` side.

### Q-13 — What validation is actually required?
The application has essentially none (§BR-30): one server-side check in the entire codebase, and
JavaScript validators wired to the wrong forms. Requirements must be gathered rather than
reverse-engineered. Which fields are genuinely mandatory — agreement dates, clause number,
clause text, company RIF, contact email?

### Q-14 — Where does the public portal live?
The shipped portal is a **static** `index.html`; the dynamic news front-end
(`tablas/noticiero/`) exists but is unreachable (no `modulos` row) (§F7). The replacement is a React
app, so: is the portal part of the same app (public routes, no auth) or a separate surface? Does it
need SEO/SSR? This affects architecture, not just a screen.

### Q-15 — What does the "No Compara" heading mean?
One of the 64 comparison headings is literally named "No Compara". It suggests an explicit opt-out
(these clauses are excluded from comparison). **No code references it.** Confirm whether an
"excluded from comparison" concept is real (§BR-24).

### Q-16 — Are comparison filters (sector / company type / state) real requirements?
They exist in the UI but the dropdowns depended on hardcoded `root` credentials that the March 2016
`error_log` shows were denied — so they returned nothing in production (§BR-17). Keep, or drop?

### Q-17 — Should deriving a contract from a negotiation copy or move the agreements?
Legacy copies (`acuerdos` rows survive), and re-closing duplicates. Copy-with-idempotency is the
safer semantic, but confirm whether a negotiation can legitimately produce a second agreement (a
renegotiation) — that decides whether promotion is once-per-case or repeatable.

### Q-18 — Is the legacy `codigo_empresa` an external business code that must be preserved?
`empresas` has both `id` (PK, 116–195) and `codigo_empresa` (2–115, zero overlap) and the code keys
on the latter while the insert never writes it (§BR-23). If `codigo_empresa` is a value the
business cites (in reports, correspondence, legacy cross-references), it must be carried as a
first-class business identifier even though we are not migrating data. If it was only ever an
accident of two sequences, it should disappear. **This one I cannot infer at all.**

### Q-19 — Why are some clauses flagged `articulo_cerrado`, and what does exclusion mean?
The flag is set in the clause editor and read in exactly one place: the compiled print view skips
clauses where it equals `1` (§BR-07C). So some clauses are deliberately omitted from the published
agreement document. **The reason is not recorded anywhere.** Possibilities: superseded clauses kept
for traceability; reserved/transitory provisions not part of the published text; clauses pending
legal review. This must be answered because it determines whether the replacement needs a clause
lifecycle (active / superseded / excluded) rather than a boolean on a print screen.

---

## Confirmed unresolvable from the repository

Stated so no one assumes further digging will settle them:

1. **Whether the legacy system worked in production.** It cannot be executed on a supported
   runtime (§S15), and the only runtime evidence is two `error_log` files showing *failures*.
   Every "confirmed" verdict in these documents means "confirmed in code", never "observed working".
2. **Whether a negotiation ever reached closed** — the dump has 2 bitácoras with states `'1'` and
   `''`; state `10` never occurs in data.
3. **How `empresas.codigo_empresa` values were ever populated** — no trigger, no procedure, and the
   only INSERT omits the column.
4. **Whether the permission grants reflect policy.** No document, comment or commit history states
   intent.
5. **What `usuarios_login` was** — referenced by both audit writers, absent from the schema.
6. **Whether `categorias_titulos` (heading blocks) is administrable** — agents disagreed on whether
   `settings.php` routes to it; the object is included but I found no case selecting it.
7. **Production MySQL `sql_mode`** — decides whether the negotiation-to-agreement handoff silently
   corrupted data (strict → insert fails → clauses go to the wrong contract; non-strict → zero
   dates).
8. **The two database names** — code says `cccol` and `CCCOL` and `presenci_cccol` in different
   places; only one can have been live.

---

## Suggested resolution order

1. **Q-6** (comparison semantics) — decides MVP scope and size.
2. **Q-1, Q-2, Q-3** — decide the four core entities: agreement, clause, heading, comparison value.
3. **Q-4, Q-5, Q-17** — decide the negotiation state machine and promotion semantics.
4. **Q-9, Q-8, Q-7** — decide identity, roles and publication governance.
5. **Q-10, Q-18** — decide referential and identifier strategy.
6. Everything else during its slice.

Answering 1–4 is enough to start Task 3. The rest can trail.
