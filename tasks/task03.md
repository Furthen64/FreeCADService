# Task 03 — Renderer double-execution guard swallows real errors

## Status
DONE

## Verification
`freecad/fcadserve_render.py` now uses `already_completed(...)` which requires
`render.result.json["ok"] == true` AND both iso/section outputs present before
short-circuiting; a `ok:false` result re-runs the render. The C# reader
(`JobExecutor.ReadRenderResult`) already requires `ok:true`, so the two sides
agree.

## Context
`freecad/fcadserve_render.py` main() starts with an idempotency guard:

```python
if os.path.exists(render_result_path) and os.path.exists(iso_path):
    ... skip re-run; os._exit(0)
```

The guard is meant to neutralize the FreeCAD GUI running the script twice. But
the check does NOT inspect `render.result.json`'s `ok` value. If the first
execution partially succeeded — iso written, then a section/report error set
`ok:false` — the second execution sees both files and skips, so a recoverable
failure is never retried. The C# side then fails the job with `render_failed`
when a re-run could have succeeded.

## Scope
- `freecad/fcadserve_render.py` (main, guard at top)

## Requirements
Only treat the run as already-complete when the RESULT IS SUCCESSFUL:
require `render.result.json["ok"] == true` (and iso present). A failed first
attempt must be re-run on double execution.

## Acceptance criteria
- Double execution after a successful render still short-circuits (exit 0).
- Double execution after a `ok:false` result re-runs the render instead of
  skipping, so transient/partial failures recover.