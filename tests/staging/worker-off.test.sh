#!/usr/bin/env bash
# Isolated shell test for scripts/staging/worker-off.sh. Uses a fake docker executable and a temporary
# root; it never talks to Docker or the Droplet.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

mkdir -p "$tmp/root/.staging-ai" "$tmp/fake"
printf 'Worker__Enabled=false\n' > "$tmp/root/.env"
printf 'services:\n  worker:\n    image: fake\n' > "$tmp/root/docker-compose.worker-on.yml"

cat > "$tmp/fake/docker" <<'FAKE'
#!/bin/sh
case "$1" in
  compose)
    case "$*" in
      *"ps -q worker"*) echo fake-worker ;;
      *) exit 0 ;;
    esac
    ;;
  inspect)
    case "$*" in
      *RestartPolicy*) echo unless-stopped ;;
      *Config.Env*) echo Worker__Enabled=false ;;
      *State.Health*) echo healthy ;;
      *) exit 0 ;;
    esac
    ;;
  *) exit 0 ;;
esac
FAKE
chmod 755 "$tmp/fake/docker"

CONVENCIONES_ROOT="$tmp/root" PATH="$tmp/fake:$PATH" sh "$repo_root/scripts/staging/worker-off.sh" >/dev/null
test ! -f "$tmp/root/docker-compose.worker-on.yml"
test -s "$tmp/root/.staging-ai/disabled-at"
echo "worker-off success path: PASS"

cat > "$tmp/fake/docker" <<'FAKE'
#!/bin/sh
case "$1" in
  compose)
    case "$*" in
      *"ps -q worker"*) echo fake-worker ;;
      *) exit 0 ;;
    esac
    ;;
  inspect)
    case "$*" in
      *Config.Env*) echo Worker__Enabled=true ;;
      *State.Health*) echo healthy ;;
      *) echo unless-stopped ;;
    esac
    ;;
  *) exit 0 ;;
esac
FAKE
chmod 755 "$tmp/fake/docker"
rm -f "$tmp/root/.staging-ai/disabled-at"
if CONVENCIONES_ROOT="$tmp/root" PATH="$tmp/fake:$PATH" sh "$repo_root/scripts/staging/worker-off.sh" >/dev/null 2>&1; then
  echo "worker-off failure path: FAIL (should reject enabled container)" >&2
  exit 1
fi
test ! -f "$tmp/root/.staging-ai/disabled-at"
echo "worker-off failure path: PASS"
