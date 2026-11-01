#!/usr/bin/env bash
# Shared helpers for the staging-only exercise. Secrets arrive as environment variables from the
# GitHub staging environment; this file never prints them.
set -euo pipefail

STATE_DIR="${STATE_DIR:-/opt/convenciones/.staging-ai}"
STATE_FILE="${STATE_FILE:-$STATE_DIR/state.env}"
SSH_TARGET="${SSH_TARGET:?SSH_TARGET is required}"

require_env() {
  local name
  for name in "$@"; do
    if [ -z "${!name:-}" ]; then
      echo "missing required environment value: $name" >&2
      exit 1
    fi
  done
}

ssh_staging() {
  ssh -o BatchMode=yes -o StrictHostKeyChecking=yes "$SSH_TARGET" "$@"
}

save_state() {
  ssh_staging "umask 077; mkdir -p '$STATE_DIR'; cat > '$STATE_FILE'"
}

append_state() {
  ssh_staging "umask 077; cat >> '$STATE_FILE'"
}

state_exists() {
  ssh_staging "test -s '$STATE_FILE'"
}

load_state() {
  local tmp
  tmp="$(mktemp)"
  chmod 600 "$tmp"
  ssh_staging "cat '$STATE_FILE'" >"$tmp"
  # shellcheck disable=SC1090
  . "$tmp"
  rm -f "$tmp"
}

admin_curl() {
  curl -sS \
    -H "apikey: $SUPABASE_SERVICE_KEY" \
    -H "Authorization: Bearer $SUPABASE_SERVICE_KEY" \
    -H "Content-Type: application/json" \
    "$@"
}

api_token() {
  local email="$1" password="$2" payload token
  payload="$(jq -n --arg email "$email" --arg password "$password" '{email:$email,password:$password}')"
  token="$(printf '%s' "$payload" | curl -sS -X POST \
    "$SUPABASE_URL/auth/v1/token?grant_type=password" \
    -H "apikey: $SUPABASE_ANON_KEY" \
    -H "Content-Type: application/json" \
    --data-binary @- | jq -r '.access_token // empty')"
  if [ -z "$token" ]; then
    echo "could not obtain a staging access token" >&2
    exit 1
  fi
  printf '%s' "$token"
}

psql_run() {
  PGPASSWORD="$SUPABASE_DB_PASSWORD" PGSSLMODE=require psql \
    --host "$SUPABASE_DB_HOST" \
    --port "$SUPABASE_DB_PORT" \
    --username "$SUPABASE_DB_USER" \
    --dbname "$SUPABASE_DB_NAME" \
    --set ON_ERROR_STOP=1 \
    --no-psqlrc \
    "$@"
}

psql_scalar() {
  psql_run -tA -c "$1"
}

http_status() {
  local token="$1" path="$2"
  curl -sS -o /dev/null -w '%{http_code}' \
    -H "Authorization: Bearer $token" \
    "$STAGING_API_URL$path"
}

require_404() {
  local token="$1" path="$2" code
  code="$(http_status "$token" "$path")"
  if [ "$code" != "404" ]; then
    echo "cross-tenant check failed: $path returned $code (expected 404)" >&2
    exit 1
  fi
  printf '| %s | %s | 404 expected |\n' "$path" "$code" >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
}
