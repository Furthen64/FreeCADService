#!/usr/bin/env python3
"""fcadserveSTL FreeCAD renderer.

Runs inside a GUI-capable FreeCAD process (org.freecad.FreeCAD) underneath a
per-job virtual display (Xvfb). Re-imports the STEP produced by
``fcadserve_worker.py`` and renders the standard orthographic views that cannot
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
import zlib
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
    try:
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
        # A STEP import may be a compound containing several disconnected
        # solids. Keep the complete, document-independent TopoShape so every
        # component is rendered after the import document is closed.
        return best.copy()
    finally:
        App.closeDocument(doc.Name)


def validate_png(path, expected_width, expected_height):
    """Reject corrupt, wrong-size, and visually blank 8-bit FreeCAD PNGs."""
    with open(path, "rb") as fh:
        data = fh.read()
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise RuntimeError("saveImage did not produce a PNG")

    pos = 8
    width = height = color_type = bit_depth = None
    palette = None
    compressed = bytearray()
    while pos + 12 <= len(data):
        length = int.from_bytes(data[pos:pos + 4], "big")
        chunk_end = pos + 12 + length
        if chunk_end > len(data):
            raise RuntimeError("PNG has a truncated chunk")
        kind = data[pos + 4:pos + 8]
        payload = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            if length != 13:
                raise RuntimeError("PNG has an invalid IHDR")
            width = int.from_bytes(payload[0:4], "big")
            height = int.from_bytes(payload[4:8], "big")
            bit_depth, color_type = payload[8], payload[9]
            if payload[10:] != b"\x00\x00\x00":
                raise RuntimeError("PNG uses unsupported compression, filter, or interlace")
        elif kind == b"PLTE":
            palette = payload
        elif kind == b"IDAT":
            compressed.extend(payload)
        elif kind == b"IEND":
            break
        pos = chunk_end

    if (width, height) != (expected_width, expected_height):
        raise RuntimeError(f"PNG dimensions were {width}x{height}, expected {expected_width}x{expected_height}")
    if bit_depth != 8 or color_type not in (0, 2, 3, 4, 6) or not compressed:
        raise RuntimeError("PNG has an unsupported pixel format or no image data")

    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[color_type]
    stride = width * channels
    expected = height * (stride + 1)
    raw = zlib.decompress(bytes(compressed))
    if len(raw) != expected:
        raise RuntimeError("PNG pixel data has an unexpected length")

    previous = bytearray(stride)
    offset = 0
    has_visible_non_white_pixel = False
    for _ in range(height):
        filter_type = raw[offset]
        offset += 1
        row = bytearray(raw[offset:offset + stride])
        offset += stride
        for index, value in enumerate(row):
            left = row[index - channels] if index >= channels else 0
            up = previous[index]
            up_left = previous[index - channels] if index >= channels else 0
            if filter_type == 1:
                row[index] = (value + left) & 0xff
            elif filter_type == 2:
                row[index] = (value + up) & 0xff
            elif filter_type == 3:
                row[index] = (value + ((left + up) // 2)) & 0xff
            elif filter_type == 4:
                p = left + up - up_left
                pa, pb, pc = abs(p - left), abs(p - up), abs(p - up_left)
                predictor = left if pa <= pb and pa <= pc else up if pb <= pc else up_left
                row[index] = (value + predictor) & 0xff
            elif filter_type != 0:
                raise RuntimeError("PNG uses an unsupported row filter")

        for pixel in range(0, stride, channels):
            if color_type == 0 and row[pixel] != 255:
                has_visible_non_white_pixel = True
            elif color_type == 2 and row[pixel:pixel + 3] != b"\xff\xff\xff":
                has_visible_non_white_pixel = True
            elif color_type == 3:
                palette_index = row[pixel] * 3
                if palette is None or palette_index + 3 > len(palette):
                    raise RuntimeError("PNG palette is missing or invalid")
                if palette[palette_index:palette_index + 3] != b"\xff\xff\xff":
                    has_visible_non_white_pixel = True
            elif color_type == 4 and row[pixel] and row[pixel + 1] != 255:
                has_visible_non_white_pixel = True
            elif color_type == 6 and row[pixel + 3] and row[pixel:pixel + 3] != b"\xff\xff\xff":
                has_visible_non_white_pixel = True
        previous = row

    if not has_visible_non_white_pixel:
        raise RuntimeError("PNG contains no visible non-white model pixels")


def render_doc(shape, out_path, width, height, view="iso"):
    import FreeCAD as App  # noqa: PLC0415
    import FreeCADGui as Gui  # noqa: PLC0415

    rdoc = App.newDocument("fcadserveRenderIso")
    try:
        # Keep each capture isolated: exactly one visible feature belongs to
        # this temporary document, and the document is closed in finally.
        rfeat = rdoc.addObject("Part::Feature", "Part")
        rfeat.Shape = shape
        rfeat.ViewObject.Visibility = True
        rfeat.ViewObject.DisplayMode = "Flat Lines"
        rfeat.ViewObject.ShapeColor = (0.80, 0.82, 0.86)
        rfeat.ViewObject.LineColor = (0.12, 0.12, 0.14)
        rfeat.ViewObject.LineWidth = 1.0
        rfeat.ViewObject.Transparency = 0
        rdoc.recompute()
        App.setActiveDocument(rdoc.Name)
        render_view = Gui.activeDocument().activeView()
        render_view.setAnimationEnabled(False)
        render_view.stopAnimating()
        render_view.setCameraType("Orthographic")
        if view == "section":
            # build_cutaway removes the half with the smaller X coordinates,
            # so its exposed cut face has a -X normal. setViewDirection()
            # points the camera toward the model, so use the direction from
            # the -X side of the cut plane toward the model.
            render_view.setViewDirection((1.0, 0.0, 0.0))
        else:
            standard_views = {
                "iso": render_view.viewIsometric,
                "left": render_view.viewLeft,
                "top": render_view.viewTop,
                "right": render_view.viewRight,
                "bottom": render_view.viewBottom,
            }
            try:
                standard_views[view]()
            except KeyError:
                raise ValueError(f"unknown render view: {view!r}") from None

        # Apply camera changes synchronously before the offscreen grab. The
        # queued ViewFit message could otherwise leave saveImage() capturing
        # the previous view.
        render_view.fitAll()
        render_view.redraw()
        Gui.updateGui()
        render_view.saveImage(out_path, width, height, "White")
        if not os.path.exists(out_path) or os.path.getsize(out_path) == 0:
            raise RuntimeError("saveImage produced no file")
        validate_png(out_path, width, height)
        return True
    finally:
        try:
            App.closeDocument(rdoc.Name)
        except Exception:
            pass


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


def patch_report(artifacts_dir, target, section_reason=None):
    try:
        path = os.path.join(artifacts_dir, target["report"])
        with open(path, encoding="utf-8") as fh:
            report = json.load(fh)
    except (OSError, ValueError, KeyError):
        log(os.path.dirname(artifacts_dir), f"could not patch report {path!r}")
        return
    views = [
        name for name in ("iso", "section", "left", "top", "right", "bottom")
        if os.path.exists(os.path.join(artifacts_dir, target[f"{name}_png"]))
    ]
    report["rendering"] = {"status": "done", "views": views}
    for name in ("iso", "section", "left", "top", "right", "bottom"):
        artifact_key = f"{name}_png"
        report["artifacts"][artifact_key] = target[artifact_key] if name in views else None
    report["artifacts"]["section_unavailable_reason"] = section_reason
    report["timestamps"]["finished_at_utc"] = now_utc()
    atomic_write(path, json.dumps(report, indent=2))


def already_completed(render_result_path, render_paths):
    """True only when a previous run both finished AND succeeded.

    A failed attempt must be allowed to re-run (the FreeCAD GUI may execute
    this script twice, so guard against duplicate work, never against retry).
    """
    if not (os.path.exists(render_result_path) and all(os.path.exists(path) for path in render_paths)):
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

    render_paths = [
        os.path.join(artifacts_dir, target[f"{name}_png"])
        for name in ("iso", "left", "top", "right", "bottom")
    ]
    if already_completed(render_result_path, render_paths):
        log(job_dir, "render already completed successfully; skipping re-run (GUI double-execution guard)")
        os._exit(0)

    step_path = os.path.join(artifacts_dir, target["step"])

    result = {
        "ok": False,
        "iso_ok": False,
        "section_ok": False,
        "section_enabled": True,
        "views_ok": {name: False for name in ("iso", "section", "left", "top", "right", "bottom")},
        "error": None,
        "reason": None,
    }

    try:
        import FreeCAD as App  # noqa: PLC0415
        import Part  # noqa: PLC0415

        set_phase(job_dir, p["progress_path"], "rendering")
        shape = load_shape(step_path, Part)
        render_specs = (
            ("iso", shape),
            ("left", shape),
            ("top", shape),
            ("right", shape),
            ("bottom", shape),
        )
        try:
            render_specs = (("section", build_cutaway(shape, Part)),) + render_specs
        except Exception as exc:
            result["section_enabled"] = False
            result["reason"] = str(exc)
            log(job_dir, f"section render unavailable: {exc}")
        for name, render_shape in render_specs:
            set_phase(job_dir, p["progress_path"], f"rendering_{name}")
            output_path = os.path.join(artifacts_dir, target[f"{name}_png"])
            render_doc(render_shape, output_path, width, height, view=name)
            result["views_ok"][name] = True
            result[f"{name}_ok"] = True

        result["ok"] = True
        set_phase(job_dir, p["progress_path"], "writing_report")
        patch_report(artifacts_dir, target, result["reason"])
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
