# Temporary worker activation — safety design

**Status:** proposed/local changes, not committed, not deployed.

Staging normally runs with `Worker__Enabled=false` in `/opt/convenciones/.env`. The only exception is
the bounded exercise; the main `.env` is never edited to `true`.

## Activation path

```text
worker-enable.sh
  ├─ verify main .env has Worker__Enabled=false
  ├─ upload docker-compose.worker-on.yml and the remote off script
  ├─ start and verify an independent setsid watchdog (before the worker)
  ├─ start worker with:
  │    docker compose -f docker-compose.yml -f docker-compose.worker-on.yml up -d worker
  │    Worker__Enabled=true
  │    Worker__DeadlineUtc=<UTC deadline>
  │    restart: "no"
  └─ verify container env, deadline, restart policy and health
```

The override is the only place where `Worker__Enabled=true` exists for a running container.

## Why it fails closed

| Failure | Result |
|---|---|
| Workflow cancelled after watchdog start | watchdog is `setsid`-detached and disables at the deadline |
| Workflow cancelled before worker start | no worker was started; watchdog is harmless |
| Watchdog process dies | worker stops claiming at its own `Worker__DeadlineUtc`; no new jobs |
| Droplet/Docker reboot | container has `restart: "no"` and is not recreated by the default compose file |
| Deadline missing/invalid/expired at startup | `WorkerDeadlinePolicy` exits with code 3 before the host starts |
| Deadline expires while running | the loop stops before the next claim; the SQL claim also filters by deadline |
| Disable command issued but failed | `worker-off.sh` waits for healthy, checks env and restart policy; the caller retries and fails if not restored |
| Override left behind accidentally | normal deploy runs `docker compose up --force-recreate worker` **without** the override, with `.env` false |

## Shutdown path

`worker-off.sh` runs on the droplet:

1. stops the enabled worker with the override;
2. removes `docker-compose.worker-on.yml`;
3. recreates the worker from the main compose (`.env` false);
4. asserts `Worker__Enabled=false` in `.env` and in the container;
5. asserts `RestartPolicy=unless-stopped`;
6. waits for the container health status and writes `disabled-at`.

`worker-disable.sh` retries this three times and only reports success after the remote verification.
The watchdog invokes the same script, so automatic and manual shutdown share one implementation.

## Cancellation boundary\n\nThe worker now links each attempt to the earlier of `AttemptTimeoutSeconds` and `DeadlineUtc`. The token is\npassed to Document Intelligence and Foundry HTTP calls and to the stage. Cancellation stops local polling\nand processing, but it cannot recall an operation already accepted by the provider: Document Intelligence\nLROs and model requests may continue and be billed server-side after the client cancels. This is documented\nas an external limitation; the deadline prevents additional claims and the watchdog stops the container.\n\n## systemd vs setsid\n\nA system-level `systemd` timer/service would survive a `setsid` process crash and reboot, but the\n`deploy` user is not in `sudoers` (the bootstrap script only adds it to the `docker` group). A user\n`systemd` timer needs lingering enabled by root. Under the current privilege model, the combination of\nthe `setsid` watchdog, the mandatory worker deadline and the independent `worker-disable` is the\nstrongest available design. If root access is granted later, a systemd timer is the preferred second\nmechanism; it must call the same idempotent `worker-off.sh` and must not race the existing watchdog.\n\n## Remaining limitations

- A host process watchdog that is killed before the deadline leaves the container running, but the
  worker’s own deadline stops new claims; evidence is `watchdog-fired-at` / worker logs, not a stopped
  container until an operator or deadline expiry.
- The worker deadline is UTC; clock drift on the droplet/runner can shift the effective window. The
  deadline is generated on the droplet, not on the GitHub runner.
- Docker daemon restart is covered by `restart: "no"`, but a manual `docker start` of the old container
  would bypass it; operators must use `worker-enable.sh`/`worker-disable.sh`.
- The exact staging restart drill still needs to be run after approval.
