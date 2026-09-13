# Task 09 — Optional STL upload staging endpoint

## Status
DONE

## Verification
`POST /v1/uploads` (Api/Endpoints.cs) streams raw bodies through
`UploadStore` (Api/UploadStore.cs): filename from `?name=` or
Content-Disposition, sanitized to a bare `stem.stl`, stored uniquely under
`FCADSERVE_UPLOAD_ROOT` (default `<StateRoot>/uploads`, always an allowed
input root), streamed with a running SHA-256 against
`Jobs.MaxInputSizeBytes`. Covered by six unit tests in `ApiEndpointsTests`
(store + submit round-trip, header filename, missing/bad-extension 400s,
traversal-name sanitization, 413 over limit) and by `tests/integration.sh`,
which uploads the real box fixture and asserts the returned sha256 matches
the on-disk file before submitting it. Full suite: 75/75 green; integration
PASS end to end (upload → submit → skipped-with-artifacts → 409 → cancel).

## Context
The documented protocol only accepts server-side absolute paths, so a remote
client had no way to get an STL onto the host without out-of-band file
transfer. A staging endpoint closes that gap while keeping the path policy
intact: uploaded files land inside the state root, which is service-owned,
and are admitted as inputs automatically — no operator has to widen
`ALLOWED_INPUT_ROOTS` for them.

## Scope
- New: `src/fcadserve/Api/UploadStore.cs`
- `src/fcadserve/Api/Endpoints.cs` (`POST /v1/uploads`)
- `src/fcadserve/Options/ServiceOptions.cs` (`UploadRoot`)
- `src/fcadserve/Program.cs` (default root, allowed-input wiring, Kestrel body cap off in favor of streamed enforcement)
- Docs: README API + config table, CLIENT_PROMPT.md §3.0
- Tests: `tests/fcadserve.Tests/ApiEndpointsTests.cs`, `tests/integration.sh`

## Requirements
- Accept raw binary bodies; name via query or Content-Disposition.
- Reject non-`.stl`, missing names, empty uploads (400 with explicit codes);
  reject over-limit bodies mid-stream (413 `input_too_large`).
- Never overwrite existing uploads; never escape the upload root.
- Returned `stl_path` must be directly submittable to `/v1/jobs`.

## Acceptance criteria
- Upload → submit round-trip works verbatim (unit + integration).
- sha256 in the response equals the fixture's real hash.
- Traversal-style names are stored inside the root under a safe name.
- Existing camelCase/snake_case submit behavior unchanged.
