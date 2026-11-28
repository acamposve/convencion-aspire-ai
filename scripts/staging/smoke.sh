#!/usr/bin/env bash
# T19 release smoke gate: signs in and exercises the MVP flows against the deployed staging
# environment, then removes what it created.
#
#   smoke.sh provision [--dry-run]   create or reuse the smoke tenant, rotate its password
#   smoke.sh run       [--dry-run]   sign in, upload, extract, queue, author, publish, compare, portal
#   smoke.sh teardown  [--dry-run]   unpublish and delete the tenant's content (the identity stays)
#
# Characteristics, all deliberate:
#   * synthetic data only (tests/fixtures/synthetic-contract.pdf, names prefixed SMOKE);
#   * the worker stays disabled: the gate validates the run reaches Queued and asserts that no
#     classification call was made, so it spends no model tokens;
#   * exactly one Document Intelligence extraction per run, and the summary records it;
#   * it refuses any target that is not the staging API;
#   * the tenant and its account are reused, not deleted: platform.audit_entry is append-only and its
#     actor foreign key performs ON DELETE SET NULL, an UPDATE the append-only trigger rejects, so a
#     user with audit rows cannot be deleted (verified 2026-11-22). The gate cleans content instead.
set -euo pipefail

STATE_DIR="${STATE_DIR:-/opt/convenciones/.staging-ai}"
STATE_FILE="${STATE_FILE:-$STATE_DIR/smoke.env}"
SMOKE_COMPANY_NAME="${SMOKE_COMPANY_NAME:-Smoke release gate}"
export STATE_DIR STATE_FILE

STAGE="${1:-}"
shift || true
DRY_RUN=0
for argument in "$@"; do
  case "$argument" in
    --dry-run) DRY_RUN=1 ;;
    *) echo "unknown argument: $argument" >&2; exit 2 ;;
  esac
done

usage() {
  sed -n '2,10p' "$0" >&2
  exit 2
}

case "$STAGE" in
  provision|run|teardown) ;;
  *) usage ;;
esac

: "${STAGING_API_URL:=https://138-197-15-40.sslip.io/api}"
case "$STAGING_API_URL" in
  https://138-197-15-40.sslip.io/api) ;;
  *) echo "refusing: STAGING_API_URL is not the staging API ($STAGING_API_URL)" >&2; exit 2 ;;
esac

SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/null}"
BODY="$(mktemp)"
chmod 600 "$BODY"
trap 'rm -f "$BODY"' EXIT

fail() { echo "SMOKE FAIL: $*" >&2; exit 1; }
row() { printf '| %s | %s | %s |\n' "$1" "$2" "$3" >>"$SUMMARY"; }

expect() {
  local label="$1" observed="$2" expected="$3" detail
  row "$label" "$observed" "$expected"
  if [ "$observed" != "$expected" ]; then
    # The API answers a rejected request with a small JSON problem document; printing it turns a bare
    # code into a diagnosis. It contains no credential.
    detail="$(tr '\n' ' ' <"$BODY" | cut -c1-400)"
    fail "$label returned $observed (expected $expected); response: $detail"
  fi
}

expect_any() {
  local label="$1" observed="$2"
  shift 2
  row "$label" "$observed" "$*"
  local candidate detail
  for candidate in "$@"; do
    [ "$observed" = "$candidate" ] && return 0
  done
  detail="$(tr '\n' ' ' <"$BODY" | cut -c1-400)"
  fail "$label returned $observed (expected one of: $*); response: $detail"
}

api() {
  local method="$1" path="$2" token="${3:-}"
  shift 3 || true
  local -a headers=()
  [ -n "$token" ] && headers=(-H "Authorization: Bearer $token")
  curl -sS -o "$BODY" -w '%{http_code}' -X "$method" \
    ${headers[@]+"${headers[@]}"} "$@" "$STAGING_API_URL$path"
}

json() { jq -r "$1" "$BODY"; }

storage_delete() {
  local path="$1"
  curl -sS -o /dev/null -X DELETE \
    -H "apikey: $SUPABASE_SERVICE_KEY" \
    -H "Authorization: Bearer $SUPABASE_SERVICE_KEY" \
    "$SUPABASE_URL/storage/v1/object/documents/$path" || true
}

print_plan() {
  cat <<'PLAN'
| step | method | path | expected |
|---|---|---|---|
| sign in | POST | {SUPABASE_URL}/auth/v1/token?grant_type=password | 200 + access_token |
| upload | POST | /documents (multipart file=tests/fixtures/synthetic-contract.pdf) | 201 |
| extract | POST | /documents/{id}/extract | 200 |
| read text | GET | /documents/{id}/text | 200 |
| queue | POST | /documents/{id}/process | 200 or 202, then Queued |
| headings | GET | /comparison/headings | 200 |
| agreement 1 | POST | /agreements | 201 |
| clause 1 | POST | /agreements/{id}/clauses | 201 |
| agreement 2 | POST | /agreements | 201 |
| clause 2 | POST | /agreements/{id}/clauses | 201 |
| publish | POST | /agreements/{id}/publish | 200 |
| compare | GET | /comparison/?headingId=&agreementIds= | 200 |
| portal list | GET | /public/agreements (no token) | 200 |
| portal published | GET | /public/agreements/{published} (no token) | 200 |
| portal draft | GET | /public/agreements/{draft} (no token) | 404 |
| worker off | SQL | select enabled from platform.worker_heartbeat | false |
| no model spend | SQL | count(*) from platform.ai_usage_record where stage = 'Classify' | 0 |
PLAN
}

if [ "$DRY_RUN" = "1" ]; then
  echo "dry run: stage=$STAGE target=$STAGING_API_URL"
  echo "dry run: no request is sent and no state is read"
  print_plan
  exit 0
fi

# shellcheck source=scripts/staging/lib.sh
. "$(dirname "$0")/lib.sh"

case "$STAGE" in
  provision) require_env SUPABASE_URL SUPABASE_ANON_KEY SUPABASE_SERVICE_KEY \
      SUPABASE_DB_HOST SUPABASE_DB_PORT SUPABASE_DB_NAME SUPABASE_DB_USER SUPABASE_DB_PASSWORD \
      SSH_TARGET STAGING_API_URL ;;
  run|teardown) require_env SUPABASE_URL SUPABASE_ANON_KEY SUPABASE_SERVICE_KEY \
      SUPABASE_DB_HOST SUPABASE_DB_PORT SUPABASE_DB_NAME SUPABASE_DB_USER SUPABASE_DB_PASSWORD \
      SSH_TARGET STAGING_API_URL ;;
esac

apply_tenant_sql() {
  sed \
    -e "s/__COMPANY_UUID__/$COMPANY/g" \
    -e "s/__USER_UUID__/$USER/g" \
    -e "s/__EMAIL__/$EMAIL/g" \
    -e "s/__DISPLAY_NAME__/Smoke release gate/g" \
    -e "s/__SMOKE_NAME__/$SMOKE_COMPANY_NAME/g" \
    docs/operations/smoke-tenants.sql | psql_run >/dev/null
}

save_smoke_state() {
  {
    printf 'COMPANY=%q\n' "$COMPANY"
    printf 'USER=%q\n' "$USER"
    printf 'EMAIL=%q\n' "$EMAIL"
    printf 'PASSWORD=%q\n' "$PASSWORD"
  } | save_state
}

provision() {
  local suffix password payload response

  if state_exists; then
    load_state
    suffix="$(date +%s)"
    password=$(openssl rand -base64 24)
    payload="$(jq -n --arg password "$password" '{password:$password}')"
    printf '%s' "$payload" | admin_curl -X PUT \
      "$SUPABASE_URL/auth/v1/admin/users/$USER" --data-binary @- >/dev/null \
      || fail "could not rotate the smoke user password"
    PASSWORD=$password
    echo "reusing the smoke tenant and rotating its password (run $suffix)"
  else
    suffix="$(date +%s)"
    EMAIL="smoke-${suffix}@staging.invalid"
    PASSWORD=$(openssl rand -base64 24)
    payload="$(jq -n --arg email "$EMAIL" --arg password "$PASSWORD" \
      '{email:$email,password:$password,email_confirm:true}')"
    response="$(printf '%s' "$payload" | admin_curl -X POST \
      "$SUPABASE_URL/auth/v1/admin/users" --data-binary @-)"
    USER="$(printf '%s' "$response" | jq -r '.id // empty')"
    [ -n "$USER" ] || fail "Supabase Auth did not return a user id"
    COMPANY="$(python3 -c 'import uuid; print(uuid.uuid4())')"
    echo "created the smoke tenant $COMPANY"
  fi

  apply_tenant_sql
  save_smoke_state

  {
    echo "### Release smoke gate - provision"
    echo ""
    echo "- tenant: \`$COMPANY\` (reused across runs; content is removed by teardown)"
    echo "- auth user: \`$USER\`"
    echo "- password rotated and stored only on the droplet (mode 600); it is not printed"
  } >>"$SUMMARY"
}

run_flows() {
  local token code status text_size run_rows classify_rows extract_rows

  state_exists || fail "no smoke state; run the provision stage first"
  load_state

  {
    echo "### Release smoke gate - flows"
    echo ""
    echo "| step | observed | expected |"
    echo "|---|---:|---:|"
  } >>"$SUMMARY"

  token="$(api_token "$EMAIL" "$PASSWORD")"
  row "sign in" "token issued" "token issued"

  code="$(api POST /documents "$token" -F "file=@tests/fixtures/synthetic-contract.pdf;type=application/pdf")"
  expect "upload document" "$code" 201
  DOC="$(json '.id // empty')"
  [ -n "$DOC" ] || fail "upload returned no document id"

  code="$(api POST "/documents/$DOC/extract" "$token")"
  expect "extract document" "$code" 200

  code="$(api GET "/documents/$DOC/text" "$token")"
  expect "read extracted text" "$code" 200
  text_size="$(wc -c <"$BODY")"
  [ "$text_size" -gt 50 ] || fail "extracted text is suspiciously short ($text_size bytes)"

  code="$(api POST "/documents/$DOC/process" "$token")"
  expect_any "queue processing run" "$code" 200 202
  RUN="$(json '.id // empty')"
  [ -n "$RUN" ] || fail "process returned no run id"

  status=""
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    code="$(api GET "/documents/$DOC/processing" "$token")"
    [ "$code" = "200" ] || fail "processing status returned $code"
    status="$(json '.status // empty')"
    [ "$status" = "Queued" ] && break
    sleep 3
  done
  row "processing status" "$status" "Queued"
  [ "$status" = "Queued" ] || fail "expected Queued with the worker disabled; got '$status'"

  code="$(api GET /comparison/headings "$token")"
  expect "list comparison headings" "$code" 200
  # The endpoint returns heading groups with nested headings, not a flat list of headings: pick a
  # heading from a group that does not require an economic comparison, and fall back to any heading.
  HEADING="$(json '([.[] | select(.requiresEconomicComparison == false) | .headings[]?] + [.[] | .headings[]?]) | .[0].id // empty')"
  [ -n "$HEADING" ] || fail "no comparison heading is available"

  code="$(api POST /agreements "$token" -H 'Content-Type: application/json' \
    --data "$(jq -n --arg d "$(date -u +%F)" '{validFrom:$d,validTo:null,scope:"SMOKE release gate"}')")"
  expect "create agreement 1" "$code" 201
  AGREEMENT_DRAFT="$(json '.id // empty')"
  [ -n "$AGREEMENT_DRAFT" ] || fail "agreement creation returned no id"

  code="$(api POST "/agreements/$AGREEMENT_DRAFT/clauses" "$token" -H 'Content-Type: application/json' \
    --data "$(jq -n --arg h "$HEADING" '{number:1,title:"SMOKE clause",summary:"Synthetic clause",body:"Synthetic clause body for the release smoke gate.",headingId:$h,economicComparisonNote:null,isExcludedFromCompilation:false,exclusionNote:null}')")"
  expect "author clause (draft agreement)" "$code" 201
  CLAUSE="$(json '.id // empty')"
  [ -n "$CLAUSE" ] || fail "clause creation returned no id"

  code="$(api POST /agreements "$token" -H 'Content-Type: application/json' \
    --data "$(jq -n --arg d "$(date -u -d '1 day ago' +%F)" '{validFrom:$d,validTo:null,scope:"SMOKE release gate (comparison)"}')")"
  expect "create agreement 2" "$code" 201
  AGREEMENT_DRAFT_2="$(json '.id // empty')"
  [ -n "$AGREEMENT_DRAFT_2" ] || fail "second agreement creation returned no id"

  code="$(api POST "/agreements/$AGREEMENT_DRAFT_2/clauses" "$token" -H 'Content-Type: application/json' \
    --data "$(jq -n --arg h "$HEADING" '{number:1,title:"SMOKE clause",summary:"Synthetic clause",body:"Second synthetic clause body for the comparison.",headingId:$h,economicComparisonNote:null,isExcludedFromCompilation:false,exclusionNote:null}')")"
  expect "author clause (second agreement)" "$code" 201

  code="$(api GET "/agreements/$AGREEMENT_DRAFT/clauses" "$token")"
  expect "read own clauses" "$code" 200
  grep -q "$CLAUSE" "$BODY" || fail "the clause just authored is not in the list"

  # A third agreement stays a draft: the public portal must not serve it.
  code="$(api POST /agreements "$token" -H 'Content-Type: application/json' \
    --data "$(jq -n --arg d "$(date -u -d '2 days ago' +%F)" '{validFrom:$d,validTo:null,scope:"SMOKE release gate (draft only)"}')")"
  expect "create agreement 3 (draft)" "$code" 201
  AGREEMENT_DRAFT_ONLY="$(json '.id // empty')"
  [ -n "$AGREEMENT_DRAFT_ONLY" ] || fail "third agreement creation returned no id"

  # The comparison only renders published agreements, so both compared ones are published first.
  code="$(api POST "/agreements/$AGREEMENT_DRAFT/publish" "$token")"
  expect "publish agreement 1" "$code" 200

  code="$(api POST "/agreements/$AGREEMENT_DRAFT_2/publish" "$token")"
  expect "publish agreement 2" "$code" 200

  code="$(api GET "/comparison/?headingId=$HEADING&agreementIds=$AGREEMENT_DRAFT,$AGREEMENT_DRAFT_2" "$token")"
  expect "compare two published agreements" "$code" 200
  grep -q "Synthetic clause body for the release smoke gate" "$BODY" \
    || fail "the comparison does not include the first authored clause body"
  grep -q "Second synthetic clause body for the comparison" "$BODY" \
    || fail "the comparison does not include the second authored clause body"

  code="$(api GET /public/agreements '')"
  expect "public portal list (anonymous)" "$code" 200

  code="$(api GET "/public/agreements/$AGREEMENT_DRAFT" '')"
  expect "public portal serves the published agreement" "$code" 200

  code="$(api GET "/public/agreements/$AGREEMENT_DRAFT_ONLY" '')"
  expect "public portal hides the draft" "$code" 404

  # The heading contract (#135): both comparison endpoints reject an unusable headingId with 400
  # validation_failed, and tenancy keeps answering 404. This is the only place the contract is
  # exercised against the deployed environment with a real session, so it runs on every dispatch.
  for endpoint in "comparison" "comparison/candidates"; do
    for invalid in "not-a-guid" "00000000-0000-0000-0000-000000000000"; do
      code="$(curl -sS -o "$BODY" -w '%{http_code}' \
        -H "Authorization: Bearer $token" \
        "$STAGING_API_URL/$endpoint?headingId=$invalid&agreementIds=$AGREEMENT_DRAFT")"
      expect "invalid headingId on /$endpoint ($invalid) is refused" "$code" 400
      grep -q '"code":"validation_failed"' "$BODY" \
        || fail "/$endpoint answered 400 for '$invalid' without the validation_failed code"
    done
  done

  status="$(psql_scalar "select enabled from platform.worker_heartbeat where id = 1;")"
  row "worker enabled" "$status" "f"
  [ "$status" = "f" ] || fail "the staging worker is enabled; the gate must run with it disabled"

  classify_rows="$(psql_scalar "select count(*) from platform.ai_usage_record where company_id = '$COMPANY' and stage = 'Classify';")"
  row "classification calls" "$classify_rows" "0"
  [ "$classify_rows" = "0" ] || fail "a classification call was made; the gate must not spend model tokens"

  extract_rows="$(psql_scalar "select count(*) from platform.ai_usage_record where company_id = '$COMPANY' and stage = 'Extract';")"
  run_rows="$(psql_scalar "select count(*) from platform.processing_run where company_id = '$COMPANY';")"
  row "extraction calls (declared cost)" "$extract_rows" "1 per run"
  row "processing runs for the tenant" "$run_rows" "1 per run"

  {
    echo ""
    echo "- document: \`$DOC\`, run: \`$RUN\`"
    echo "- published agreement: \`$AGREEMENT_DRAFT\`; draft: \`$AGREEMENT_DRAFT_2\`"
    echo "- the tenant keeps this content until the teardown stage removes it"
  } >>"$SUMMARY"
}

teardown() {
  local token code paths path left

  state_exists || {
    echo "no smoke state; nothing to clean (the worker is not touched by this gate)"
    exit 0
  }
  load_state

  token="$(api_token "$EMAIL" "$PASSWORD")"

  # Unpublish first: a published synthetic agreement must not outlive the run.
  code="$(api GET /agreements "$token")"
  expect "list agreements before teardown" "$code" 200
  while read -r agreement; do
    [ -n "$agreement" ] || continue
    api POST "/agreements/$agreement/unpublish" "$token" >/dev/null || true
  done < <(jq -r '.items[]?.id // empty' "$BODY")

  # Objects before rows: the row is the only record of where the bytes are.
  mapfile -t paths < <(psql_scalar "select storage_path from platform.document where company_id = '$COMPANY';")
  for path in "${paths[@]:-}"; do
    [ -n "$path" ] || continue
    storage_delete "$path"
  done

  psql_run -c "
    delete from platform.ai_usage_record where company_id = '$COMPANY';
    delete from platform.ai_budget_reservation where company_id = '$COMPANY';
    delete from platform.ai_budget_audit where company_id = '$COMPANY';
    delete from platform.ai_budget where company_id = '$COMPANY';
    delete from platform.clause_classification where company_id = '$COMPANY';
    delete from platform.extracted_clause where company_id = '$COMPANY';
    delete from platform.processing_attempt where company_id = '$COMPANY';
    delete from platform.processing_run where company_id = '$COMPANY';
    delete from platform.document_text where document_id in (select id from platform.document where company_id = '$COMPANY');
    delete from platform.document_object_deletion where company_id = '$COMPANY';
    delete from app.agreement_clause where agreement_id in (select id from app.agreement where company_id = '$COMPANY');
    delete from app.agreement where company_id = '$COMPANY';
    delete from platform.document where company_id = '$COMPANY';
    delete from platform.processing_usage where company_id = '$COMPANY';
  " >/dev/null

  left="$(psql_scalar "select (select count(*) from platform.document where company_id = '$COMPANY') + (select count(*) from app.agreement where company_id = '$COMPANY') + (select count(*) from platform.processing_run where company_id = '$COMPANY') + (select count(*) from platform.ai_usage_record where company_id = '$COMPANY');")"
  [ "$left" = "0" ] || fail "the smoke tenant still holds $left row(s) after teardown"

  {
    echo "### Release smoke gate - teardown"
    echo ""
    echo "- tenant \`$COMPANY\`: documents, agreements, runs and usage removed; \`$left\` rows left"
    echo "- storage objects for the tenant deleted through the service API"
    echo "- the tenant, its role and its account are kept for reuse (append-only audit, see the script header)"
  } >>"$SUMMARY"
}

case "$STAGE" in
  provision) provision ;;
  run) run_flows ;;
  teardown) teardown ;;
esac
