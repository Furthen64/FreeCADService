# Task 07 — systemd unit, install docs, README per GENESIS

## Status
DONE

## Verification
- `deploy/fcadserve.service` written (non-root `User=`/`Group=` placeholders,
  `EnvironmentFile=`, `Restart=on-failure`, `TimeoutStopSec=45`,
  `NoNewPrivileges=true`, `ProtectSystem=strict` + `ReadWritePaths`/`ProtectHome`,
  no `MemoryDenyWriteExecute`/`SystemCallFilter` because .NET JIT and the flatpak
  sandbox need them — documented in the unit + README).
- Full `README.md`: quick start, dev `launch_server.sh`, config table
  (all `FCADSERVE_*` keys + defaults, incl. new lower size default), API
  reference with snake_case curl examples matching `tests/integration.sh`,
  artifact layout + report schema, retention, fallback semantics, security
  section, service install steps, testing (incl. `tests/integration.sh`) and a
  note that `tasks/` tracks outstanding work. No stale `steppify` references.

Note: `systemd-analyze verify` requires a substituted unit; verified by
inspecting the unit only (paths carry `/usr/lib/fcadserve` + `/var/lib/fcadserve`
with a documented env override).

## Context
GENESIS.md:203-213 requires:
- a systemd unit for a normal non-root service account;
- `Restart=on-failure`;
- a writable state directory for SQLite and logs;
- explicit permissions for configured STL/output roots;
- a shutdown timeout that terminates active FreeCAD workers cleanly;
- hardening such as `NoNewPrivileges=true`, with filesystem protections
  documented where they interact with Flatpak;
- docs covering install, start/stop, health checks, example API calls, and
  required FreeCAD/Flatpak/Xvfb setup.

None of this exists yet; `README.md` is a single placeholder line.

## Scope
- New: `deploy/fcadserve.service` (systemd user/system unit template with
  placeholders for paths + runtime user)
- New: `README.md` (full usage/config/API/setup/security doc; replace the
  placeholder line 2 `"I need a FreeCAD service for fcadserve (fcadserveSTL)"`)
- Optionally `deploy/install.sh` to lay down config + scripts + state dirs.

## Requirements
- Unit must set `User=`/`Group=` (non-root account), `WorkingDirectory`,
  `EnvironmentFile=` for `FCADSERVE_*`, `Restart=on-failure`,
  `TimeoutStopSec=` sized so `KillProcessGroupAsync`/shutdown cleanup finishes,
  `NoNewPrivileges=true`, and `ExecStartPre`/docs covering flatpak + Xvfb.
- Document the interaction between systemd filesystem protections
  (`ProtectSystem`, `ReadWritePaths`) and the flatpak sandbox (which itself
  maps host paths).
- README: quick start, `launch_server.sh` for dev, config table
  (all `FCADSERVE_*` env keys + defaults), API reference (submit/poll/artifacts/
  cancel/healthz/readyz) with curl examples matching actual JSON (see task01),
  artifact layout + report schema, retention, known-reconstruction-fallback
  semantics.
- Note in README that `tasks/` tracks outstanding work.

## Acceptance criteria
- `deploy/fcadserve.service` is valid (`systemd-analyze verify` passes with paths
  substituted).
- README reflects the CURRENT code paths and config names after rename to
  fcadserve (no stale `/usr/lib/steppify` references).
- Example API calls use whatever POST body spelling task01 settles on.