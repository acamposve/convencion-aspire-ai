#!/usr/bin/env bash
# Proves the D2.1 deployment guard against the real Compose files:
#   1. an inherited Worker__Enabled=true really does reach the rendered worker configuration;
#   2. the deployment's explicit export forces it back to false;
#   3. the guard rejects every non-"false" rendering, including the activation override.
#
# Requires only the Docker Compose CLI: `docker compose config` parses the files and does not
# talk to a daemon.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
guard="$repo_root/scripts/deploy/assert-worker-disabled.py"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp "$repo_root/deploy/docker-compose.yml" "$work/docker-compose.yml"
cp "$repo_root/deploy/docker-compose.worker-on.yml" "$work/docker-compose.worker-on.yml"
cp "$repo_root/deploy/Caddyfile" "$work/Caddyfile"
cat > "$work/.env" <<'ENV'
IMAGE_TAG=guard-test
API_IMAGE=ghcr.io/example/api:guard-test
WEB_IMAGE=ghcr.io/example/web:guard-test
Worker__Enabled=false
Retention__IntervalMinutes=360
Retention__BatchSize=100
ASPNETCORE_URLS=http://+:8080
ENV

cd "$work"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

# Reads the worker flag back out of a rendered model, so each case asserts what was actually
# rendered rather than only how the guard reacted.
worker_flag() {
    python3 - "$1" <<'PY'
import json, sys
model = json.load(open(sys.argv[1], encoding="utf-8"))
value = ((model.get("services") or {}).get("worker") or {}).get("environment") or {}
print(value.get("Worker__Enabled"))
PY
}

# 1. The hazard is real: Compose interpolates the shell environment with higher precedence than .env.
( export Worker__Enabled=true; docker compose config --format json ) > inherited.json
[ "$(worker_flag inherited.json)" = "true" ] || fail 'the inherited variable did not reach the rendered configuration'
if python3 "$guard" inherited.json; then
    fail 'the guard accepted an inherited Worker__Enabled=true'
fi

# 2. The deployment's explicit export defeats the inherited value.
( export Worker__Enabled=false; docker compose config --format json ) > forced.json
[ "$(worker_flag forced.json)" = "false" ] || fail 'the explicit export did not reach the rendered configuration'
python3 "$guard" forced.json || fail 'the guard rejected an explicitly disabled worker'

# 3. Without any shell variable, .env still disables the worker.
( unset Worker__Enabled; docker compose config --format json ) > from-env.json
[ "$(worker_flag from-env.json)" = "false" ] || fail 'the .env default did not reach the rendered configuration'
python3 "$guard" from-env.json || fail 'the guard rejected the .env default'

# 4. Activation keeps its own explicit path; the guard refuses that rendering.
( export Worker__Enabled=false WORKER_DEADLINE_UTC=2030-01-01T00:00:00Z; \
  docker compose -f docker-compose.yml -f docker-compose.worker-on.yml config --format json ) > activated.json
[ "$(worker_flag activated.json)" = "true" ] || fail 'the activation override did not render Worker__Enabled=true'
if python3 "$guard" activated.json; then
    fail 'the guard accepted the worker activation override'
fi

# 5. Structural failures are rejected rather than assumed safe.
printf '{"services":{}}' > no-worker.json
if python3 "$guard" no-worker.json; then
    fail 'the guard accepted a model without a worker'
fi

printf '{"services":{"worker":{"environment":{}}}}' > unresolved.json
if python3 "$guard" unresolved.json; then
    fail 'the guard accepted an unresolved worker flag'
fi

printf '{"services":{"worker":{"environment":{"Worker__Enabled":"true"}}}}' > literal-true.json
if python3 "$guard" literal-true.json; then
    fail 'the guard accepted Worker__Enabled=true'
fi

printf 'not json' > malformed.json
if python3 "$guard" malformed.json; then
    fail 'the guard accepted a malformed model'
fi

printf 'D2.1 worker-disabled deployment guard: all checks passed\n'
