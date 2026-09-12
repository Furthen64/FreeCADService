# Task 08 — Minor correctness & hygiene cleanups

## Status
DONE (items 5 left as-is with documented assumption)

## Verification
1. `status_url` consistent: `SubmitResponse(jobId, basePath)` honors
   `Request.PathBase` like `JobToDto` (Endpoints.cs passes `ctx.Request.PathBase`).
2. `SectionName` removed; config binds at the ROOT (documented in README +
   ServiceOptions comment).
3. `RenderOptions` flattened to `(Width, Height)` (1280x960); dead booleans gone.
4. Dead `return self.finalize_failed()` branches removed from worker
   (`fail_node` raises).
5. Left unchanged: `PruneTerminalAsync` LIMIT/long.MaxValue noted as a documented
   assumption (500 default; low value).
6. `JobDatabase` WAL + `DefaultTimeout=30` via `PRAGMA journal_mode=WAL` in
   `InitializeAsync` (`JournalMode` property not supported by
   Microsoft.Data.Sqlite).
7. `launch_server.sh` gained an `FCADSERVE_NO_CLEAN` guard; paths remain fixed
   but overridable via the same env vars.

## Context
Assorted low-priority findings from the review. Each is self-contained.

1. **`status_url` basePath inconsistency**
   `ApiModels.SubmitResponse` (Api/ApiModels.cs:11) emits `/v1/jobs/{id}`
   without the `Request.PathBase` prefix, while `JobToDto` (Api/ApiModels.cs:45)
   honors it. Behind a reverse proxy the queued response points at the wrong
   base. Make both consistent.

2. **`ServiceOptions.SectionName` unused / misleading**
   `SectionName` (Options/ServiceOptions.cs:10) is `"fcadserve"` but config
   binds at the ROOT (`Get<ServiceOptions>()`, Program.cs:46). A
   `fcadserve.json` file that nests a `"fcadserve": {...}` section silently
   won't bind. Either bind to the section or drop the constant / document that
   keys are root-level.

3. **`RenderOptions.Iso`/`Section` booleans are dead config**
   Jobs/JobExecutor.cs:30 always constructs `new RenderOptions(true, true, ...)`
   and the renderer `freecad/fcadserve_render.py` ignores them (it always
   attempts the section). Remove the booleans, keep width/height, and have the
   renderer bail non-fatally if iso/section were requested off.

4. **Unreachable code in worker**
   `freecad/fcadserve_worker.py` lines ~113-114, ~140-141, ~150-151:
   `self.fail_node(...)` always raises, so the subsequent
   `return self.finalize_failed()` is dead. Remove the dead returns or convert
   `fail_node` to return a sentinel and let callers unify the finalize path.

5. **Retention query shape**
   `PruneTerminalAsync` (Jobs/JobDatabase.cs:250-288) selects ALL terminal rows
   then takes `Skip(maxRetained)`. Fine at the 500 default, but the
   `LIMIT $keep` with `long.MaxValue` is misleading; either `ORDER BY`
   + real `LIMIT maxRetained OFFSET` offset computation or keep it simple with a
   documented assumption. Low value; do only if touching the file anyway.

6. **DB: no WAL / busy timeout**
   Single-process semaphore serialization is fine now, but adding
   `JournalMode = Wal` + `DefaultTimeout` to the connection string builder
   (Jobs/JobDatabase.cs:67-73) future-proofs concurrent readers and avoids
   `database is locked` if a second process ever reads the DB.

7. **`launch_server.sh` destructiveness**
   It `rm -rf`s `work/e2e_state`/`work/e2e_out` on every start. Add a warning
   and/or make the paths overridable so a dev never nukes an unrelated dir.

## Requirements
Pick up the items that make sense now (1, 2, 3 are quick and touch API surface);
4 & 7 are mechanical; 5 & 6 optional. Do NOT touch behavior covered by
task01-07 here (e.g. API spelling is handled in task01).

## Acceptance criteria
- Consistent `status_url` across responses; config-key documentation matches
  actual binding.
- No dead render config; worker has no unreachable fatal branches.
- Guard-rail in launch script (or documented override).