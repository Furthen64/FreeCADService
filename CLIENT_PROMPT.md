# CLIENT_PROMPT.md — Build a client for fcadserve

Use this document to create a client library, CLI, or script that talks to a
**running instance of the `fcadserve` service** — a FreeCAD STL → STEP
conversion service. The service is assumed to be already deployed and reachable
over plain HTTPS or HTTP at some base URL you are handed (default dev binding is
`http://127.0.0.1:8688`; a real deployment will have its own host/port, possibly
behind a reverse proxy — always use the base URL you are given).

---

## 1. What the service does

Submit an **STL file path that lives on the server** and get back:

- a STEP file (`.stp`) converted from the mesh,
- isometric + cutaway PNG renders of the reconstructed solid,
- a JSON report describing reconstruction/validation.

`stl_path` is an absolute path **on the server's filesystem** which must be
inside an allowed input root. Either you already have an STL on the server
(agree on the path with the operator), or you stage one first via the optional
`POST /v1/uploads` endpoint (§3.0) — its returned `stl_path` is guaranteed to
be submittable. You also need a writable output directory **on the server**
(or omit it to write next to the input).

Two-phase processing happens asynchronously: submit → poll → collect.

---

## 2. Conventions

- All bodies and responses are JSON (`application/json`), except artifact
  downloads and `/healthz`/`/readyz`.
- **No authentication is built in.** If the deployment adds some, your
  credentials/headers will be supplied separately.
- All IDs are opaque strings (`j_…`). Treat them as opaque.
- Path-relative URLs returned by the service (e.g. `status_url`) are relative
  to the service base URL — join them to the base you were given; do not
  re-derive them.
- There is no API versioning beyond the `/v1` path prefix.

---

## 3. Endpoints

### 3.0 `POST /v1/uploads?name=part.stl` — (optional) stage an STL on the server

Raw binary body (the STL bytes); filename from the `?name=` query parameter or
the `Content-Disposition: ...; filename="..."` header. Only `.stl` names are
accepted; the name is sanitized to a bare `stem.stl` and stored uniquely under
the service's upload staging root (never overwritten).

Success — `201 Created`:

```json
{
  "upload_id": "u_1a2b3c4d5e",
  "name": "part.stl",
  "stl_path": "/var/lib/fcadserve/uploads/part.stl",
  "size_bytes": 684,
  "sha256": "7f1782bd...",
  "submit_url": "/v1/jobs"
}
```

- `stl_path` is absolute and inside an allowed input root — pass it verbatim as
  `stl_path` to `POST /v1/jobs`.
- Errors: `400 {code: missing_filename | unsupported_extension | empty_upload}`,
  `413 {code: input_too_large}` when the body exceeds the configured max input
  size (default 50 MiB).

### 3.1 `POST /v1/jobs` — submit a job

Request body (either spelling below is accepted; mixed is fine):

```json
{
  "stl_path": "/server/data/part.stl",   // snake_case
  "output_dir": "/server/data/out"       // optional
}
```

```json
{
  "stlPath": "/server/data/part.stl",    // camelCase equivalent
  "outputDir": "/server/data/out"
}
```

Rules:
- `stl_path` is required and must be **absolute**. `output_dir` is optional;
  when omitted the STEP/PNGs/report are written next to the input STL. Output
  dir (explicit or derived) must also be inside an allowed root (or the input's
  parent when no output roots are configured).
- Providing the same key twice, or both spellings of the same key
  (e.g. both `stl_path` and `stlPath`), is rejected as a malformed body.
- A body that is not a JSON object is rejected.

Success — `202 Accepted`:

```json
{
  "job_id": "j_1a2b3c4d",
  "status": "queued",
  "status_url": "/v1/jobs/j_1a2b3c4d"
}
```

Errors — non-2xx with `{ "code": "...", "error": "..." }`:

| HTTP | `code` | Meaning / when it happens |
|------|--------|---------------------------|
| 400 | `invalid_json` | body missing/not JSON |
| 400 | `missing_stl_path` | `stl_path` absent or empty |
| 400 | `relative_path` | `stl_path` (or non-empty `output_dir`) is not absolute |
| 400 | `unsupported_extension` | input not `.stl` |
| 400 | `missing_file` | input path does not exist on the server |
| 400 | `input_is_directory` | input path is a directory |
| 400 | `input_outside_allowed_root` | input not under a configured input root |
| 400 | `input_too_large` | exceeds `MaxInputSizeBytes` (default 50 MiB) |
| 400 | `output_is_file` | `output_dir` is an existing file |
| 400 | `output_outside_allowed_root` | output dir outside allowed output roots |
| 400 | `invalid_output_dir` | output dir could not be derived |
| 409 | `output_already_exists` | a target artifact (`*.stp`, `*_iso.png`, `*_section.png`, `*.report.json`) already exists in the output dir |
| 409 | `artifact_collides_with_input` | a derived artifact name equals the input path (pathology) |
| 429 | `queue_full` | too many jobs queued; retry later with backoff |

> `409 output_already_exists` is the **deliberate no-overwrite guard**. To
> re-run against the same output dir you must first clear the existing
> artifacts or use a different output dir. There is no client-visible overwrite
> flag on submission.

### 3.2 `GET /v1/jobs/{jobId}` — poll job status

```json
{
  "job_id": "j_1a2b3c4d",
  "status": "skipped",
  "phase": "finished",
  "created_at": "2026-09-12T20:51:11.93+00:00",
  "started_at": "2026-09-12T20:51:11.99+00:00",
  "finished_at": "2026-09-12T20:51:20.60+00:00",
  "updated_at": "2026-09-12T20:51:20.60+00:00",
  "stl_path": "/server/data/part.stl",
  "output_dir": "/server/data/out",
  "effective_output_dir": "/server/data/out",
  "input_size_bytes": 684,
  "input_sha256": "7f1782bd...",
  "exit_code": 0,
  "error_code": null,
  "error": null,
  "report_path": "/server/data/out/part.report.json",
  "artifacts": [
    { "name": "part.stp",       "kind": "step",        "path": "/server/data/out/part.stp",               "size_bytes": 8412 },
    { "name": "part_iso.png",   "kind": "iso_png",     "path": "/server/data/out/part_iso.png",           "size_bytes": 12043 },
    { "name": "part_section.png","kind": "section_png","path": "/server/data/out/part_section.png",       "size_bytes": 10538 },
    { "name": "part.report.json","kind": "report",     "path": "/server/data/out/part.report.json",       "size_bytes": 2394 }
  ],
  "status_url": "/v1/jobs/j_1a2b3c4d"
}
```

- `status` ∈ `queued | running | succeeded | skipped | failed | cancelled`.
- `phase` ∈ `queued | startingfreecad | loadingstl | reconstructing | exportingstep | validatingstep | rendering | writingreport | finished` (informational only; do not gate on it).
- `exit_code` is the raw FreeCAD process exit code (may be null).
- `error_code`/`error` are set only on failure/cancellation.
- `artifacts` may also include sidecars: `status` (worker status.json),
  `console_log` (FreeCAD console), `job_metadata`.
- `report_path` points at the JSON report file **on the server** (see §5).

`404` → `{ "code": "not_found", ... }`.

### 3.3 `GET /v1/jobs/{jobId}/artifacts` — artifact manifest

```json
{
  "job_id": "j_1a2b3c4d",
  "status": "skipped",
  "output_dir": "/server/data/out",
  "files": [
    { "name": "part.stp", "kind": "step", "path": "/server/data/out/part.stp", "size_bytes": 8412 },
    { "name": "part_iso.png", "kind": "iso_png", "path": "/server/data/out/part_iso.png", "size_bytes": 12043 }
  ]
}
```

`kind` is one of `step | iso_png | section_png | report | status |
console_log | job_metadata | service_metadata | artifact` (unknown/derived names
classify as `artifact`).

### 3.4 `GET /v1/jobs/{jobId}/artifacts/{name}` — download artifact

Binary download (server reads the file and streams):

- `200` with `Content-Type`: `image/png`, `application/step`,
  `application/json`, `text/plain`, else `application/octet-stream`.
- `404` → `{ "code": "artifact_not_found" | "artifact_missing", ... }`.

Use `{name}` exactly as listed in the manifest (`part.stp`,
`part_iso.png`, etc.).

### 3.5 `POST /v1/jobs/{jobId}/cancel` — cancel a job

```json
{
  "job_id": "j_1a2b3c4d",
  "status": "cancelled",
  "cancel_requested": true,
  "status_url": "/v1/jobs/j_1a2b3c4d"
}
```

- Cancelling a **queued** job flips it straight to `cancelled`.
- Cancelling a **running** job sets `cancel_requested: true`, kills the worker
  process group, and the job settles to `cancelled` shortly after.
- Cancelling an already-terminal job is a no-op (`cancel_requested` reflects
  whether a cancel was acted on).
- `404` for an unknown job.

### 3.6 `GET /healthz` — liveness

`200` `{ "status": "ok" }`. Does not check dependencies (FreeCAD, Xvfb, DB).

### 3.7 `GET /readyz` — readiness

- `200` `{ "ready": true, "checks": { … } }` when FreeCAD entrypoint, Xvfb,
  worker script, DB, state root and input roots are all usable.
- `503` `{ "ready": false, "checks": { … } }` otherwise (e.g. still warming up,
  input roots misconfigured).

Client guidance: **wait for `ready:true` before submitting the first job** and
retry with backoff on `503`.

---

## 4. Job lifecycle & what counts as "done"

```
submit ─▶ queued ─▶ running ─▶ succeeded | skipped | failed
                  │               └─ cancel ─▶ cancelled
                  └─ cancel (queued) ─▶ cancelled
```

Terminal statuses (once reached the job won't change):
- **`succeeded`** — reconstruction produced a valid analytic solid; STEP,
  renders and report published.
- **`skipped`** — **successful fallback**. The mesher could not build a clean
  analytic solid, so the pipeline published the usable/partial result anyway
  (raw geometry was insufficient for a watertight solid, etc.). Artifacts are
  still published and downloadable. **Treat `skipped` as success**, though the
  `validation` section may report `solids: 0`. See §5.
- **`failed`** — processing stopped; `error_code`/`error` explain why. Common
  codes seen at the job level: `missing_status`, `worker_failed`,
  `reconstruct_error`, `invalid_step_reimport`, `export_step_error`,
  `load_stl_error`, `render_failed`, `missing_render_result`,
  `missing_artifacts`, `job_timeout`, `freecad_start_failed`, `xvfb_failed`,
  `cancelled`/`render_timeout`/`internal_error`.
- **`cancelled`** — explicitly cancelled.

There is **no server-side webhook**; clients must poll `GET /v1/jobs/{id}`.

---

## 5. The report file (JSON, downloaded like any artifact)

`report_path` (or the `report` artifact) is a JSON document:

```json
{
  "schema_version": 1,
  "job_id": "j_1a2b3c4d",
  "producer": { "software": "FreeCAD", "version": ["1","1","3",""], "reconstructor": "fcadserve.py" },
  "input": { "path": "/server/data/part.stl", "stem": "part", "size_bytes": 684,
             "sha256": "7f1782bd..." },
  "output_dir": "",
  "reconstruction": { "status": "skipped", "reason": "...", "stats": {}, "shape": { "volume": 27000.0, "shells": 1, ... } },
  "validation":   { "reimport_ok": true, "solids": 0, "shells": 1, "volume": 27000.0, "error": null },
  "rendering":    { "status": "done" },
  "artifacts":    { "step": "part.stp", "iso_png": "part_iso.png", "section_png": "part_section.png", "section_unavailable_reason": null },
  "steps": [ { "name": "load_stl", "status": "ok", "started_at_utc": "...", "duration_ms": 4 }, ... ],
  "timestamps":  { "created_at_utc": "...", "finished_at_utc": "..." }
}
```

- `input.sha256` == `input_sha256` in the job view == the DB value.
- `reconstruction.status` mirrors the job status (`reconstructed` /
  `skipped`). When `skipped`, `reason` explains the fallback.
- `validation.reimport_ok` tells you the STEP round-tripped.
- `rendering.status` becomes `done` after the render phase.

---

## 6. Recommended client behavior (skeleton)

1. **Base + readiness** — remember your base URL. Loop `GET /readyz` until
   `ready:true` (backoff, e.g. 1s → 2s → 4s, cap ~15s; give up after a timeout
   you choose, e.g. 90s).
2. **Submit** — `POST /v1/jobs` with an absolute server-side `stl_path`
   (+ optional `output_dir`). Expect `202`. Read `job_id` and **use the
   returned `status_url`** for polling.
3. **Handle submission errors**:
   - `409 output_already_exists` → tell the caller to pick another output dir
     or clear the target artifacts first.
   - `429 queue_full` → exponential backoff and retry the submit.
   - other 400s → permanent client/business errors; surface `code` + `error`.
4. **Poll** — `GET /v1/jobs/{job_id}` every 1–3 s until `status` is terminal
   (`succeeded|skipped|failed|cancelled`). Add a server-side timeout only if
   you know the deployment's `TimeoutSeconds` (default 600 s); the service
   fails its own timeouts with `error_code: job_timeout`.
5. **Terminal handling** — `succeeded` and `skipped` are both success; check
   `error_code`/`error` for `failed`. For success, fetch the artifact manifest
   (`GET .../artifacts`), then download what you need by name. `kind == "step"`
   is the deliverable; `iso_png`/`section_png` are the renders; `report` is the
   provenance document.
6. **Cancel** — `POST .../cancel` when shutting down or a caller aborts;
   then poll to a terminal `cancelled` state.
7. **Retries** — treat `5xx`/network failures as transient; never blindly
   resubmit on `409`.

---

## 7. Build-verification checklist

A complete client:
- [ ] polls `/readyz` before submitting
- [ ] can submit using the documented snake_case body verbatim
- [ ] also accepts/emits camelCase without breaking
- [ ] surfaces every documented error `code` distinctly
- [ ] treats `skipped` as success and exposes `validation`/`reason`
- [ ] polls until terminal, uses returned `status_url`
- [ ] lists + downloads artifacts by manifest name
- [ ] handles cancel for queued and running jobs
- [ ] handles `409` (naming collision) as a *user-visible, non-retryable* error
- [ ] demonstrates capturing the report and checking `input.sha256`

A good smoke test against a live instance (curl):

```bash
BASE=http://127.0.0.1:8688
curl -sf $BASE/healthz
curl -sf $BASE/readyz

# after you confirm an STL exists on the host + an allowed output dir:
resp=$(curl -s -X POST $BASE/v1/jobs \
  -H 'content-type: application/json' \
  -d '{"stl_path":"/server/data/part.stl","output_dir":"/server/data/out"}')
job=$(echo "$resp" | jq -r .job_id)
until curl -s $BASE/v1/jobs/$job | jq -e '.status == "succeeded" or .status == "skipped" or .status == "failed"' >/dev/null; do sleep 2; done
curl -s $BASE/v1/jobs/$job | jq
curl -sO $BASE/v1/jobs/$job/artifacts/part.stp
```

---

## 8. Gotchas

- **Server-side paths only.** `stl_path`/`output_dir` refer to the machine
  running fcadserve, not your local machine. To get a file onto that machine,
  use `POST /v1/uploads` (§3.0) and submit the returned `stl_path`.
- **Path understanding is security-relevant.** A `404`-free submit that
  returns `input_outside_allowed_root`/`output_outside_allowed_root` means the
  path is invalid *for that deployment* — ask the operator for the configured
  roots (`/readyz` `checks.input_roots` shows them).
- **No overwrite.** Re-running is expected to fail with `409` until the output
  dir is cleared — by design.
- **`skipped` ≠ error.** Many inputs (especially low-poly or highly tessellated
  meshes) land in the analytic-fallback path; artifacts are still valid output.
- **Artifact names are deterministic** from the input stem
  (`<stem>.stp`, `<stem>_iso.png`, `<stem>_section.png`, `<stem>.report.json`),
  but always read the manifest rather than guessing; `section_png` can be
  absent when a section cannot be produced.
- **Phase is coarse** (`startingfreecad`, `loadingstl`, … all-lowercase) and
  informational; gate on `status`.
- **Statuses are lowercased enum names.** Compare case-insensitively if your
  client normalizes.
- **camelCase vs snake_case:** do not send both spellings of a key in one body.