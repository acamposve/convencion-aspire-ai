#!/usr/bin/env bash
# Isolated shell test for watchdog-stop.sh. A pid that does not belong to the watchdog must never be
# killed; a stale pid file is removed safely.
set -euo pipefail

script="$(cd "$(dirname "$0")/../.." && pwd)/scripts/staging/watchdog-stop.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

pidfile="$tmp/watchdog.pid"
printf '%s:%s\n' "$$" "999999" > "$pidfile"
sh "$script" "$pidfile" >/dev/null 2>&1
kill -0 "$$" 2>/dev/null
test ! -f "$pidfile"
echo "watchdog-stop refuses an unrelated pid: PASS"

printf 'not-a-pid:1\n' > "$pidfile"
sh "$script" "$pidfile" >/dev/null 2>&1
test ! -f "$pidfile"
echo "watchdog-stop removes a malformed pid file: PASS"

printf '%s:%s\n' "$$" "$(awk '{print $22}' /proc/$$/stat 2>/dev/null || echo 0)" > "$pidfile"
sh "$script" "$pidfile" >/dev/null 2>&1
kill -0 "$$" 2>/dev/null
test ! -f "$pidfile"
echo "watchdog-stop refuses a matching start time without a watchdog cmdline: PASS"
