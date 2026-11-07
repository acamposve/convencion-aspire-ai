#!/usr/bin/env bash
# Stage: worker-disable. Independent kill switch; safe to run at any time and after a failed workflow.
set -euo pipefail
# shellcheck source=scripts/staging/lib.sh
. "$(dirname "$0")/lib.sh"

require_env SSH_TARGET

ssh_staging "mkdir -p /opt/convenciones/.staging-ai"
scp -q scripts/staging/worker-off.sh "$SSH_TARGET:/opt/convenciones/.staging-ai/disable-worker.sh"
scp -q scripts/staging/watchdog-stop.sh "$SSH_TARGET:/opt/convenciones/.staging-ai/watchdog-stop.sh"
ssh_staging "chmod 700 /opt/convenciones/.staging-ai/disable-worker.sh /opt/convenciones/.staging-ai/watchdog-stop.sh"

# Retry the disable path; never report success just because a command was issued.
outcome=1
for attempt in 1 2 3; do
  if ssh_staging "cd /opt/convenciones && .staging-ai/disable-worker.sh"; then
    outcome=0
    break
  fi
  echo "disable attempt $attempt failed; retrying" >&2
  sleep 3
done

if [ "$outcome" -ne 0 ]; then
  echo "worker-disable failed after 3 attempts; the worker may still be enabled. Do not leave it in this state." >&2
  ssh_staging "cd /opt/convenciones && docker compose ps worker && docker compose logs --tail=50 worker" >&2 || true
  exit 1
fi

ssh_staging "cd /opt/convenciones && sh .staging-ai/watchdog-stop.sh .staging-ai/watchdog.pid; rm -f .staging-ai/watchdog.sh"

EVIDENCE="$(ssh_staging "cd /opt/convenciones && grep '^Worker__Enabled=' .env && docker compose ps worker && cat .staging-ai/disabled-at")"
printf '%s\n' "$EVIDENCE"
{
  echo "### Worker disabled and verified"
  echo ""
  echo '```'
  printf '%s\n' "$EVIDENCE"
  echo '```'
  echo ""
  echo "- override removed; default compose recreated the worker with env false"
  echo "- restart policy verified as unless-stopped"
  echo "- watchdog stopped after the verified disable"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
