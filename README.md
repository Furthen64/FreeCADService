# fcadserve

`fcadserve` is a small HTTP service that converts STL meshes to STEP (and
analytic geometry) using FreeCAD, then renders six orthographic preview images
of the result: isometric, cut-away, left, top, right, and bottom. It is
implemented as a .NET 10 ASP.NET Core
service that drives FreeCAD in two phases:

1. **headless pipeline** (`FreeCADCmd`, no display): load STL -> reconstruct
   analytic geometry (or intentionally fall back) -> export STEP -> re-import
   and validate -> write a JSON report;
2. **GUI render** (FreeCAD under a per-job Xvfb display): render all six views
   and patch the report.

A reconstruction that cannot be turned into a validated analytic solid is not
an error: the job finishes with status `skipped` and publishes the fallback
STEP plus images, and it is clearly distinguishable from an infrastructure
failure.

## Requirements

- .NET 10 SDK/runtime
- FreeCAD. Two modes are supported via `FCADSERVE_FREECAD__MODE`:
  - `flatpak` (default): `org.freecad.FreeCAD` from Flathub, exposing
    `FreeCAD`, `FreeCADCmd`, `freecadcmd`. Install with
    `flatpak install flathub org.freecad.FreeCAD`.
  - `exec`: a raw `FreeCAD` executable path in `FCADSERVE_FREECAD__COMMAND`.
    There is **no sandbox** in this mode — see Security.
- `Xvfb` (for the render phase).
- The Python scripts in `freecad/` (worker + renderer) at the paths referenced
  by `FCADSERVE_WORKER_SCRIPT` / `FCADSERVE_RENDER_SCRIPT`.

## Quick start (development)

```bash
./launch_server.sh 8777
```

This builds the service if needed, resets `work/e2e_state` + `work/e2e_out`
(set `FCADSERVE_NO_CLEAN=1` to keep them), and runs the service on port 8777
with sensible local defaults. Submit a job:

```bash
curl -X POST http://127.0.0.1:8777/v1/jobs \
  -H 'content-type: application/json' \
  -d '{"stl_path":"/home/you/FreeCADService/work/box.stl","output_dir":"/home/you/FreeCADService/work/e2e_out"}'
```

Both `stl_path`/`output_dir` (documented) and `stlPath`/`outputDir` are
accepted.

## Configuration

Configuration binds from the root; environment variables use the `FCADSERVE_`
prefix with `__` for nesting. Precedence: `fcadserve.json` < environment <
command line. Command-line overrides use .NET's `--Key=Value` form (nested
keys keep the `:` path separator, e.g. `--Port=9000` or
`--Jobs:TimeoutSeconds=30`).

| Key | Default | Meaning |
|-----|---------|---------|
| `FCADSERVE_BIND_ADDRESS` | `127.0.0.1` | Bind interface |
| `FCADSERVE_PORT` | `8688` | HTTP port |
| `FCADSERVE_STATE_ROOT` | `/var/lib/fcadserve` | SQLite DB, job work dirs, logs |
| `FCADSERVE_ALLOWED_INPUT_ROOTS` | — | `;`/`,`-separated roots where input STLs may live; empty rejects all |
| `FCADSERVE_ALLOWED_OUTPUT_ROOTS` | — | where outputs may be written; empty = only the input's parent directory |
| `FCADSERVE_WORKER_SCRIPT` | `/usr/lib/fcadserve/fcadserve_worker.py` | headless pipeline script |
| `FCADSERVE_RENDER_SCRIPT` | `/usr/lib/fcadserve/fcadserve_render.py` | GUI render script |
| `FCADSERVE_OVERWRITE_ARTIFACTS` | `false` | allow overwriting existing artifacts |
| `FCADSERVE_UPLOAD_ROOT` | `<StateRoot>/uploads` | staging dir for `POST /v1/uploads`; always an allowed input root |
| `FCADSERVE_LOG_LEVEL` | `Information` | trace/debug/information/warning/error |
| `FCADSERVE_FREECAD__MODE` | `flatpak` | `flatpak` or `exec` |
| `FCADSERVE_FREECAD__FLATPAK_APP_ID` | `org.freecad.FreeCAD` | flatpak application id |
| `FCADSERVE_FREECAD__COMMAND` | — | raw executable for `exec` mode |
| `FCADSERVE_FREECAD__STARTUP_TIMEOUT_SEC` | `120` | seconds to allow FreeCAD startup |
| `FCADSERVE_FREECAD__ISOLATE_USER_CONFIG` | `true` | per-job XDG dirs (flatpak ignores these; see Security) |
| `FCADSERVE_XVFB__EXECUTABLE` | `Xvfb` | Xvfb binary |
| `FCADSERVE_XVFB__SCREEN` | `1280x1024x24` | virtual screen geometry |
| `FCADSERVE_JOBS__WORKER_COUNT` | `1` | concurrent FreeCAD subprocesses |
| `FCADSERVE_JOBS__QUEUE_CAPACITY` | `100` | max queued jobs |
| `FCADSERVE_JOBS__TIMEOUT_SECONDS` | `600` | per-job wall-clock budget (pipeline 2/3, render the rest) |
| `FCADSERVE_JOBS__MAX_INPUT_SIZE_BYTES` | `52428800` (50 MiB) | max accepted input STL |
| `FCADSERVE_JOBS__REQUEUE_RUNNING_JOBS_ON_STARTUP` | `true` | re-queue interrupted jobs on restart |
| `FCADSERVE_RETENTION__MAX_RETAINED_JOBS` | `500` | terminal job rows/work dirs kept |

List-style values are `;`- or `,`-separated:
`FCADSERVE_ALLOWED_INPUT_ROOTS="/data/a;/data/b"`.

## API

All endpoints return JSON.

### `POST /v1/uploads?name=part.stl`

Stage an STL on the server (raw binary body; filename from `?name=` or the
`Content-Disposition` header). Returns `201` with `{upload_id, name, stl_path,
size_bytes, sha256}` — `stl_path` is absolute and already inside an allowed
input root, so it can be submitted to `/v1/jobs` directly. Names are sanitized
to a bare `stem.stl`, stored uniquely (never overwritten), streamed with a
running SHA-256, and rejected above `MaxInputSizeBytes` (`413 input_too_large`).
Errors: `400 missing_filename` / `unsupported_extension` / `empty_upload`.

### `POST /v1/jobs`

Submit an STL conversion. Body:

```json
{ "stl_path": "/data/part.stl", "output_dir": "/data/out" }
```

- `output_dir` optional: empty/absent means "the STL's parent directory".
- Responses: `202` queued (`{job_id, status:"queued", status_url}`);
  `400` validation (`relative_path`, `missing_stl_path`, `missing_file`,
  `unsupported_extension`, `input_outside_allowed_root`,
  `output_outside_allowed_root`, `input_too_large`); `409` if a derived
  artifact already exists (`output_already_exists`) or would collide with the
  input; `429` if the queue is full.

### `GET /v1/jobs/{job_id}`

Job status incl. artifacts. Statuses: `queued`, `running`, `succeeded`,
`skipped`, `failed`, `cancelled`.

### `GET /v1/jobs/{job_id}/artifacts`

Artifact manifest (`files` with `name`, `kind`, `path`, `size_bytes`).

### `GET /v1/jobs/{job_id}/artifacts/{name}`

Download one artifact (STEP, PNGs, report, console log).

### `POST /v1/jobs/{job_id}/cancel`

Cancel a queued or running job. Running FreeCAD process groups are terminated.

### `GET /healthz`

Liveness: always `{"status":"ok"}` when the process responds.

### `GET /readyz`

Readiness: `200` with `ready:true`, or `503` with per-check details covering
the database, state root, Xvfb, the worker script, the FreeCAD mode, and the
configured root policies.

## Artifacts and report

With an input `part.stl`, outputs published into the output directory are:

- `part.stp` — STEP export (kind `step`);
- `part_iso.png` — isometric preview (kind `iso_png`);
- `part_section.png` — cutaway preview (kind `section_png`);
- `part_left.png` — left view (kind `left_png`);
- `part_top.png` — top view (kind `top_png`);
- `part_right.png` — right view (kind `right_png`);
- `part_bottom.png` — bottom view (kind `bottom_png`);
- `part.report.json` — machine-readable report (kind `report`).

The manifest also lists per-job sidecars from the state root: `job.json`,
`status.json`, `freecad_console.log`.

`part.report.json` contains `input`, `producer`, `reconstruction`
(`status`/`reason`/`stats`), `validation` (`reimport_ok`, `solids`, `shells`,
`volume`), `rendering` (`status`), `artifacts`, `steps` (timings) and
`timestamps`. See `GENESIS.md` for the intent behind each field.

## Cancellation, timeouts, recovery

- A running job that exceeds `FCADSERVE_JOBS__TIMEOUT_SECONDS` is terminated
  (the pipeline keeps 2/3 of the budget; an insufficient-render-time failure is
  reported distinctly as `render_timeout`).
- Cancel sets a persisted flag and kills the job's setsid process group
  (FreeCAD plus its Xvfb).
- On service restart, queued/running jobs are re-queued (their work dirs are
  regenerated), unless `FCADSERVE_JOBS__REQUEUE_RUNNING_JOBS_ON_STARTUP=false`.
- Terminal jobs beyond `FCADSERVE_RETENTION__MAX_RETAINED_JOBS` are pruned
  every few minutes (DB row + work dir).

## Security

- Paths are validated against allow-listed roots with symlink resolution
  (`PathPolicy`) before any file is touched; derived artifact names cannot
  traverse directories or overwrite the input STL.
- FreeCAD/Yvfb subprocesses are launched via `ArgumentList` (no shell
  interpolation) and parameterized only through the `FCADSERVE_JOB_PARAMS`
  environment variable.
- Job work dirs and X displays are per-job, and process groups are hard-killed
  on stop/cancel/timeout.
- **Mode isolation caveats**: in `flatpak` mode FreeCAD is sandboxed by flatpak
  and manages its own XDG dirs, so `FCADSERVE_FREECAD__ISOLATE_USER_CONFIG`
  does not apply; in `exec` mode the process has no sandbox — it runs as the
  service user with the full host filesystem visible through the path policy.
  Keep `FCADSERVE_ALLOWED_*_ROOTS` as narrow as possible, run the service as a
  dedicated non-root account (see the systemd unit in `deploy/`), and treat
  inputs from other users as untrusted.

## Service installation (systemd)

See `deploy/fcadserve.service` for a unit using a non-root `fcadserve`
account, `Restart=on-failure`, a writable `/var/lib/fcadserve`, `ProtectSystem=
strict` with explicit `ReadWritePaths`, and `NoNewPrivileges=true`. Set the
environment in `/etc/fcadserve/fcadserve.conf`. The unit's shutdown path kills
active worker process groups and their Xvfb displays within `TimeoutStopSec`.

## Testing

- `dotnet build fcadserve.slnx`
- `dotnet test tests/fcadserve.Tests` — pure service tests (path policy, DB,
  store, command construction, config normalization, HTTP API against the
  in-memory/test app). FreeCAD-independent.
- `./launch_server.sh` + the curl examples above for a full two-phase walk
  using real FreeCAD and Xvfb on this machine.

## Project layout

- `src/fcadserve` — the service (Minimal API, job queue/executor, SQLite,
  path policy, FreeCAD runner).
- `freecad/` — FreeCAD-side scripts: `fcadserve.py` (reconstruction),
  `fcadserve_worker.py` (headless pipeline), `fcadserve_render.py` (GUI
  renderer).
- `deploy/` — systemd unit.
- `tests/` — test projects.
- `tasks/` — work items (task01...task09) and their status.
- `GENESIS.md` — original requirements/spec.
