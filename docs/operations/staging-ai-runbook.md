# Staging AI exercise — runbook (#47 / #52)

**Status:** prepared, not executed. Staging deploy, worker enablement and all secrets changes need the
owner's explicit approval.

**Scope:** synthetic or authorized test documents only. No real collective agreement may be uploaded
until the provider terms are reviewed and approved.

## 0. What the operator must provide

| Value | Where to get it | Where it goes |
|---|---|---|
| Foundry endpoint | Azure Portal → resource **ai-convenciones-hxetrj** → *Keys and Endpoint* → Endpoint (expected `https://ai-convenciones-hxetrj.cognitiveservices.azure.com/`) | GitHub **variable** `AZURE_AI_ENDPOINT` (environment `staging`) |
| Foundry API key | same page → *KEY 1* | GitHub **secret** `AZURE_AI_API_KEY` (environment `staging`) |
| Deployment name | Foundry portal → **ai-convenciones-hxetrj** → *Models + endpoints* → deployment `clauses-mistral` | GitHub **variable** `AZURE_AI_DEPLOYMENT` |
| Document Intelligence endpoint | Azure Portal → resource **di-convenciones-hxetrj** → *Keys and Endpoint* (expected `https://di-convenciones-hxetrj.cognitiveservices.azure.com/`) | GitHub **variable** `DOCUMENT_INTELLIGENCE_ENDPOINT` |
| Document Intelligence key | same page → *KEY 1* | GitHub **secret** `DOCUMENT_INTELLIGENCE_API_KEY` |
| Supabase URL | Supabase → Project Settings → API → Project URL | already `vars.SUPABASE_URL` |
| Supabase service key | Supabase → Project Settings → API → `service_role` secret | already `secrets.SUPABASE_SECRET_KEY` |
| Bucket | Supabase → Storage → bucket **documents**, **Private** | already `Documents__Bucket=documents` |

Never paste a key into a commit, issue, log, chat or `.env.example`. The workflow writes the real
`.env` on the droplet with `umask 077`; the file must stay `chmod 600`.

## 1. Configure GitHub `staging`

Do this from a trusted machine, in the repository, with `gh` authenticated. The commands read the
value interactively so it does not end up in shell history or terminal output.

```bash
gh variable set AZURE_AI_ENDPOINT --env staging --repo acamposve/convencion-aspire-ai
# paste https://ai-convenciones-hxetrj.cognitiveservices.azure.com/

gh variable set AZURE_AI_DEPLOYMENT --env staging --repo acamposve/convencion-aspire-ai
# paste clauses-mistral

gh secret set AZURE_AI_API_KEY --env staging --repo acamposve/convencion-aspire-ai
# paste KEY 1; the prompt does not echo it

gh variable set DOCUMENT_INTELLIGENCE_ENDPOINT --env staging --repo acamposve/convencion-aspire-ai
# paste https://di-convenciones-hxetrj.cognitiveservices.azure.com/

gh secret set DOCUMENT_INTELLIGENCE_API_KEY --env staging --repo acamposve/convencion-aspire-ai
# paste the DI key
```

Verify names only (values are intentionally not printed):

```bash
gh variable list --env staging --repo acamposve/convencion-aspire-ai
gh secret list  --env staging --repo acamposve/convencion-aspire-ai
```

The deploy workflow writes `Worker__Enabled=false` on every deploy. Enabling the worker is a
**manual, temporary** edit on the droplet (§5).

## 2. Verify the Supabase bucket

1. Supabase Dashboard → **Storage**.
2. If `documents` does not exist: **New bucket** → name `documents` → **Public bucket = OFF** → Create.
3. If it exists: open it and confirm **Public = OFF**.
4. Do not add a public read policy. The API/worker use the service key server-side; the frontend only
   receives the anon key from `VITE_SUPABASE_ANON_KEY`, which is already separate.

## 3. Deploy the configuration PR (after approval)

1. Merge the configuration PR; CI must be green and the deploy workflow runs.
2. Confirm the workflow emitted no “Azure AI settings are incomplete” warning; if it did, a variable or
   secret name is missing — fix it before proceeding.
3. Confirm the API is healthy:

```bash
curl -fsS http://138.197.15.40/health/ready
```

4. Confirm the worker is **disabled**:

```bash
ssh <SSH_USER>@138.197.15.40
cd /opt/convenciones
docker compose logs --tail=50 worker | grep -E "disabled|stage_not_configured|Worker"
```

Expected: `Worker is disabled (Worker:Enabled=false); the container stays healthy.`
Do not print the contents of `.env`.

## 4. Create the two staging tenants

### 4.1 Create the Auth users

Supabase Dashboard → **Authentication** → **Users** → **Add user** (twice), with fictional addresses,
for example:

- `tenant-a@staging.invalid` (password generated and stored in the password manager)
- `tenant-b@staging.invalid`

Enable **Auto Confirm User** so the password grant works. Copy each **User UID**.

### 4.2 Run the tenant SQL

Open [`staging-ai-tenants.sql`](staging-ai-tenants.sql), replace:

- `USER_A_UUID` with the UID of `tenant-a@staging.invalid`
- `USER_B_UUID` with the UID of `tenant-b@staging.invalid`

Run it in Supabase → **SQL Editor**. The script creates two companies, their profiles, the `editor`
dedicated `ai_staging_tester` role (document read/write + clause read/process/review only) and the staging quota (5 documents / 100 pages / 500,000
tokens). It is safe to re-run: inserts are idempotent.

Expected output: two rows with the company ids and names.

## 5. Enable the worker for the exercise window

```bash
ssh <SSH_USER>@138.197.15.40
cd /opt/convenciones
sed -i 's/^Worker__Enabled=false$/Worker__Enabled=true/' .env
docker compose up -d worker
docker compose logs --tail=50 worker
```

Expected: the worker starts and logs no configuration error. If it logs
`stage_not_configured`, stop here: one of the Azure variables/secrets is missing or the deploy did not
write them. Do **not** continue with real data.

To disable immediately (kill switch):

```bash
cd /opt/convenciones
sed -i 's/^Worker__Enabled=true$/Worker__Enabled=false/' .env
docker compose up -d worker
docker compose logs --tail=20 worker | grep "disabled"
```

Every deploy rewrites the file with `Worker__Enabled=false`; still verify after a deploy.

## 6. Exercise the happy path (tenant A)

The API base is `http://138.197.15.40/api`. Staging is HTTP; only synthetic/authorized documents are
allowed. TLS is required before real contracts.

```bash
export API=http://138.197.15.40/api
export SUPABASE_URL=https://<project>.supabase.co
read -rs SUPABASE_ANON_KEY
read -rs PASSWORD_A

TOKEN_A=$(curl -fsS -X POST "$SUPABASE_URL/auth/v1/token?grant_type=password" \
  -H "apikey: $SUPABASE_ANON_KEY" -H "Content-Type: application/json" \
  -d "{\"email\":\"tenant-a@staging.invalid\",\"password\":\"$PASSWORD_A\"}" | jq -r .access_token)
```

Upload a **synthetic or explicitly authorized** PDF:

```bash
curl -fsS -X POST "$API/documents" \
  -H "Authorization: Bearer $TOKEN_A" \
  -F "file=@/path/to/synthetic-contract.pdf;type=application/pdf"
```

Take the returned document id and extract the text, then start processing:

```bash
curl -fsS -X POST "$API/documents/$DOC_A/extract" -H "Authorization: Bearer $TOKEN_A"
curl -fsS -X POST "$API/documents/$DOC_A/process" -H "Authorization: Bearer $TOKEN_A"
```

Poll the run until `Completed` (or a documented error):

```bash
curl -fsS "$API/documents/$DOC_A/processing" -H "Authorization: Bearer $TOKEN_A" | jq
curl -fsS "$API/documents/$DOC_A/clauses?page=1&pageSize=25" -H "Authorization: Bearer $TOKEN_A" | jq
```

Record: run id, status, clause count, classified/unclassified counts, quota usage.

## 7. Cross-tenant denial (tenant B)

Get `TOKEN_B` the same way. Every response for A's resources must be **404**, not 403 and not 200:

```bash
for path in \
  "documents/$DOC_A" \
  "documents/$DOC_A/text" \
  "documents/$DOC_A/clauses" \
  "documents/$DOC_A/processing" \
  "processing/$RUN_A" \
  "clauses/$CLAUSE_A"; do
  code=$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN_B" "$API/$path")
  echo "$code  $path"
done
```

Expected: `404` on every line. Also confirm B's own document list does not contain A's id:

```bash
curl -fsS "$API/documents" -H "Authorization: Bearer $TOKEN_B" | jq '.items[].id'
```

Record the exact status codes and timestamps in the results document.

## 8. Quota check

Tenant A has 5 documents / 100 pages / 500,000 tokens. Start a sixth run and confirm:

```bash
curl -s -o /tmp/quota.json -w '%{http_code}\n' -X POST \
  "$API/documents/$DOC_SIXTH/process" -H "Authorization: Bearer $TOKEN_A"
cat /tmp/quota.json | jq
```

Expected: `409` with `code = "quota_exceeded"` (or `concurrency_limit` if another run is active). Never
delete quota/usage rows to make the test pass; record the response.

## 9. Kill-switch drill

1. While the worker is **disabled**, upload/process a fresh synthetic document with A.
2. Confirm the run stays `Queued` after 30–60 seconds:

```bash
curl -fsS "$API/documents/$DOC_KILL/processing" -H "Authorization: Bearer $TOKEN_A" | jq '.status,.stage'
```

3. Enable the worker (§5) and wait for `Completed`; confirm clauses exist.
4. Disable the worker again (§5). Verify the worker logs show the disabled message and no new claims.

Record timestamps for disable → queued, enable → completed, and the final disabled state.

## 10. Evidence and closure

Write `docs/operations/staging-ai-exercise-results.md` with:

- date, commit deployed and image tag;
- the exact status codes from §7;
- run ids, clause counts, quota responses;
- kill-switch timestamps and final status;
- screenshots or log excerpts with **no secrets and no contract text**;
- open issues and whether #47 criteria are met.

#47 can be closed only when every acceptance check has evidence. #52 starts after that closure.

## 11. Stop conditions

Abort the exercise and disable the worker immediately if:

- any cross-tenant request returns 200 or non-404;
- a secret appears in a log, response or commit;
- the bucket is public;
- the worker logs a provider error that mentions an unexpected endpoint or key;
- the document is not synthetic/authorized.

After the exercise the worker **must** remain `Worker__Enabled=false`.
