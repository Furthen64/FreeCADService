# Genesis prompt: SteppifySTL as a Linux service

You are implementing a production-oriented Linux service around the existing
SteppifySTL FreeCAD pipeline. Do not treat this as an interactive FreeCAD
macro. The service must accept API requests, run jobs reliably, and expose
machine-readable status and artifact paths.

## Objective

Build a local HTTP service that accepts:

1. an absolute path to an input STL file;
2. an optional output directory.

If the output directory is empty or omitted, use the parent directory of the
input STL. The service must run the complete pipeline:

- load the STL in FreeCAD;
- reconstruct analytic geometry using the repository's existing reconstruction
  logic;
- safely fall back to the refined source shape when reconstruction is skipped;
- export a STEP artifact;
- validate the exported STEP by re-importing it;
- render a standard isometric PNG;
- render a cutaway/section PNG when possible;
- write a JSON report and service-level job metadata.

The service must not require a human to open FreeCAD or select an object.

## Existing code to reuse

Study the repository before changing it. The important existing components are:

- `main.py`: reconstruction logic and `reconstruct_object(...,
  interactive=False)`;
- `tools/freecad_batch.py`: STL loading, STEP export, STEP re-import validation,
  and report generation;
- `tools/freecad_screenshot_startup/InitGui.py`: FreeCAD GUI rendering logic;
- `tools/render_fixture_screenshots.py`: current Xvfb/FreeCAD screenshot
  orchestration;
- `tools/summarize_batch_report.py`: report conventions.

Preserve the useful geometry behavior and report fields. Refactor shared logic
instead of duplicating large sections of `main.py` or creating a second,
diverging reconstruction implementation.

## Recommended architecture

Use a small Python HTTP API process plus a persistent SQLite job database and a
separate worker process for each FreeCAD job.

The API process must never execute FreeCAD geometry in its request handler.
Long-running work belongs in a queue/worker so HTTP requests remain responsive.
Start with one worker and make concurrency configurable; FreeCAD jobs should
not share a document or GUI process by default.

Each job should run in an isolated subprocess. A FreeCAD GUI process under
Xvfb is the simplest reliable route because `ActiveView.saveImage()` requires a
GUI view. The worker should perform reconstruction, STEP export, validation,
and screenshot capture in the same FreeCAD job process where practical. Do not
make STEP re-import or screenshot generation depend on the API process.

If a FreeCAD process crashes, times out, or leaves an invalid result, mark only
that job as failed and keep the service alive.

## HTTP API

Implement at least these endpoints:

### `POST /v1/jobs`

Request JSON:

```json
{
  "stl_path": "/absolute/path/to/part.stl",
  "output_dir": "/optional/output/directory"
}
```

`stl_path` is required and must be absolute. `output_dir` may be omitted or an
empty string. Return HTTP `202` with a stable job identifier:

```json
{
  "job_id": "...",
  "status": "queued",
  "status_url": "/v1/jobs/..."
}
```

### `GET /v1/jobs/{job_id}`

Return status, timestamps, input/output paths, current phase, exit code, error
information, and artifact paths when available. Suggested statuses are:

`queued`, `running`, `succeeded`, `skipped`, `failed`, and `cancelled`.

Suggested phases are:

`queued`, `starting_freecad`, `loading_stl`, `reconstructing`, `exporting_step`,
`validating_step`, `rendering`, `writing_report`, and `finished`.

### `GET /v1/jobs/{job_id}/artifacts`

Return the artifact manifest. Optionally support downloading individual files,
but do not expose arbitrary filesystem reads—only files belonging to that job.

### `POST /v1/jobs/{job_id}/cancel`

Cancel queued jobs immediately. For running jobs, terminate the worker
subprocess, clean up temporary files, and mark the job cancelled.

### `GET /healthz` and `GET /readyz`

`healthz` confirms the service process is alive. `readyz` verifies that the
configured FreeCAD executable, Xvfb capability, database, and output policy are
usable.

## Output contract

The effective output directory is:

- `output_dir` when supplied;
- otherwise the input STL's parent directory.

Create it when permitted. Never overwrite the input STL. Avoid accidental
collisions: either reject an existing job output or use deterministic
stem-based names with a unique job manifest. Document the policy clearly.

Every successful or intentionally skipped job should produce a manifest with
paths for:

- generated STEP;
- isometric PNG;
- section PNG, or an explicit unavailable reason;
- reconstruction report JSON;
- FreeCAD stdout/stderr logs;
- service job metadata.

Use atomic writes where possible: render and write into a temporary job
directory, close all files, validate required artifacts, then publish the
manifest and final status. A failed job must not be reported as successful just
because a partial PNG exists.

## FreeCAD worker behavior

The worker must:

1. create a fresh FreeCAD document;
2. load the requested STL as a mesh;
3. call the existing reconstruction code without GUI selection APIs;
4. export the selected result to STEP;
5. re-import the STEP and enforce the existing valid-single-solid checks when
   the status is `reconstructed`;
6. open the resulting STEP/shape in a GUI-capable FreeCAD document;
7. save an orthographic axonometric image using `ActiveView.saveImage()`;
8. create the existing half-length bounding-box cutaway and save the section
   image when the boolean succeeds;
9. write the report and exit with a meaningful code.

Use a virtual display. Prefer a per-job X display or a dedicated isolated Xvfb
process so jobs cannot render into each other's windows. Set a per-job FreeCAD
user/configuration directory when supported by the Flatpak setup, and ensure
temporary startup modules are removed even after failure.

Do not use `os._exit()` before status, logs, and document cleanup are complete
unless it is the only reliable way to stop FreeCAD after rendering. If it is
necessary, make the status file write atomic and test the behavior.

## Security and filesystem policy

The API accepts filesystem paths, so treat path handling as a security boundary.

Add configurable allowlists such as:

- `STEPPIFY_ALLOWED_INPUT_ROOTS`;
- `STEPPIFY_ALLOWED_OUTPUT_ROOTS`.

Reject relative paths, missing files, directories, unsupported extensions,
symlink escapes, and output paths outside the configured roots. Resolve paths
before validation. Do not pass request values through a shell command string.
Use argument arrays for subprocesses.

The service is intended for a trusted local client, but it must still enforce
timeouts, maximum input size, queue limits, and a maximum number of retained
jobs. Do not expose an unauthenticated arbitrary-file-processing service on all
network interfaces by default; bind to localhost unless explicitly configured.

## Configuration and deployment

Provide configuration through environment variables or a small config file for:

- bind address and port;
- FreeCAD Flatpak application ID or executable command;
- Xvfb executable;
- allowed input/output roots;
- job timeout;
- queue size and worker count;
- artifact retention;
- log level.

Add a systemd unit suitable for a normal non-root service account. Include:

- `Restart=on-failure`;
- a writable state directory for SQLite and logs;
- explicit permissions for configured STL/output roots;
- a shutdown timeout that terminates active FreeCAD workers cleanly;
- reasonable hardening such as `NoNewPrivileges=true`, with filesystem
  protections documented where they interact with Flatpak.

Document installation, starting, stopping, health checks, example API calls,
and required FreeCAD/Flatpak/Xvfb setup.

## Testing requirements

Add tests for:

- request validation and path policy;
- empty versus explicit output directory behavior;
- queueing and status transitions;
- restart recovery of queued/running jobs;
- cancellation and timeout handling;
- FreeCAD subprocess command construction without shell injection;
- successful reconstruction;
- intentional skipped reconstruction;
- invalid STEP re-import;
- missing section image with a valid isometric image;
- FreeCAD crash and screenshot timeout;
- artifact manifest correctness;
- API behavior while a job is running.

Keep FreeCAD-dependent tests separate from pure service tests. Provide a small
integration command that runs one real STL through the complete service when
FreeCAD and Xvfb are installed.

## Completion criteria

The implementation is complete when a clean Linux machine with the documented
dependencies can run the systemd service and a client can submit:

```bash
curl -X POST http://127.0.0.1:PORT/v1/jobs \
  -H 'content-type: application/json' \
  -d '{"stl_path":"/data/part.stl","output_dir":""}'
```

The client must be able to poll the returned job URL and receive a final
manifest containing the STEP, PNGs, report, and logs. A reconstruction that is
intentionally skipped must be distinguishable from an infrastructure failure,
and one failed job must not bring down the service or corrupt another job's
artifacts.
