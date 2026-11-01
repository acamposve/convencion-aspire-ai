#!/usr/bin/env bash
# Stage: completed-check. Runs after the worker has been enabled and finished the queued run.
set -euo pipefail
# shellcheck source=scripts/staging/lib.sh
. "$(dirname "$0")/lib.sh"

require_env SUPABASE_URL SUPABASE_ANON_KEY SUPABASE_SERVICE_KEY SSH_TARGET STAGING_API_URL
state_exists || { echo "run the provision and queued-check stages first" >&2; exit 2; }
load_state
[ -n "${DOC_A:-}" ] && [ -n "${RUN_A:-}" ] || { echo "no document/run in the exercise state" >&2; exit 2; }

TOKEN_A="$(api_token "$EMAIL_A" "$PASSWORD_A")"
TOKEN_B="$(api_token "$EMAIL_B" "$PASSWORD_B")"

STATUS=""
for _ in $(seq 1 60); do
  STATUS="$(curl -fsS "$STAGING_API_URL/documents/$DOC_A/processing" \
    -H "Authorization: Bearer $TOKEN_A" | jq -r '.status // empty')"
  case "$STATUS" in
    Completed) break ;;
    Failed|Cancelled)
      CODE="$(curl -fsS "$STAGING_API_URL/documents/$DOC_A/processing" \
        -H "Authorization: Bearer $TOKEN_A" | jq -r '.errorCode // "unknown"')"
      echo "run ended with $STATUS ($CODE)" >&2
      exit 1
      ;;
    Queued|Running) sleep 10 ;;
    *) echo "unexpected run status: $STATUS" >&2; exit 1 ;;
  esac
done
if [ "$STATUS" != "Completed" ]; then
  echo "run did not complete within 10 minutes; last status: $STATUS" >&2
  exit 1
fi

CLAUSES_JSON="$(curl -fsS "$STAGING_API_URL/documents/$DOC_A/clauses?page=1&pageSize=100" \
  -H "Authorization: Bearer $TOKEN_A")"
TOTAL="$(printf '%s' "$CLAUSES_JSON" | jq -r '.total // 0')"
CLAUSE_A="$(printf '%s' "$CLAUSES_JSON" | jq -r '.items[0].id // empty')"
if [ "$TOTAL" -lt 1 ] || [ -z "$CLAUSE_A" ]; then
  echo "the completed run has no clauses" >&2
  exit 1
fi

{
  echo "### Completed check"
  echo ""
  echo "- run: \`$RUN_A\`"
  echo "- clauses: **$TOTAL**"
  echo ""
  echo "| path | observed | expected |"
  echo "|---|---:|---|"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"

require_404 "$TOKEN_B" "/documents/$DOC_A"
require_404 "$TOKEN_B" "/documents/$DOC_A/text"
require_404 "$TOKEN_B" "/documents/$DOC_A/clauses"
require_404 "$TOKEN_B" "/documents/$DOC_A/processing"
require_404 "$TOKEN_B" "/processing/$RUN_A"
require_404 "$TOKEN_B" "/clauses/$CLAUSE_A"

A_CODE="$(http_status "$TOKEN_A" "/documents/$DOC_A/clauses")"
[ "$A_CODE" = "200" ] || { echo "tenant A cannot read its own clauses ($A_CODE)" >&2; exit 1; }
