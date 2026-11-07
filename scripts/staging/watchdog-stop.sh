#!/bin/sh
# Runs on the staging droplet. Stops only the watchdog instance recorded in the pid file, after
# verifying its command line and /proc start time. A stale or unverified PID is never killed.
set -u
pidfile="${1:-/opt/convenciones/.staging-ai/watchdog.pid}"

if [ ! -f "$pidfile" ]; then
  exit 0
fi

line="$(cat "$pidfile" 2>/dev/null || true)"
pid="${line%%:*}"
starttime="${line#*:}"
case "$pid" in
  ''|*[!0-9]*)
    echo "watchdog pid file has no numeric pid; leaving it untouched" >&2
    rm -f "$pidfile"
    exit 0
    ;;
esac

if [ ! -d "/proc/$pid" ]; then
  echo "watchdog pid $pid is no longer running; removing stale pid file" >&2
  rm -f "$pidfile"
  exit 0
fi

cmdline="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)"
case "$cmdline" in
  *".staging-ai/watchdog.sh"*) ;;
  *)
    echo "pid $pid does not belong to the staging watchdog; refusing to kill it" >&2
    rm -f "$pidfile"
    exit 0
    ;;
esac

current_start="$(awk '{print $22}' "/proc/$pid/stat" 2>/dev/null || true)"
if [ -z "$current_start" ] || [ "$current_start" != "$starttime" ]; then
  echo "watchdog pid $pid has a different start time; refusing to kill it" >&2
  rm -f "$pidfile"
  exit 0
fi

if kill "$pid" 2>/dev/null; then
  echo "stopped watchdog pid $pid"
else
  echo "could not signal watchdog pid $pid; it may have exited concurrently" >&2
fi
rm -f "$pidfile"
