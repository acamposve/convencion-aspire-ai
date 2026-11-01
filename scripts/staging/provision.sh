#!/usr/bin/env bash
# Stage: provision. Creates ephemeral Auth users and the two staging tenants, then stores the
# exercise state (including ephemeral passwords) only on the droplet with mode 600.
set -euo pipefail
# shellcheck source=scripts/staging/lib.sh
. "$(dirname "$0")/lib.sh"

require_env SUPABASE_URL SUPABASE_ANON_KEY SUPABASE_SERVICE_KEY \
  SUPABASE_DB_HOST SUPABASE_DB_PORT SUPABASE_DB_NAME SUPABASE_DB_USER SUPABASE_DB_PASSWORD \
  SSH_TARGET STAGING_API_URL

if state_exists; then
  echo "staging exercise state already exists; run the teardown stage first" >&2
  exit 2
fi

COMPANY_A="$(python3 -c 'import uuid; print(uuid.uuid4())')"
COMPANY_B="$(python3 -c 'import uuid; print(uuid.uuid4())')"
SUFFIX="$(date +%s)"
EMAIL_A="tenant-a-${SUFFIX}@staging.invalid"
EMAIL_B="tenant-b-${SUFFIX}@staging.invalid"
PASSWORD_A="$(openssl rand -base64 24)"
PASSWORD_B="$(openssl rand -base64 24)"
USER_A=""
USER_B=""
SUCCESS=0

cleanup() {
  if [ "$SUCCESS" = "1" ]; then
    return
  fi
  echo "provision failed; removing partial staging test data" >&2
  if [ -n "$USER_A" ]; then admin_curl -X DELETE "$SUPABASE_URL/auth/v1/admin/users/$USER_A" >/dev/null || true; fi
  if [ -n "$USER_B" ]; then admin_curl -X DELETE "$SUPABASE_URL/auth/v1/admin/users/$USER_B" >/dev/null || true; fi
  if [ -n "$USER_A" ]; then
    psql_run -c "delete from platform.user_role where user_id = '$USER_A';" >/dev/null 2>&1 || true
    psql_run -c "delete from platform.user_profile where user_id = '$USER_A';" >/dev/null 2>&1 || true
    psql_run -c "delete from platform.user_account where id = '$USER_A';" >/dev/null 2>&1 || true
  fi
  if [ -n "$USER_B" ]; then
    psql_run -c "delete from platform.user_role where user_id = '$USER_B';" >/dev/null 2>&1 || true
    psql_run -c "delete from platform.user_profile where user_id = '$USER_B';" >/dev/null 2>&1 || true
    psql_run -c "delete from platform.user_account where id = '$USER_B';" >/dev/null 2>&1 || true
  fi
  [ -n "$COMPANY_A" ] && psql_run -c "delete from app.company where id = '$COMPANY_A';" >/dev/null 2>&1 || true
  [ -n "$COMPANY_B" ] && psql_run -c "delete from app.company where id = '$COMPANY_B';" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "checking the documents bucket is private"
BUCKETS="$(mktemp)"
chmod 600 "$BUCKETS"
CODE="$(curl -sS -o "$BUCKETS" -w '%{http_code}' \
  -H "apikey: $SUPABASE_SERVICE_KEY" \
  -H "Authorization: Bearer $SUPABASE_SERVICE_KEY" \
  "$SUPABASE_URL/storage/v1/bucket")"
if [ "$CODE" = "404" ]; then
  CREATE_CODE="$(curl -sS -o /dev/null -w '%{http_code}' -X POST \
    -H "apikey: $SUPABASE_SERVICE_KEY" \
    -H "Authorization: Bearer $SUPABASE_SERVICE_KEY" \
    -H "Content-Type: application/json" \
    --data '{"name":"documents","public":false}' \
    "$SUPABASE_URL/storage/v1/bucket")"
  [ "$CREATE_CODE" = "200" ] || [ "$CREATE_CODE" = "201" ] || {
    echo "could not create the documents bucket (HTTP $CREATE_CODE)" >&2
    exit 1
  }
elif [ "$CODE" = "200" ]; then
  jq -e '.[] | select(.name == "documents") | (.public == false)' "$BUCKETS" >/dev/null || {
    echo "the documents bucket is missing or public; refusing to continue" >&2
    exit 1
  }
else
  echo "could not list storage buckets (HTTP $CODE)" >&2
  exit 1
fi
rm -f "$BUCKETS"

create_user() {
  local email="$1" password="$2"
  local payload
  payload="$(jq -n --arg email "$email" --arg password "$password" \
    '{email:$email,password:$password,email_confirm:true}')"
  printf '%s' "$payload" | admin_curl -X POST "$SUPABASE_URL/auth/v1/admin/users" --data-binary @-
}

USER_A_JSON="$(create_user "$EMAIL_A" "$PASSWORD_A")"
USER_B_JSON="$(create_user "$EMAIL_B" "$PASSWORD_B")"
USER_A="$(printf '%s' "$USER_A_JSON" | jq -r '.id // empty')"
USER_B="$(printf '%s' "$USER_B_JSON" | jq -r '.id // empty')"
[ -n "$USER_A" ] && [ -n "$USER_B" ] || {
  echo "Supabase Auth Admin did not return user ids" >&2
  exit 1
}

sed \
  -e "s/COMPANY_A_UUID/$COMPANY_A/g" \
  -e "s/COMPANY_B_UUID/$COMPANY_B/g" \
  -e "s/__USER_A_UUID__/$USER_A/g" \
  -e "s/__USER_B_UUID__/$USER_B/g" \
  docs/operations/staging-ai-tenants-auto.sql | psql_run

{
  printf 'COMPANY_A=%q\n' "$COMPANY_A"
  printf 'COMPANY_B=%q\n' "$COMPANY_B"
  printf 'USER_A=%q\n' "$USER_A"
  printf 'USER_B=%q\n' "$USER_B"
  printf 'EMAIL_A=%q\n' "$EMAIL_A"
  printf 'EMAIL_B=%q\n' "$EMAIL_B"
  printf 'PASSWORD_A=%q\n' "$PASSWORD_A"
  printf 'PASSWORD_B=%q\n' "$PASSWORD_B"
} | save_state

SUCCESS=1
{
  echo "### Staging provisioning"
  echo ""
  echo "- company A: \`$COMPANY_A\`"
  echo "- company B: \`$COMPANY_B\`"
  echo "- auth user A: \`$USER_A\`"
  echo "- auth user B: \`$USER_B\`"
  echo ""
  echo "Ephemeral passwords were stored only on the droplet (mode 600); they are not printed."
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
