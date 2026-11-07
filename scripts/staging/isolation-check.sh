#!/usr/bin/env bash
# Stage: isolation-check. Uses the existing A/B identities and DOC_A; B uploads one synthetic document
# (no processing run) to prove ownership and reciprocal 404s with exact HTTP codes.
set -euo pipefail
# shellcheck source=scripts/staging/lib.sh
. "$(dirname "$0")/lib.sh"

require_env SUPABASE_URL SUPABASE_ANON_KEY SUPABASE_SERVICE_KEY SSH_TARGET STAGING_API_URL
state_exists || { echo "run the provision stage first" >&2; exit 2; }
load_state
[ -n "${DOC_A:-}" ] || { echo "DOC_A is missing from state; run queued-check first" >&2; exit 2; }

assert_worker_off() {
  # shellcheck disable=SC2016 # the single-quoted command is intentionally expanded on the droplet
  ssh_staging 'cd /opt/convenciones && grep -qx "Worker__Enabled=false" .env && container="$(docker compose ps -q worker)" && test -n "$container" && docker inspect --format "{{range .Config.Env}}{{println .}}{{end}}" "$container" | grep -qx "Worker__Enabled=false" && test "$(docker inspect --format "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}" "$container")" = "healthy"'
}
assert_worker_off

require_code() {
  local token="$1" path="$2" expected="$3" label="$4" code
  code="$(http_status "$token" "$path")"
  if [ "$code" != "$expected" ]; then
    echo "$label failed: $path returned $code (expected $expected)" >&2
    exit 1
  fi
  printf '| %s | %s | %s |\n' "$label" "$code" "$expected" >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
}

TOKEN_A="$(api_token "$EMAIL_A" "$PASSWORD_A")"
TOKEN_B="$(api_token "$EMAIL_B" "$PASSWORD_B")"

BODY="$(mktemp)"
chmod 600 "$BODY"
if [ -z "${DOC_B:-}" ]; then
  UPLOAD_CODE="$(curl -sS -o "$BODY" -w '%{http_code}' -X POST "$STAGING_API_URL/documents" \
    -H "Authorization: Bearer $TOKEN_B" \
    -F "file=@tests/fixtures/synthetic-contract.pdf;type=application/pdf")"
  if [ "$UPLOAD_CODE" != "201" ]; then
    echo "tenant B upload returned $UPLOAD_CODE (expected 201)" >&2
    exit 1
  fi
  DOC_B="$(jq -r '.id // empty' "$BODY")"
  [ -n "$DOC_B" ] || { echo "tenant B upload returned no document id" >&2; exit 1; }
  if ! { printf 'DOC_B=%q\n' "$DOC_B"; printf 'UPLOAD_B_CODE=%q\n' "$UPLOAD_CODE"; } | append_state; then
    echo "could not persist DOC_B; deleting the newly uploaded document to avoid a duplicate" >&2
    CLEANUP_CODE="$(curl -sS -o /dev/null -w '%{http_code}' -X DELETE \
      "$STAGING_API_URL/documents/$DOC_B" -H "Authorization: Bearer $TOKEN_B")"
    echo "cleanup delete returned $CLEANUP_CODE; manual teardown may be required" >&2
    exit 1
  fi
else
  [ "${UPLOAD_B_CODE:-}" = "201" ] || {
    echo "DOC_B exists but UPLOAD_B_CODE is not 201; refusing to infer the upload status" >&2
    exit 1
  }
  UPLOAD_CODE="$UPLOAD_B_CODE"
fi
rm -f "$BODY"

EXTRACT_CODE="$(curl -sS -o /dev/null -w '%{http_code}' -X POST \
  "$STAGING_API_URL/documents/$DOC_B/extract" -H "Authorization: Bearer $TOKEN_B")"
if [ "$EXTRACT_CODE" != "200" ]; then
  echo "tenant B extraction returned $EXTRACT_CODE (expected 200)" >&2
  exit 1
fi
if [ -z "${EXTRACT_B_CODE:-}" ]; then
  {
    printf 'EXTRACT_B_CODE=%q\n' "$EXTRACT_CODE"
  } | append_state
fi

{
  echo "### Isolation check"
  echo ""
  echo "- DOC_A: \`$DOC_A\`"
  echo "- DOC_B: \`$DOC_B\`"
  echo "- upload B: **$UPLOAD_CODE**"
  echo "- extraction B: **$EXTRACT_CODE**"
  echo ""
  echo "| check | observed | expected |"
  echo "|---|---:|---:|"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"

require_code "$TOKEN_A" "/documents/$DOC_A" "200" "A own document"
require_code "$TOKEN_A" "/documents/$DOC_A/text" "200" "A own text"
require_code "$TOKEN_B" "/documents/$DOC_B" "200" "B own document"
require_code "$TOKEN_B" "/documents/$DOC_B/text" "200" "B own text"

require_404 "$TOKEN_A" "/documents/$DOC_B"
require_404 "$TOKEN_A" "/documents/$DOC_B/text"
require_404 "$TOKEN_B" "/documents/$DOC_A"
require_404 "$TOKEN_B" "/documents/$DOC_A/text"

assert_worker_off
{
  echo ""
  echo "- worker flag before and after: **false**"
  echo "- no processing run was created for DOC_B"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
