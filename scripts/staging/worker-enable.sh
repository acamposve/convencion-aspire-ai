#!/usr/bin/env bash
# Stage: worker-enable. Enables the worker for a bounded window and starts an independent watchdog on
# the droplet that disables it again even if this workflow is cancelled or the runner dies.
set -euo pipefail
# shellcheck source=scripts/staging/lib.sh
. "$(dirname "$0")/lib.sh"

require_env SSH_TARGET WINDOW_MINUTES
case "$WINDOW_MINUTES" in
  ''|*[!0-9]*) echo "WINDOW_MINUTES must be a number" >&2; exit 1 ;;
esac
if [ "$WINDOW_MINUTES" -lt 1 ] || [ "$WINDOW_MINUTES" -gt 120 ]; then
  echo "WINDOW_MINUTES must be between 1 and 120" >&2
  exit 1
fi

ssh_staging "bash -s -- '$WINDOW_MINUTES'" <<'REMOTE'
set -euo pipefail
cd /opt/convenciones
window="$1"
test -f .env || { echo ".env is missing on the droplet" >&2; exit 1; }
mkdir -p .staging-ai

if ! grep -q '^Worker__Enabled=true$' .env; then
  sed -i 's/^Worker__Enabled=false$/Worker__Enabled=true/' .env
fi

cat > .staging-ai/watchdog.sh <<'WATCH'
#!/bin/sh
sleep __WINDOW_SECONDS__
cd /opt/convenciones
sed -i 's/^Worker__Enabled=true$/Worker__Enabled=false/' .env
docker compose up -d worker
WATCH
sed -i "s/__WINDOW_SECONDS__/$((window * 60))/" .staging-ai/watchdog.sh
chmod 700 .staging-ai/watchdog.sh

if [ -f .staging-ai/watchdog.pid ]; then
  kill "$(cat .staging-ai/watchdog.pid)" 2>/dev/null || true
  rm -f .staging-ai/watchdog.pid
fi
setsid .staging-ai/watchdog.sh >/tmp/staging-worker-watchdog.log 2>&1 &
echo $! > .staging-ai/watchdog.pid

docker compose up -d worker
sleep 5
docker compose logs --tail=30 worker
echo "worker flag: $(grep '^Worker__Enabled=' .env)"
echo "watchdog pid: $(cat .staging-ai/watchdog.pid)"
REMOTE

{
  echo "### Worker enabled"
  echo ""
  echo "- window: **$WINDOW_MINUTES minutes**"
  echo "- independent droplet watchdog started; it disables the worker on expiry"
  echo "- run the completed-check stage, then dispatch worker-disable"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
