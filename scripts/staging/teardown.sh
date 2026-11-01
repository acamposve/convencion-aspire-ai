#!/usr/bin/env bash
# Stage: teardown. Disables the worker, deletes only the exercise tenants/users/objects, and removes
# the ephemeral state. It refuses to guess: without state it only guarantees the worker is off.
set -euo pipefail
# shellcheck source=scripts/staging/lib.sh
. "$(dirname "$0")/lib.sh"

require_env SUPABASE_URL SUPABASE_ANON_KEY SUPABASE_SERVICE_KEY SSH_TARGET

# Independent kill switch first: never clean data while the worker can still process it.
bash "$(dirname "$0")/worker-disable.sh"

if ! state_exists; then
  echo "no staging exercise state found; worker is disabled and nothing was deleted"
  exit 0
fi

load_state
[ -n "${COMPANY_A:-}" ] && [ -n "${COMPANY_B:-}" ] || {
  echo "state does not contain both companies; refusing to delete anything" >&2
  exit 1
}

# Remove storage objects through the service API before deleting their rows.
if command -v mapfile >/dev/null 2>&1; then
  mapfile -t PATHS < <(psql_scalar "select storage_path from platform.document where company_id in ('$COMPANY_A', '$COMPANY_B');")
  for path in "${PATHS[@]:-}"; do
    [ -n "$path" ] || continue
    curl -sS -o /dev/null -X DELETE \
      -H "apikey: $SUPABASE_SERVICE_KEY" \
      -H "Authorization: Bearer $SUPABASE_SERVICE_KEY" \
      "$SUPABASE_URL/storage/v1/object/documents/$path" || true
  done
fi

psql_run -c "
  delete from platform.clause_classification where company_id in ('$COMPANY_A', '$COMPANY_B');
  delete from platform.processing_attempt where company_id in ('$COMPANY_A', '$COMPANY_B');
  delete from platform.extracted_clause where company_id in ('$COMPANY_A', '$COMPANY_B');
  delete from platform.processing_run where company_id in ('$COMPANY_A', '$COMPANY_B');
  delete from platform.document_text where document_id in (select id from platform.document where company_id in ('$COMPANY_A', '$COMPANY_B'));
  delete from platform.document where company_id in ('$COMPANY_A', '$COMPANY_B');
  delete from platform.user_role where user_id in ('$USER_A', '$USER_B');
  delete from platform.user_profile where user_id in ('$USER_A', '$USER_B');
  delete from platform.user_account where id in ('$USER_A', '$USER_B');
  delete from app.company where id in ('$COMPANY_A', '$COMPANY_B');
"

for user in "$USER_A" "$USER_B"; do
  admin_curl -X DELETE "$SUPABASE_URL/auth/v1/admin/users/$user" >/dev/null || true
done

ssh_staging "rm -rf '$STATE_DIR'"

{
  echo "### Teardown"
  echo ""
  echo "- worker disabled and restarted"
  echo "- storage objects, AI rows, documents, profiles, users and companies for this exercise removed"
  echo "- ephemeral state removed from the droplet"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
