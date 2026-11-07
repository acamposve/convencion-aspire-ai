#!/bin/sh
# Runs on the staging droplet. Returns the worker to the disabled state and verifies it.
set -eu
root="${CONVENCIONES_ROOT:-/opt/convenciones}"
cd "$root"
mkdir -p .staging-ai

# Stop the enabled container while the override still describes it, then remove the override so a
# subsequent Compose run (and any restart) uses the main, disabled configuration.
docker compose -f docker-compose.yml stop worker
rm -f docker-compose.worker-on.yml

docker compose -f docker-compose.yml up -d --force-recreate worker

grep -qx 'Worker__Enabled=false' .env
container="$(docker compose ps -q worker)"
test -n "$container"
docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$container" | grep -qx 'Worker__Enabled=false'
test "$(docker inspect --format '{{.HostConfig.RestartPolicy.Name}}' "$container")" = "unless-stopped"

# Do not report success just because the command was issued: wait for the container to be healthy.
i=0
while [ "$i" -lt 30 ]; do
  if [ "$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container")" = "healthy" ]; then
    break
  fi
  i=$((i + 1))
  sleep 1
done
test "$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container")" = "healthy"

date -u +%Y-%m-%dT%H:%M:%SZ > .staging-ai/disabled-at
echo "worker disabled and verified at $(cat .staging-ai/disabled-at)"
