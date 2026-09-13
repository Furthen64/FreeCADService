#!/usr/bin/env python3
"""fcadserveSTL FreeCAD worker.

Runs inside a headless FreeCAD command process (FreeCADCmd). Performs the
pipeline that needs no display:

  load STL -> reconstruct (or fall back) -> export STEP
  -> re-import validate -> write report

Rendering of the six standard PNG views happens in a separate, GUI-capable
process owned by ``fcadserve_render.py`` (a real display/X output is required
for that step). Job parameters travel exclusively via the FCADSERVE_JOB_PARAMS
environment variable. Progress and final status are written as atomic JSON
files into the job directory. Exits 0 on success/skip and non-zero on failure.
"""

from __future__ import annotations

import json
import os
import sys
import time
import traceback
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


class Worker:
    def __init__(self, params):
        self.p = params
        self.job_dir = params["job_dir"]
        self.artifacts_dir = params["artifacts_dir"]
        self.steps = []
        self.phase = None
        self.log_path = os.path.join(self.job_dir, "worker.log")
        self._error_code = "worker_failed"
        self._error_message = "unknown failure"

    # -- helpers ---------------------------------------------------------

    def log(self, msg):
        line = f"[{now_utc()}] {msg}"
        print(line, flush=True)
        print(line, file=sys.stderr, flush=True)
        try:
            with open(self.log_path, "a", encoding="utf-8") as fh:
                fh.write(line + "\n")
        except OSError:
            pass

    def set_phase(self, phase):
        self.phase = phase
        progress = {
            "phase": phase,
            "phase_started_at_utc": now_utc(),
            "updated_at_utc": now_utc(),
        }
        atomic_write(self.p["progress_path"], json.dumps(progress))
        self.log(f"phase={phase}")

    def step(self, name):
        self.step_fn = name
        return {"name": name, "started_at_utc": now_utc(), "timed": time.monotonic()}

    def end_step(self, entry, ok, detail=None):
        entry["status"] = "ok" if ok else "failed"
        entry["duration_ms"] = int((time.monotonic() - entry["timed"]) * 1000)
        if detail is not None:
            entry["detail"] = detail
        del entry["timed"]
        self.steps.append(entry)
        return ok

    def artifact_path(self, name):
        return os.path.join(self.artifacts_dir, name)

    # -- pipeline ---------------------------------------------------------

    def run(self):
        import FreeCAD as App
        import Part
        import Mesh

        target = self.p["target_names"]
        stem = self.p["stem"]

        # 1. load the STL as a mesh --------------------------------------
        self.set_phase("loading_stl")
        e = self.step("load_stl")
        try:
            doc = App.newDocument("fcadserve")
            feat = doc.addObject("Mesh::Feature", "Mesh")
            feat.Mesh = Mesh.read(self.p["stl_path"])
            doc.recompute()
            self.log(f"loaded mesh: {getattr(feat.Mesh, 'CountPoints', '?')} points")
        except Exception as exc:
            # fail_node raises; the top-level handler in main() finalizes.
            self.fail_node("load_stl_error", e, f"failed to load STL: {exc}")
        self.end_step(e, True)

        # 2. reconstruct analytic geometry -------------------------------
        self.set_phase("reconstructing")
        e = self.step("reconstruct")
        recon = None
        try:
            import fcadserve
            recon = fcadserve.reconstruct_object(doc, feat.Name, interactive=False)
            self.log(f"reconstruction status={recon['status']} reason={recon.get('reason')}")
        except Exception as exc:
            self.fail_node("reconstruct_error", e, f"reconstruction crashed: {exc}")
        self.end_step(e, True)
        shape = recon["shape"]

        # 3. export STEP ----------------------------------------------------
        self.set_phase("exporting_step")
        step_path = self.artifact_path(target["step"])
        e = self.step("export_step")
        try:
            shape.exportStep(step_path)
            size = os.path.getsize(step_path)
            if size == 0:
                raise RuntimeError("exported STEP is empty")
        except Exception as exc:
            self.fail_node("export_step_error", e, f"STEP export failed: {exc}")
        self.end_step(e, True, f"{size} bytes")

        # 4. re-import the STEP and validate --------------------------------
        self.set_phase("validating_step")
        e = self.step("validate_step")
        validation = self.validate_step(step_path, recon["status"], App, Part)
        if not validation["ok"]:
            self.fail_node("invalid_step_reimport", e, validation["error"])
        self.end_step(e, True, f"solids={validation['solids']} shells={validation['shells']}")

        # 5. report -----------------------------------------------------------
        self.set_phase("writing_report")
        report_path = self.report(recon, validation)

        self.finalize_success(recon, validation, report_path)

    # -- steps ---------------------------------------------------------------

    def validate_step(self, step_path, recon_status, App, Part):
        if recon_status == "reconstructed":
            return self.validate_single_solid(step_path, Part)
        return self.validate_any_valid(step_path, Part)

    def validate_single_solid(self, step_path, Part):
        try:
            import FreeCAD as App  # noqa: PLC0415
            doc = App.newDocument("Reimport")
            Part.insert(step_path, doc.Name)
            item = self._best_shape_item(doc)
            shp = item.Shape
            solids = len(getattr(shp, "Solids", []))
            valid = getattr(shp, "Solids", None) is not None and solids == 1
            return {
                "ok": valid,
                "solids": solids,
                "shells": len(getattr(shp, "Shells", [])),
                "volume": round(getattr(getattr(shp, "Solids", [None])[0] if solids else shp, "Volume", 0.0), 12),
                "error": None if valid else f"expected exactly one valid solid, got {solids} solid(s)",
            }
        except Exception as exc:
            return {"ok": False, "solids": -1, "shells": -1, "volume": None, "error": f"re-import failed: {exc}"}

    def validate_any_valid(self, step_path, Part):
        try:
            import FreeCAD as App  # noqa: PLC0415
            doc = App.newDocument("Reimport")
            Part.insert(step_path, doc.Name)
            item = self._best_shape_item(doc)
            shp = item.Shape
            solids = len(getattr(shp, "Solids", []))
            shells = len(getattr(shp, "Shells", []))
            valid = (solids + shells) > 0
            return {
                "ok": valid,
                "solids": solids,
                "shells": shells,
                "volume": round(getattr(getattr(shp, "Solids", [None])[0] if solids else shp, "Volume", 0.0), 12),
                "error": None if valid else "re-imported shape is empty or invalid",
            }
        except Exception as exc:
            return {"ok": False, "solids": -1, "shells": -1, "volume": None, "error": f"re-import failed: {exc}"}

    @staticmethod
    def _best_shape_item(doc):
        # Part.insert can yield objects whose Shape is a Shell (not a Solid) —
        # pick the first object that exposes the most usable solids, else the
        # last inserted object.
        best = None
        best_solids = -1
        for obj in doc.Objects:
            shp = getattr(obj, "Shape", None)
            if shp is None:
                continue
            solids = len(getattr(shp, "Solids", []))
            if solids > best_solids:
                best, best_solids = obj, solids
        return best if best is not None else doc.Objects[-1]

    def report(self, recon, validation):
        import FreeCAD as App

        input_info = {
            "path": self.p["stl_path"],
            "stem": self.p["stem"],
            "size_bytes": os.path.getsize(self.p["stl_path"]),
            "sha256": self.p.get("input_sha256"),
        }
        report = {
            "schema_version": 1,
            "job_id": self.p["job_id"],
            "producer": {
                "software": "FreeCAD",
                "version": [str(v) for v in App.Version()],
                "reconstructor": "fcadserve.py",
            },
            "input": input_info,
            "output_dir": "",  # relative to artifacts dir below
            "reconstruction": {
                "status": recon["status"],
                "reason": recon.get("reason"),
                "stats": recon.get("stats", {}),
                "shape": self._shape_info(recon["shape"]),
            },
            "validation": {
                "reimport_ok": validation["ok"],
                "solids": validation.get("solids"),
                "shells": validation.get("shells"),
                "volume": validation.get("volume"),
                "error": validation.get("error"),
            },
            "rendering": {
                "status": "pending",
                "views": ["iso", "section", "left", "top", "right", "bottom"],
            },
            "artifacts": {
                "step": self.p["target_names"]["step"],
                "iso_png": None,
                "section_png": None,
                "left_png": None,
                "top_png": None,
                "right_png": None,
                "bottom_png": None,
            },
            "steps": self.steps,
            "timestamps": {
                "created_at_utc": self.p.get("created_at_utc"),
                "finished_at_utc": now_utc(),
            },
        }
        report_path = self.artifact_path(self.p["target_names"]["report"])
        atomic_write(report_path, json.dumps(report, indent=2))
        self.log(f"wrote report: {report_path}")
        return report_path

    @staticmethod
    def _shape_info(shape):
        import FreeCAD as App  # noqa: PLC0415

        return {
            "volume": round(getattr(shape, "Volume", 0.0), 12),
            "surface_area": round(getattr(shape, "Area", 0.0), 12),
            "shells": len(getattr(shape, "Shells", [])),
            "faces": len(getattr(shape, "Faces", [])),
        }

    # -- finalization --------------------------------------------------------

    def fail_node(self, error_code, entry, message):
        self.log(f"FAIL error_code={error_code}: {message}")
        if entry is not None:
            entry["status"] = "failed"
            entry["detail"] = message
            try:
                entry["duration_ms"] = int((time.monotonic() - entry["timed"]) * 1000)
                del entry["timed"]
            except KeyError:
                pass
            self.steps.append(entry)
        self._error_code = error_code
        self._error_message = message
        raise RuntimeError(message)

    def finalize_failed(self):
        status = {
            "status": "failed",
            "error_code": getattr(self, "_error_code", "worker_failed"),
            "error": self._error_message,
            "steps": self.steps,
        }
        atomic_write(self.p["status_path"], json.dumps(status, indent=2))
        self.shutdown(1)

    def finalize_success(self, recon, validation, report_path):
        status = {
            "status": recon["status"],
            "reason": recon.get("reason"),
            "report_path": report_path,
            "artifacts": {
                "step": self.p["target_names"]["step"],
                "report": self.p["target_names"]["report"],
            },
            "validation": validation,
            "steps": self.steps,
            "put_reconstruction": recon.get("status"),
        }
        atomic_write(self.p["status_path"], json.dumps(status, indent=2))
        self.shutdown(0)

    # -- process exit --------------------------------------------------------

    def shutdown(self, code):
        # In the headless FreeCADCmd, the script runs to completion and this
        # merely guarantees a deterministic exit code even if FreeCAD tries to
        # keep busy loops alive.
        try:
            import FreeCAD as App  # noqa: PLC0415
            try:
                App.closeAllDocuments()
            except Exception:
                pass
        except Exception:
            pass
        time.sleep(0.5)
        os._exit(code)


def main():
    # Under both the FreeCAD GUI and FreeCADCmd the executed script itself is
    # argv[1]; job parameters therefore travel exclusively via the environment.
    params_path = os.environ.get("FCADSERVE_JOB_PARAMS", "")
    if not params_path or not os.path.exists(params_path):
        print("no job params file supplied (FCADSERVE_JOB_PARAMS)", flush=True)
        os._exit(2)

    with open(params_path, "r", encoding="utf-8") as fh:
        params = json.load(fh)

    # FreeCAD executes startup scripts more than once; a rerun is a no-op so
    # the pipeline is not performed twice.
    done_marker = os.path.join(params["job_dir"], "worker.done.marker")
    if os.path.exists(done_marker) and os.path.exists(params["status_path"]):
        print("pipeline already completed; skipping rerun", flush=True)
        os._exit(0)
    with open(done_marker, "w", encoding="utf-8") as fh:
        fh.write(now_utc() + "\n")

    os.makedirs(params["artifacts_dir"], exist_ok=True)

    worker = Worker(params)
    try:
        worker.run()
    except SystemExit:
        raise
    except Exception:
        worker._error_code = getattr(worker, "_error_code", "worker_failed")
        worker._error_message = traceback.format_exc()
        worker.log("UNHANDLED EXCEPTION:\n" + worker._error_message)
        worker.finalize_failed()


if __name__ == "__main__" or os.environ.get("FCADSERVE_JOB_PARAMS"):
    # FreeCADCmd/GUI run the file through their macro loader where
    # __name__ is not "__main__"; the executor always sets the params env, so
    # use that as the authoritative signal to enter the worker.
    main()
