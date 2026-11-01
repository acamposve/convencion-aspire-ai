# HTTPS staging — implementation and verification results

**Date:** 2026-11-01 · **Host:** `138-197-15-40.sslip.io` · **Status:** implemented and verified;
worker remains disabled.

## Infrastructure

| Check | Result |
|---|---|
| DNS | `A 138-197-15-40.sslip.io -> 138.197.15.40` |
| Port 80 | open |
| Port 443 | open after Caddy deployment |
| TLS certificate | `CN=138-197-15-40.sslip.io`, issuer `Let's Encrypt (YE1)`, expires 2027-01-30 |
| HTTP | `308 Permanent Redirect` to `https://138-197-15-40.sslip.io/...` |
| HTTPS readiness | `Healthy` |
| Caddy | `Server: Caddy` in front of nginx |

## Deployment path

- PR #71 replaced the direct port 80 publication with Caddy, added automatic Let's Encrypt and the
  HTTPS origin for CORS/readiness, kept previous compose/Caddyfile for rollback and added `Staging smoke`.
- PR #72 fixed the retention sweeper DI registration.
- PR #73 fixed the worker database key names (`Database:Host/User/Password` versus the literal
  `Database__Host` keys).
- PR #75 removed the duplicate `AzureDocumentIntelligenceClient` constructor that made the typed client
  unresolvable.

Every deployment was manual (`Deploy staging`, `confirm=deploy`); the merge did not deploy by itself.
Final deploy run: `41325213422`.

## Post-deploy smoke (run `41325470122`)

| Check | Evidence |
|---|---|
| HTTPS readiness | `Healthy` |
| HTTP→HTTPS | `308 -> https://138-197-15-40.sslip.io/health/ready` |
| Worker flag | `Worker__Enabled=false` |
| Worker container | `Up 52 seconds (healthy)` |
| Required config names | present in the API container (values never printed) |
| `.env` in web container | absent |
| Dotfile exposure | `https://.../.env` → **403** |
| Azure Foundry network | smoke step passed |
| Document Intelligence network | smoke step passed |
| Supabase network | smoke step passed |
| Secrets | none printed in logs or responses |

## Security observations

- Caddy terminates TLS; nginx and API remain internal.
- The service-role key is only in the backend `.env`; the web image receives only public build values.
- Dotfiles are denied by nginx; `/health/*` and the SPA remain reachable.
- The worker is disabled before and after the exercise; the stack remains `Worker__Enabled=false`.

## Remaining limitations

- The GitHub private repository has no native Environment reviewers on the current plan; deployment is
  gated by `workflow_dispatch` + `confirm=deploy` and explicit conversation approval.
- UFW still lists only 22/80 in the bootstrap script; Docker's published 443 works, but the host firewall
  rule should be added by root when possible (no sudo for the deploy user).
- No document processing, no `provision`, no worker enablement and no real contract were performed.
- Provider terms and the synthetic-only policy remain pending.

## Next

After explicit approval, run the `Staging AI exercise` stages in order: `provision`, `queued-check`,
`worker-enable` (bounded), `completed-check`, `worker-disable`, `teardown`.