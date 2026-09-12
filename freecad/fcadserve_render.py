#!/usr/bin/env python3
"""fcadserveSTL FreeCAD renderer.

Runs inside a GUI-capable FreeCAD process (org.freecad.FreeCAD) underneath a
per-job virtual display (Xvfb). Re-imports the STEP produced by
``fcadserve_worker.py`` and renders the isometric and cutaway PNGs that cannot
be produced headlessly. Idempotent by design: the FreeCAD GUI may execute the
script more than once, so everything here is guarded by an already-declared
result/artifact check.

Progress and a result summary are written as atomic JSON files into the job
directory. Exits 0 on success/skip and non-zero on failure.
"""

from __future__ import annotations

import json
import os
import sys
import time
from datetime import datetime, timezone


def now_utc():
    return datetime.now(timezone.utc).isoformat()


def atomic_write(path, data_str):
    tmp = path + f".tmp-{os.getpid()}-{int(time.time() * 1000)}"
    with open(tmp, "w", encoding="utf-8") as fh:
        fh.write(data_str)
        fh.flush()
        os.fsync(fh.fileno())
    os.replace(tmp, path)


def log(job_dir, msg):
    line = f"[{now_utc()}] {msg}"
    print(line, flush=True)
    print(line, file=sys.stderr, flush=True)
    try:
        with open(os.path.join(job_dir, "render.log"), "a", encoding="utf-8") as fh:
            fh.write(line + "\n")
    except OSError:
        pass


def set_phase(job_dir, progress_path, phase):
    atomic_write(progress_path, json.dumps({
        "phase": phase,
        "phase_started_at_utc": now_utc(),
        "updated_at_utc": now_utc(),
    }))
    log(job_dir, f"phase={phase}")


def load_shape(step_path, part):
    import FreeCAD as App  # noqa: PLC0415

    doc = App.newDocument("fcadserveRender")
    part.insert(step_path, doc.Name)
    best = None
    best_solids = -1
    for obj in doc.Objects:
        shp = getattr(obj, "Shape", None)
        if shp is None:
            continue
        solids = len(getattr(shp, "Solids", []))
        if solids > best_solids:
            best, best_solids = shp, solids
    if best is None:
        raise RuntimeError("no shape available in STEP file")
    if best_solids > 0:
        return best.Solids[0]
    return best


def render_doc(shape, out_path, width, height):
    import FreeCAD as App  # noqa: PLC0415
    import FreeCADGui as Gui  # noqa: PLC0415

    rdoc = App.newDocument("fcadserveRenderIso")
    rfeat = rdoc.addObject("Part::Feature", "Part")
    rfeat.Shape = shape
    rdoc.recompute()
    App.setActiveDocument(rdoc.Name)
    view = Gui.activeDocument().activeView()
    view.setCameraType("Perspective")
    view.viewIsometric()
    Gui.SendMsgToActiveView("ViewFit")
    view.saveImage(out_path, width, height, "White")
    if not os.path.exists(out_path) or os.path.getsize(out_path) == 0:
        raise RuntimeError("saveImage produced no file")
    return True


def build_cutaway(shape, part):
    import FreeCAD as App  # noqa: PLC0415

    bb = shape.BoundBox
    pad = max(bb.XLength, bb.YLength, bb.ZLength) * 0.1 or 1.0
    cutter = part.makeBox(
        bb.XLength / 2.0,
        bb.YLength + 2 * pad,
        bb.ZLength + 2 * pad,
        App.Vector(bb.XMin, bb.YMin - pad, bb.ZMin - pad),
    )
    result = shape.cut(cutter)
    if getattr(result, "Volume", 0.0) <= 0:
        raise RuntimeError("cutaway boolean produced an empty shape")
    return result


def patch_report(artifacts_dir, target, report_rel):
    try:
        path = os.path.join(artifacts_dir, target["report"])
        with open(path, encoding="utf-8") as fh:
            report = json.load(fh)
    except (OSError, ValueError, KeyError):
        log(os.path.dirname(artifacts_dir), f"could not patch report {path!r}")
        return
    report["rendering"] = {"status": "done"}
    report["artifacts"]["iso_png"] = target["iso_png"]
    report["artifacts"]["section_png"] = target["section_png"] if os.path.exists(
        os.path.join(artifacts_dir, target["section_png"])) else None
    report["timestamps"]["finished_at_utc"] = now_utc()
    atomic_write(path, json.dumps(report, indent=2))


def already_completed(render_result_path, iso_path):
    """True only when a previous run both finished AND succeeded.

    A failed attempt must be allowed to re-run (the FreeCAD GUI may execute
    this script twice, so guard against duplicate work, never against retry).
    """
    if not (os.path.exists(render_result_path) and os.path.exists(iso_path)):
        return False
    try:
        with open(render_result_path, encoding="utf-8") as fh:
            result = json.load(fh)
    except (OSError, ValueError):
        return False
    return bool(result.get("ok"))


def main():
    params_path = os.environ.get("FCADSERVE_JOB_PARAMS", "")
    if not params_path or not os.path.exists(params_path):
        print("no job params file supplied (FCADSERVE_JOB_PARAMS)", flush=True)
        os._exit(2)

    with open(params_path, encoding="utf-8") as fh:
        p = json.load(fh)

    job_dir = p["job_dir"]
    artifacts_dir = p["artifacts_dir"]
    target = p["target_names"]
    render_cfg = p.get("render", {})
    width = int(render_cfg.get("width", 1280))
    height = int(render_cfg.get("height", 960))
    render_result_path = os.path.join(job_dir, "render.result.json")

    if already_completed(render_result_path, os.path.join(artifacts_dir, target["iso_png"])):
        log(job_dir, "render already completed successfully; skipping re-run (GUI double-execution guard)")
        os._exit(0)

    step_path = os.path.join(artifacts_dir, target["step"])
    iso_path = os.path.join(artifacts_dir, target["iso_png"])
    section_path = os.path.join(artifacts_dir, target["section_png"])

    result = {"ok": False, "iso_ok": False, "section_ok": False, "section_enabled": True,
              "error": None, "reason": None}

    try:
        import FreeCAD as App  # noqa: PLC0415
        import Part  # noqa: PLC0415

        set_phase(job_dir, p["progress_path"], "rendering")
        shape = load_shape(step_path, Part)

        set_phase(job_dir, p["progress_path"], "rendering_iso")
        render_doc(shape, iso_path, width, height)
        result["iso_ok"] = True

        set_phase(job_dir, p["progress_path"], "rendering_section")
        try:
            cut = build_cutaway(shape, Part)
            render_doc(cut, section_path, width, height)
            result["section_ok"] = True
        except Exception as exc:
            result["section_ok"] = False
            result["reason"] = str(exc)
            log(job_dir, f"section render skipped: {exc}")

        result["ok"] = True
        set_phase(job_dir, p["progress_path"], "writing_report")
        patch_report(artifacts_dir, target, None)
        result["status"] = "ok"
    except Exception as exc:
        import traceback  # noqa: PLC0415
        result["error"] = traceback.format_exc()
        log(job_dir, f"RENDER FAILED:\n{result['error']}")

    atomic_write(render_result_path, json.dumps(result, indent=2))

    code = 0 if result["ok"] else 1
    try:
        import FreeCAD as App  # noqa: PLC0415
        App.quit()
    except Exception:
        pass
    time.sleep(1.0)
    os._exit(code)


if __name__ == "__main__" or os.environ.get("FCADSERVE_JOB_PARAMS"):
    # The FreeCAD GUI runs the file through its macro loader where __name__ is
    # not "__main__"; the executor always sets the params env, so use that as
    # the authoritative signal to enter the renderer.
    main()