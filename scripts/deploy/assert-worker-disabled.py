#!/usr/bin/env python3
"""Fail unless the rendered Compose model starts the worker with Worker__Enabled=false.

The deployment renders the effective Compose configuration on the host with
``docker compose config --format json`` and pipes the result here before any container is
started. That rendering is the only place where every input is visible at once: the compose
files (including any ``docker-compose.override.yml`` that Compose loads implicitly), the
project ``.env``, and the shell environment that Compose interpolates with higher precedence
than ``.env``.

Anything other than the exact string ``"false"`` fails the deployment: ``"true"``,
``"False"``, an unresolved empty value, a missing variable or a missing worker service all
mean the worker could start enabled, and the retention sweeper runs inside that container.
"""

from __future__ import annotations

import json
import sys

EXPECTED = "false"


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: assert-worker-disabled.py <rendered-compose.json>", file=sys.stderr)
        return 2

    try:
        with open(argv[1], encoding="utf-8") as handle:
            model = json.load(handle)
    except (OSError, json.JSONDecodeError) as error:
        print(f"could not read the rendered Compose configuration: {error}", file=sys.stderr)
        return 1

    services = model.get("services") if isinstance(model, dict) else None
    worker = services.get("worker") if isinstance(services, dict) else None
    environment = worker.get("environment") if isinstance(worker, dict) else None
    value = environment.get("Worker__Enabled") if isinstance(environment, dict) else None

    if value != EXPECTED:
        print(
            f"refusing to continue: worker Worker__Enabled is {value!r}, expected {EXPECTED!r}",
            file=sys.stderr,
        )
        return 1

    print("worker is explicitly disabled in the rendered Compose configuration")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
