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
import math
import os
import sys
import time
import zlib
from datetime import datetime, timezone


MAX_EDGE_LABELS = 24
CLUSTER_MIN_EDGES = 3


def _norm(v):
    return math.sqrt(sum(x * x for x in v))


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _orthonormal_view(direction, up):
    """Return ``(direction, up)`` as unit vectors with ``up`` perpendicular to
    ``direction``.

    FreeCAD re-orthogonalizes a non-perpendicular screen-up against the view
    direction; doing it here keeps every stored camera basis self-consistent so
    edge-label projection and report metadata match what is actually rendered.
    The world-Z component of ``up`` is preserved where possible so views that
    should read as "Z vertical" do.
    """
    d = [float(x) for x in direction]
    dn = _norm(d)
    if dn == 0.0:
        raise ValueError("view direction must be non-zero")
    d = [x / dn for x in d]
    u = [float(x) for x in up]
    dot = sum(a * b for a, b in zip(u, d))
    u = [a - dot * b for a, b in zip(u, d)]
    un = _norm(u)
    if un < 1e-9:
        # ``up`` was parallel to the view direction; pick any perpendicular axis.
        ref = (0.0, 1.0, 0.0) if abs(d[1]) < 0.9 else (1.0, 0.0, 0.0)
        u = _cross(d, ref)
        un = _norm(u)
    return tuple(d), tuple(a / un for a in u)


# Intended camera basis per view: look direction points from the camera toward
# the model; up establishes the vertical screen axis. Each pair is orthonormalized
# so the stored vectors are guaranteed consistent (see _orthonormal_view).
_VIEW_CAMERA_INTENTS = {
    "iso": ((1.0, -1.0, -1.0), (0.0, 0.0, 1.0)),
    "section": ((1.0, 0.0, 0.0), (0.0, 0.0, 1.0)),
    "left": ((1.0, 0.0, 0.0), (0.0, 0.0, 1.0)),
    "top": ((0.0, 0.0, -1.0), (0.0, 1.0, 0.0)),
    "right": ((-1.0, 0.0, 0.0), (0.0, 0.0, 1.0)),
    "bottom": ((0.0, 0.0, 1.0), (0.0, 1.0, 0.0)),
}

VIEW_CAMERAS = {name: _orthonormal_view(d, u) for name, (d, u) in _VIEW_CAMERA_INTENTS.items()}

# FreeCAD's canonical standard-view presets. Orientation is delegated to these
# built-ins (rather than hand-computed vectors) so a preview cannot be rotated
# by an incorrect camera basis; VIEW_CAMERAS supplies the matching reference
# basis used only for edge-label projection and report metadata.
STANDARD_VIEW_METHODS = {
    "iso": "viewIsometric",
    # The cut is normal to X; this has the same screen basis as the verified
    # left cardinal view (Y horizontal, Z vertical).
    "section": "viewLeft",
    "left": "viewLeft",
    "top": "viewTop",
    "right": "viewRight",
    "bottom": "viewBottom",
}


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
            elif color_type == 4 and row[pixel + 1] > 0 and row[pixel] != 255:
                has_visible_non_white_pixel = True
            elif color_type == 6 and row[pixel + 3] and row[pixel:pixel + 3] != b"\xff\xff\xff":
                has_visible_non_white_pixel = True
        previous = row

    if not has_visible_non_white_pixel:
        raise RuntimeError("PNG contains no visible non-white model pixels")


def _edge_label_plan(labels, screen_right, screen_up, shape_diagonal):
    """Return direct labels and grouped callouts in screen coordinates.

    STL imports commonly approximate a cylindrical or filleted face with a fan
    of equal straight edges.  Annotating each member is both redundant and
    unreadable.  Equal-length edges that are close in the current projection
    are therefore represented by one callout (``length mm x N edges``).
    """
    if not labels:
        return [], []

    # Work in projected model units, not pixels: labels are added before
    # ``fitAll`` and Coin overlays deliberately do not affect the fitted box.
    proximity = max(shape_diagonal * 0.075, 1e-4)
    length_tolerance = max(shape_diagonal * 1e-5, 1e-4)
    pending = []
    for length, midpoint in labels:
        pending.append({
            "length": length,
            "midpoint": midpoint,
            "x": midpoint.dot(screen_right),
            "y": midpoint.dot(screen_up),
        })

    # Connected components make a curved run one balloon even when its
    # individual facet midpoints form a chain rather than a tight point cloud.
    groups = []
    while pending:
        group = [pending.pop()]
        changed = True
        while changed:
            changed = False
            for candidate in pending[:]:
                if any(
                    abs(candidate["length"] - member["length"]) <= length_tolerance
                    and math.hypot(candidate["x"] - member["x"], candidate["y"] - member["y"]) <= proximity
                    for member in group
                ):
                    pending.remove(candidate)
                    group.append(candidate)
                    changed = True
        groups.append(group)

    direct, callouts = [], []
    for group in groups:
        if len(group) < CLUSTER_MIN_EDGES:
            direct.extend(group)
            continue
        count = len(group)
        callouts.append({
            "length": sum(item["length"] for item in group) / count,
            "count": count,
            "midpoint": sum((item["midpoint"] for item in group), group[0]["midpoint"] * 0) / count,
            "x": sum(item["x"] for item in group) / count,
            "y": sum(item["y"] for item in group) / count,
        })
    return direct, callouts


def _callout_positions(callouts, shape, screen_right, screen_up):
    """Place grouped labels just outside their edge cluster without overlap."""
    if not callouts:
        return
    bb = shape.BoundBox
    center = (bb.Center.dot(screen_right), bb.Center.dot(screen_up))
    diagonal = max(math.hypot(bb.XLength, bb.YLength, bb.ZLength), 1.0)
    # Text is sized in pixels while this offset is in model units.  Keep a
    # generous gap so the whole callout stays outside the solid after fitAll.
    offset = diagonal * 0.20
    spacing = diagonal * 0.10
    used = []
    for callout in sorted(callouts, key=lambda item: (item["y"], item["x"])):
        dx, dy = callout["x"] - center[0], callout["y"] - center[1]
        distance = math.hypot(dx, dy)
        if distance < 1e-6:
            dx, dy, distance = 0.0, 1.0, 1.0
        dx, dy = dx / distance, dy / distance
        x, y = callout["x"] + dx * offset, callout["y"] + dy * offset
        # Nudge successive balloons along their tangent if they would collide.
        while any(math.hypot(x - ux, y - uy) < spacing for ux, uy in used):
            x += -dy * spacing
            y += dx * spacing
        used.append((x, y))
        callout["label_point"] = callout["midpoint"] + screen_right * (x - callout["x"]) + screen_up * (y - callout["y"])


def _edge_midpoint_is_visible(shape, midpoint, camera_direction, part):
    """Whether a ray from the camera can reach an edge midpoint unobstructed."""
    diagonal = max(math.hypot(shape.BoundBox.XLength, shape.BoundBox.YLength, shape.BoundBox.ZLength), 1.0)
    epsilon = diagonal * 1e-5
    # ``camera_direction`` points from camera to model.  Stop just short of
    # the midpoint so an exposed edge does not intersect its own face.
    ray = part.makeLine(
        midpoint - camera_direction * (diagonal * 2.0),
        midpoint - camera_direction * epsilon,
    )
    try:
        # ``common`` only reports an overlap with a solid volume.  Imported
        # STEP shapes may be shells, so test the ray against their surfaces
        # instead; the ray stops short of an exposed edge's own surface.
        intersections = shape.section(ray)
        return not getattr(intersections, "Vertexes", []) and not getattr(intersections, "Edges", [])
    except Exception:
        # Do not make a preview lose all dimensions if an unusual imported
        # shape cannot perform this inexpensive boolean query.
        return True


def _add_callout_background(root, callout, screen_right, screen_up, shape_diagonal, coin):
    """Add an opaque, screen-facing backing plate before the callout text."""
    label = f"{callout['length']:.2f} mm x{callout['count']}"
    # SoText2 is pixel sized, whereas the plate is in model units.  These
    # conservative proportions leave padding around the 16px monospace text
    # across the normal fitted preview sizes.
    half_width = shape_diagonal * (0.009 * len(label) + 0.025)
    half_height = shape_diagonal * 0.026
    center = callout["label_point"] + screen_up * (half_height * 0.20)
    points = [
        center - screen_right * half_width - screen_up * half_height,
        center + screen_right * half_width - screen_up * half_height,
        center + screen_right * half_width + screen_up * half_height,
        center - screen_right * half_width + screen_up * half_height,
    ]
    background = coin.SoSeparator()
    light_model = coin.SoLightModel()
    light_model.model = coin.SoLightModel.BASE_COLOR
    background.addChild(light_model)
    white = coin.SoBaseColor()
    white.rgb = (1.0, 1.0, 1.0)
    background.addChild(white)
    coordinates = coin.SoCoordinate3()
    coordinates.point.setValues(0, 4, points)
    background.addChild(coordinates)
    face = coin.SoFaceSet()
    face.numVertices = 4
    background.addChild(face)
    root.addChild(background)


def add_edge_length_labels(render_view, shape, direction, up):
    """Add direct labels and aggregated callouts for useful straight edges.

    Labels are Coin scene-graph overlays, rather than Draft dimensions, so they
    neither require a workbench nor enlarge the model's fitted bounding box.
    Curved and tessellation edges are deliberately omitted: their arc length is
    rarely a useful manufacturing dimension and they make previews illegible.
    """
    import FreeCAD as App  # noqa: PLC0415
    import Part  # noqa: PLC0415
    from pivy import coin  # noqa: PLC0415

    labels = []
    seen = set()
    camera_direction = App.Vector(*direction)
    screen_up = App.Vector(*up)
    screen_right = camera_direction.cross(screen_up)
    for edge in shape.Edges:
        if len(edge.Vertexes) != 2 or "Line" not in type(edge.Curve).__name__:
            continue
        start, end = (vertex.Point for vertex in edge.Vertexes)
        key = tuple(sorted((
            (round(start.x, 6), round(start.y, 6), round(start.z, 6)),
            (round(end.x, 6), round(end.y, 6), round(end.z, 6)),
        )))
        if key in seen or edge.Length <= 1e-6:
            continue
        seen.add(key)
        projected_length = max(
            abs((end - start).dot(screen_right)),
            abs((end - start).dot(screen_up)),
        )
        # An edge parallel to the camera ray is invisible (a point or an
        # overlapping line) in the PNG, so annotating it is misleading.
        if projected_length <= edge.Length * 1e-4:
            continue
        if not _edge_midpoint_is_visible(shape, (start + end) * 0.5, camera_direction, Part):
            continue
        labels.append((edge.Length, (start + end) * 0.5))

    # Prefer the longest edges when there are many, since tiny edges create
    # unreadable clusters and contribute little useful dimensional context.
    labels.sort(key=lambda item: item[0], reverse=True)
    labels = labels[:MAX_EDGE_LABELS]
    if not labels:
        return

    shape_diagonal = math.hypot(shape.BoundBox.XLength, shape.BoundBox.YLength, shape.BoundBox.ZLength)
    direct_labels, callouts = _edge_label_plan(labels, screen_right, screen_up, shape_diagonal)
    _callout_positions(callouts, shape, screen_right, screen_up)

    root = coin.SoSeparator()
    color = coin.SoBaseColor()
    color.rgb = (0.10, 0.10, 0.10)
    root.addChild(color)
    # An annotation must remain legible even when its screen-facing callout
    # crosses the projected silhouette of the part.
    depth_buffer = coin.SoDepthBuffer()
    depth_buffer.test = False
    depth_buffer.write = False
    root.addChild(depth_buffer)
    font = coin.SoFont()
    font.size = 16
    root.addChild(font)
    for label in direct_labels:
        item = coin.SoSeparator()
        translation = coin.SoTranslation()
        translation.translation = label["midpoint"]
        text = coin.SoText2()
        text.string = f"{label['length']:.2f} mm"
        text.justification = coin.SoText2.CENTER
        item.addChild(translation)
        item.addChild(text)
        root.addChild(item)

    for callout in callouts:
        # A short leader makes it clear which dense edge run the balloon
        # summarizes.  This is a screen-facing world-space line, so it follows
        # the model through the orthographic capture without changing fitAll.
        leader = coin.SoSeparator()
        style = coin.SoDrawStyle()
        style.lineWidth = 1.5
        leader.addChild(style)
        coordinates = coin.SoCoordinate3()
        coordinates.point.setValues(0, 2, [callout["midpoint"], callout["label_point"]])
        leader.addChild(coordinates)
        line = coin.SoLineSet()
        line.numVertices = 2
        leader.addChild(line)
        root.addChild(leader)

        _add_callout_background(root, callout, screen_right, screen_up, shape_diagonal, coin)

        item = coin.SoSeparator()
        translation = coin.SoTranslation()
        translation.translation = callout["label_point"]
        text = coin.SoText2()
        # Keep the balloon narrow; its leader supplies the association and
        # ``xN`` clearly means that N equal-length edges were summarized.
        text.string = f"{callout['length']:.2f} mm x{callout['count']}"
        text.justification = coin.SoText2.CENTER
        item.addChild(translation)
        item.addChild(text)
        root.addChild(item)
    render_view.getSceneGraph().addChild(root)


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
        if view not in VIEW_CAMERAS:
            raise ValueError(f"unknown render view: {view!r}")
        direction, up = VIEW_CAMERAS[view]
        # FreeCAD 1.1's Python API has no up-vector setter. Delegate all
        # orientation to the canonical presets instead of using a custom
        # direction that FreeCAD may roll differently between versions.
        getattr(render_view, STANDARD_VIEW_METHODS[view])()
        Gui.updateGui()
        add_edge_length_labels(render_view, shape, direction, up)

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


def patch_report(artifacts_dir, target, section_reason=None, section_plane=None):
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
    view_metadata = {
        name: {
            "camera_direction": list(VIEW_CAMERAS[name][0]),
            "up_vector": list(VIEW_CAMERAS[name][1]),
        }
        for name in views
    }
    if section_plane is not None and "section" in view_metadata:
        view_metadata["section"]["plane"] = section_plane
    report["rendering"] = {
        "status": "done",
        "views": views,
        "view_metadata": view_metadata,
    }
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
        section_plane = None
        try:
            bb = shape.BoundBox
            section_plane = {
                "normal": [1.0, 0.0, 0.0],
                "offset": bb.XMin + bb.XLength / 2.0,
            }
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
        patch_report(artifacts_dir, target, result["reason"], section_plane)
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
