#!/usr/bin/env bash
# Stage: worker-enable. Uses a Compose override, never edits .env, starts the watchdog first and asks
# the worker to enforce the same UTC deadline itself.
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
state_exists || { echo "run the provision stage first" >&2; exit 2; }

ssh_staging "mkdir -p /opt/convenciones/.staging-ai"
scp -q deploy/docker-compose.worker-on.yml "$SSH_TARGET:/opt/convenciones/docker-compose.worker-on.yml"
scp -q scripts/staging/worker-off.sh "$SSH_TARGET:/opt/convenciones/.staging-ai/disable-worker.sh"
scp -q scripts/staging/watchdog-stop.sh "$SSH_TARGET:/opt/convenciones/.staging-ai/watchdog-stop.sh"
ssh_staging "chmod 700 /opt/convenciones/.staging-ai/disable-worker.sh /opt/convenciones/.staging-ai/watchdog-stop.sh"

ssh_staging "bash -s -- '$WINDOW_MINUTES'" <<'REMOTE'
set -eu
cd /opt/convenciones
window="$1"
mkdir -p .staging-ai

test -f .env || { echo ".env is missing on the droplet" >&2; exit 1; }
grep -qx 'Worker__Enabled=false' .env || { echo "main .env is not disabled; refusing to enable" >&2; exit 1; }
test -f docker-compose.worker-on.yml || { echo "worker override is missing on the droplet" >&2; exit 1; }
test -x .staging-ai/disable-worker.sh || { echo "disable-worker.sh is missing" >&2; exit 1; }

sh .staging-ai/watchdog-stop.sh .staging-ai/watchdog.pid
rm -f .staging-ai/watchdog-fired-at .staging-ai/watchdog-failed-at .staging-ai/watchdog-success-at .staging-ai/disabled-at .staging-ai/watchdog.log

deadline="$(date -u -d "+${window} minutes" +%Y-%m-%dT%H:%M:%SZ)"

cat > .staging-ai/watchdog.sh <<'WATCHDOG'
#!/bin/sh
set -u
sleep __WINDOW_SECONDS__
date -u +%Y-%m-%dT%H:%M:%SZ > /opt/convenciones/.staging-ai/watchdog-fired-at
cd /opt/convenciones
if .staging-ai/disable-worker.sh >> .staging-ai/watchdog.log 2>&1; then
  date -u +%Y-%m-%dT%H:%M:%SZ > .staging-ai/watchdog-success-at
else
  date -u +%Y-%m-%dT%H:%M:%SZ > .staging-ai/watchdog-failed-at
fi
WATCHDOG
sed -i "s/__WINDOW_SECONDS__/$((window * 60))/" .staging-ai/watchdog.sh
chmod 700 .staging-ai/watchdog.sh

setsid .staging-ai/watchdog.sh >/dev/null 2>&1 &
watchdog_pid=$!
sleep 1
if ! kill -0 "$watchdog_pid" 2>/dev/null; then
  echo "watchdog died before activation" >&2
  rm -f .staging-ai/watchdog.pid
  exit 1
fi
watchdog_start="$(awk '{print $22}' "/proc/$watchdog_pid/stat")"
printf '%s:%s\n' "$watchdog_pid" "$watchdog_start" > .staging-ai/watchdog.pid

armed=1
cleanup_on_error() {
  if [ "${armed:-0}" = "1" ]; then
    sh .staging-ai/watchdog-stop.sh .staging-ai/watchdog.pid >/dev/null 2>&1 || true
    .staging-ai/disable-worker.sh >/dev/null 2>&1 || true
  fi
}
trap cleanup_on_error EXIT

activate() {
  WORKER_DEADLINE_UTC="$deadline" \
    docker compose -f docker-compose.yml -f docker-compose.worker-on.yml up -d --force-recreate worker
}

if ! activate; then
  sh .staging-ai/watchdog-stop.sh .staging-ai/watchdog.pid >/dev/null 2>&1 || true
  .staging-ai/disable-worker.sh || true
  echo "worker activation failed; disabled again" >&2
  exit 1
fi

container="$(docker compose ps -q worker)"
if [ -z "$container" ]; then
  sh .staging-ai/watchdog-stop.sh .staging-ai/watchdog.pid >/dev/null 2>&1 || true
  .staging-ai/disable-worker.sh || true
  echo "worker container was not created" >&2
  exit 1
fi

envs="$(docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$container")"
printf '%s\n' "$envs" | grep -qx 'Worker__Enabled=true' || {
  echo "worker did not receive Worker__Enabled=true" >&2
  exit 1
}
printf '%s\n' "$envs" | grep -qx "Worker__DeadlineUtc=$deadline" || {
  echo "worker did not receive the expected deadline" >&2
  exit 1
}
test "$(docker inspect --format '{{.HostConfig.RestartPolicy.Name}}' "$container")" = "no" || {
  echo "worker restart policy is not no" >&2
  exit 1
}

i=0
while [ "$i" -lt 30 ]; do
  if [ "$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container")" = "healthy" ]; then
    break
  fi
  i=$((i + 1))
  sleep 1
done
test "$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container")" = "healthy"

date -u +%Y-%m-%dT%H:%M:%SZ > .staging-ai/enabled-at
armed=0
echo "worker enabled until $deadline; watchdog pid $watchdog_pid"
docker compose logs --tail=20 worker
REMOTE

{
  echo "### Worker enabled with a deadline"
  echo ""
  echo "- window: **$WINDOW_MINUTES minutes**"
  echo "- watchdog starts before the worker and is verified alive"
  echo "- main .env remains Worker__Enabled=false"
  echo "- worker receives Worker__DeadlineUtc and restart: no"
} >>"${GITHUB_STEP_SUMMARY:-/dev/null}"
