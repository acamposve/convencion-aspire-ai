#!/usr/bin/env bash
# Stage: queued-check. Runs with the worker disabled; proves the run is accepted and stays Queued,
# then proves tenant B cannot read any of tenant A's resources.
set -euo pipefail
# shellcheck source=scripts/staging/lib.sh
. "$(dirname "$0")/lib.sh"

require_env SUPABASE_URL SUPABASE_ANON_KEY SUPABASE_SERVICE_KEY SSH_TARGET STAGING_API_URL
state_exists || { echo "run the provision stage first" >&2; exit 2; }
load_state

TOKEN_A="$(api_token "$EMAIL_A" "$PASSWORD_A")"
TOKEN_B="$(api_token "$EMAIL_B" "$PASSWORD_B")"

UPLOAD_JSON="$(curl -fsS -X POST "$STAGING_API_URL/documents" \
  -H "Authorization: Bearer $TOKEN_A" \
  -F "file=@tests/fixtures/synthetic-contract.pdf;type=application/pdf")"
DOC_A="$(printf '%s' "$UPLOAD_JSON" | jq -r '.id // empty')"
[ -n "$DOC_A" ] || { echo "upload did not return a document id" >&2; exit 1; }

curl -fsS -X POST "$STAGING_API_URL/documents/$DOC_A/extract" \
  -H "Authorization: Bearer $TOKEN_A" >/dev/null

PROCESS_JSON="$(curl -fsS -X POST "$STAGING_API_URL/documents/$DOC_A/process" \
  -H "Authorization: Bearer $TOKEN_A")"
RUN_A="$(printf '%s' "$PROCESS_JSON" | jq -r '.id // empty')"
[ -n "$RUN_A" ] || { echo "process did not return a run id" >&2; exit 1; }

sleep 15
STATUS="$(curl -fsS "$STAGING_API_URL/documents/$DOC_A/processing" \
  -H "Authorization: Bearer $TOKEN_A" | jq -r '.status // empty')"
if [ "$STATUS" != "Queued" ]; then
  echo "expected Queued while the worker is disabled; got $STATUS" >&2
  exit 1
fi

{
  printf 'DOC_A=%q\n' "$DOC_A"
  printf 'RUN_A=%q\n' "$RUN_A"
} | append_state

{
  echo "### Queued check"
  echo ""
  echo "- document: \`$DOC_A\`"
  echo "- run: \`$RUN_A\`"
  echo "- status after 15s: **$STATUS** (worker disabled)"
  echo ""
  echo "| path | observed | expected |"
  echo "|---|---:|---|"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"

require_404 "$TOKEN_B" "/documents/$DOC_A"
require_404 "$TOKEN_B" "/documents/$DOC_A/text"
require_404 "$TOKEN_B" "/documents/$DOC_A/processing"
require_404 "$TOKEN_B" "/processing/$RUN_A"
