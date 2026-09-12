# Task 06 — Automated test coverage per GENESIS

## Status
DONE

## Verification
69 tests, all passing via `dotnet test` on a machine WITH FreeCAD present but the
suite has no FreeCAD dependency (quarantined to `tests/integration.sh` which
skips when Xvfb/flatpak/FreeCAD are absent). Coverage map:
- request validation/path policy: `PathPolicyTests`, `PathUtilsTests`, `ApiEndpointsTests`
- empty vs explicit output dir: `PathPolicyTests`
- queueing / status transitions: `JobStoreTests`; queue-full 429: API test
- restart recovery: `JobStoreTests.RecoverOrphansAsync`
- cancellation + timeout: `JobStoreTests`, API cancel test, `JobExecutorTests`
  (timeout, render-timeout, start-failure)
- FreeCAD command construction (no shell injection): `CommandBuilderTests`
- successful / intentional-skip reconstruction, invalid STEP re-import
  (worker `status.json failed` → error-code mapping), missing-section-with-iso,
  render failure: `JobExecutorTests`
- artifact manifest correctness: `JobStoreTests` + executor artifact asserts
- API behavior while running: `ApiEndpointsTests` polling test
- config-key normalization: `ConfigKeyTests`
The real-STL two-phase pipeline runs via `tests/integration.sh` (snake_case submit,
sha256 + created_at, artifacts, collision 409, cancel, precedence).

## Context
`tests/fcadserve.Tests/SmokeTest.cs` contains a single empty placeholder
`[Fact]` (`Test1`), so there is effectively no coverage. GENESIS.md:215-235
requires tests for:

- request validation and path policy;
- empty vs explicit output directory behavior;
- queueing and status transitions;
- restart recovery of queued/running jobs;
- cancellation and timeout handling;
- FreeCAD subprocess command construction (no shell injection);
- successful reconstruction;
- intentional skipped reconstruction;
- invalid STEP re-import;
- missing section but valid isometric image;
- FreeCAD crash and render timeout;
- artifact manifest correctness;
- API behavior while a job is running.

FreeCAD-dependent tests must stay separate from pure service tests; include a
small real-STL integration command usable when FreeCAD + Xvfb are installed.

## Scope
- `tests/fcadserve.Tests/` (xunit already referenced)
- Keep FreeCAD-dependent tests in a separate class/file/namespace
  (`.../Integration/`) so CI can run pure tests without FreeCAD.

## Requirements
1. Pure service tests (no FreeCAD): `PathPolicy` (roots, symlinks, collisions,
   extension/size rules, empty vs explicit output dir), `JobStore` state
   transitions + recovery, `JobDatabase` CRUD, `FreeCadCommandBuilder` argument
   construction for both `headless` modes, `NormalizeConfigKey`.
2. HTTP tests via `WebApplicationFactory` against a temp state root:
   submit → queued, cancel, 404s, queue-full (429), collision (409).
3. Integration tests/command gated behind an env var or skipped-if-missing
   check (e.g. `FCADSERVE_FREECAD__MODE` + flatpak available), exercising a
   workflow driver that runs one STL through the whole two-phase pipeline.
4. Mirror a `smoke_run.sh`-style driver so a dev can run the real-STL pass.

## Acceptance criteria
- `dotnet test` (pure suite) passes on a machine without FreeCAD.
- Each GENESIS category maps to at least one test with a clear name.
- Integration path is documented in `tasks/` progress or README, and runs the
  existing `work/box.stl` e2e to `skipped/succeeded` correctly.