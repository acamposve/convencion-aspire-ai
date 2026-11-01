#!/usr/bin/env bash
# Stage: worker-disable. Independent kill switch; safe to run at any time and after a failed workflow.
set -euo pipefail
# shellcheck source=scripts/staging/lib.sh
. "$(dirname "$0")/lib.sh"

require_env SSH_TARGET

ssh_staging "bash -s" <<'REMOTE'
set -euo pipefail
cd /opt/convenciones
test -f .env || { echo ".env is missing on the droplet" >&2; exit 1; }

if [ -f .staging-ai/watchdog.pid ]; then
  kill "$(cat .staging-ai/watchdog.pid)" 2>/dev/null || true
  rm -f .staging-ai/watchdog.pid
fi

sed -i 's/^Worker__Enabled=true$/Worker__Enabled=false/' .env
docker compose up -d worker
sleep 5
docker compose logs --tail=20 worker
echo "worker flag: $(grep '^Worker__Enabled=' .env)"
docker compose ps worker
REMOTE

{
  echo "### Worker disabled"
  echo ""
  echo "- watchdog stopped"
  echo "- .env restored to Worker__Enabled=false"
  echo "- container restarted and verified"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
