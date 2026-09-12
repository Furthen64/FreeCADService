# Task 01 — API body binding contradicts the documented protocol

## Status
DONE

## Verification
`SubmitJobRequestConverter` (Api/ApiModels.cs) accepts both `stl_path`/`output_dir`
and `stlPath`/`outputDir`, rejects ambiguous duplicates. Registered via
`ConfigureHttpJsonOptions`. Proven by `ApiEndpointsTests` (snake_case 202,
camelCase 202) and by `tests/integration.sh`, which submits the GENESIS-style
snake_case body verbatim and gets 202.

## Context
`SubmitJobRequest(string? StlPath, string? OutputDir)` binds with ASP.NET's
default `JsonSerializerOptions.Web` (`camelCase`). The GENESIS spec documents
and curls the snake_case body:

```bash
-d '{"stl_path":"/data/part.stl","output_dir":""}'
```

Verified live: a snake_case body returns `400 missing_stl_path` while the
camelCase form succeeds. This means a GENESIS-compliant client cannot submit.

## Scope
- `src/fcadserve/Api/ApiModels.cs` (`SubmitJobRequest`)
- `src/fcadserve/Api/Endpoints.cs` (POST `/v1/jobs`)

## Requirements
Accept BOTH key spellings (snake_case and camelCase) for `stl_path`/`output_dir`
so the spec example works verbatim, without breaking existing camelCase clients.

Options: a tolerant `JsonConverter`/`[JsonPropertyName]`-style mapping, or a
normalized request DTO taking both spellings and resolving to a canonical one.

## Acceptance criteria
- `curl -X POST .../v1/jobs -d '{"stl_path":"/abs/path.stl","output_dir":""}'`
  returns 202 and queues a job (no `missing_stl_path`).
- camelCase body still works.
- Ambiguous bodies (both spellings set, different values) are rejected with an
  explicit error code rather than silently picking one.