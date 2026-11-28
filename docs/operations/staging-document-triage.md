# Staging document triage — keep or purge (#140)

**Status:** rule ready, inventory pending. **No database was read or modified to write this page**: the
rules apply as soon as the inventory in §2 arrives.

This page turns the acceptance criterion of
[#140](https://github.com/acamposve/convencion-aspire-ai/issues/140) — "no client contract and no personal
data in staging" — into a decision that does not depend on who reads the table. The applicable policy is
[runbook §8.1](runbook.md#81-what-may-be-uploaded-to-staging).

## 1. Decision rule

Evaluated **in order**. The first row that applies decides; nothing below it is considered.

| # | Condition on the `platform.document` row | Class | Action |
|---|---|---|---|
| 1 | Client material, personal data, signed annex or anything confidential | **Prohibited** | **Purge** and record in #140 what was removed (never the content itself) |
| 2 | `company_id` of the release-gate tenant (`Smoke release gate`) or `uploaded_by` of a `smoke-*@staging.invalid` account | Gate leftover | **Purge**: teardown should have removed it; a survivor means the gate failed |
| 3 | `checksum_sha256` = `4b3d6e1943dbd1087a34632e89402f379413ed15771281c9595ccf8ff4ece88a` (repository fixture, `tests/fixtures/synthetic-contract.pdf`) | Synthetic fixture | **Keep** |
| 4 | Official public legislation (gazette, law, regulation, resolution) identifiable by edition or official URL | Public, allowed | **Keep only if it supports the client demo**; otherwise **purge** |
| 5 | Matches none of the above, or the checksum and provenance are unknown | Unknown | **Purge**. When in doubt, keep nothing |

Golden rule: keeping a document requires positive proof that it is synthetic or officially public. The
absence of a client signal is **not** proof that it is synthetic.

The `Reglamento Adultos Mayores Gaceta 7090 2026.pdf` file falls under row 4: it is a public gazette, not a
contract, so it is **not an incident**, but its permanence depends on whether the client will use it in the
session. If not, it is purged like any other document that adds nothing.

## 2. Inventory: the query to run

Run it with a role that can read `auth.users` (service role / `postgres`); if that is unavailable, drop that
column and the `left join`:

```sql
select d.id,
       d.file_name,
       d.size_bytes,
       d.uploaded_at,
       d.deleted_at,
       d.extraction_status,
       d.checksum_sha256,
       c.name  as company,
       u.email as uploaded_by_email
from platform.document d
left join app.company  c on c.id = d.company_id
left join auth.users   u on u.id = d.uploaded_by
order by d.uploaded_at;
```

Every returned row is classified with the table in §1. The deciding columns are not the file name — a file
can be named like a fixture and not be one — but `checksum_sha256` and `company`.

## 3. What "purge" means (two levels, not equivalent)

**Level 1 — recommended: `DELETE /documents/{id}`**

- Requires the `If-Match` header with the document's current `rowVersion`.
- Performs a *soft delete*: the row **stays** in `platform.document` with `deleted_at` set, the document
  disappears from every read (list, download, text), its active runs are cancelled, and the intent to delete
  the object is written to `platform.document_object_deletion`.
- The Storage object is removed by `DocumentDeletionSweeper`, which runs **in the API**, not the worker:
  deletion does not depend on the worker being enabled (by design, `Documents:DeletionIntervalSeconds`
  defaults to 60 s; `<= 0` disables it).

**Level 2 — release-gate tenant only: physical SQL delete**

- This is what `scripts/staging/smoke.sh teardown` does. It is not used on a real tenant's data:
  `platform.audit_entry` is append-only and its foreign keys are `ON DELETE SET NULL`, an `UPDATE` the
  trigger rejects, so a user with audit rows cannot be deleted.

Practical consequence for the acceptance criterion: **purging at level 1 satisfies "no client contract in
staging"** even though the count of 26 does not drop, because those rows become invisible. If the count
itself must drop, filter by `deleted_at is null` or use level 2.

## 4. Verification after purging

```sql
-- What the application still shows (what the client sees)
select count(*) filter (where deleted_at is null) as visible,
       count(*)                                as total_rows
from platform.document;

-- Objects whose Storage deletion is not confirmed yet
select state, count(*)
from platform.document_object_deletion
group by state;
```

A `Pending` count that does not fall within minutes means the sweeper is not completing: check
`Documents:DeletionIntervalSeconds` and the API log before calling the purge done. A purged document is
**not** purged while its object is still in the bucket.

## 5. Who cleans what, and when

| Who | What | When |
|---|---|---|
| The release smoke gate | Its own synthetic tenant, on every run | Every `mode=flows` dispatch |
| The operator | Any document matching rows 1, 2 or 5 | On detection, without waiting for #140 |
| The Product Owner | Decides on rows 4 (public, allowed) | Before the client session |

## 6. What this rule does not decide

- Whether public legislation may be uploaded in general or only during the trial session: that widens or
  narrows runbook §8.1 and is signed by the Product Owner.
- Automatic purge when a client leaves staging (still no dedicated trigger,
  [ai-security-acceptance §7](ai-security-acceptance.md)).
