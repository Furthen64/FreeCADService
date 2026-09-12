# Task 02 — Worker report always has null `sha256` and `created_at_utc`

## Status
DONE

## Verification
`JobExecutor.WorkerParams`/`WriteParams` (Jobs/JobExecutor.cs) now include
`InputSha256` and `CreatedAtUtc` (serialized snake_case as `input_sha256` and
`created_at_utc`). `tests/integration.sh` asserts the published
`box.report.json` has `input.sha256` matching the `/v1/jobs` value and
`timestamps.created_at_utc` populated; `JobExecutorTests` covers the
params-file plumbing indirectly end to end.

## Context
The published report has `input.sha256: null` and `timestamps.created_at_utc: null`.

The worker reads these via `.get()` in `freecad/fcadserve_worker.py` (lines
~229 and ~265: `self.p.get("input_sha256")`, `self.p.get("created_at_utc")`),
but the C# side never writes them into `params.json`.

`JobExecutor.WriteParams` builds `WorkerParams` (Jobs/JobExecutor.cs:17-26,
214-223) with no sha256/created-at fields. Meanwhile `job.json` metadata
(Jobs/JobExecutor.cs:234-248) DOES include `input_sha256` and `created_at_utc` —
so the info exists in the service but is dropped from the human-facing report.

## Scope
- `src/fcadserve/Jobs/JobExecutor.cs` (`WorkerParams`, `WriteParams`)
- `freecad/fcadserve_worker.py` (`report()`)

## Requirements
Add `InputSha256` and `CreatedAtUtc` to the params file so the report is
populated, or have the worker compute the SHA itself. The service already
computed the SHA at submission (PathPolicy) — reuse it.

Note the params file is serialized snake_case (`JsonNamingPolicy.SnakeCaseLower`),
so add keys named `input_sha256` and `created_at_utc` for consistency.

## Acceptance criteria
- A completed job's `box.report.json` contains a real lowercase hex `sha256`
  matching `/v1/jobs/{id}` `input_sha256` and the DB value.
- `timestamps.created_at_utc` matches the job's created-at.
- No regression: existing fields in the report unchanged.