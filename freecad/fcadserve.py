"""fcadserveSTL reconstruction logic.

This module is intentionally shaped like the pipeline's previous main.py so the
FreeCAD worker (fcadserve_worker.py) can call a single entry point,
``reconstruct_object(doc, name, interactive=False)``, without GUI selection
APIs. It converts the tessellated STL mesh into analytic (trimmed) geometry,
builds a solid, and falls back to the refined source shape when reconstruction
is not trustworthy.
"""

from __future__ import annotations

import FreeCAD as App
import Part

FACE_TYPES = ("Plane", "Cylinder", "Cone", "Sphere", "Torus", "BSpline", "Bezier", "Other")


def _surface_kind(face):
    try:
        surface = face.Surface
        return type(surface).__name__
    except Exception:
        return "Unknown"


def _face_histogram(shape):
    counts = {f: 0 for f in FACE_TYPES}
    for face in shape.Faces:
        kind = _surface_kind(face)
        if kind in counts:
            counts[kind] += 1
        else:
            counts["Other"] += 1
    return counts


def _build_solid(shape, tolerance):
    """Attempt to convert a (mesh-derived) shape into one clean solid."""
    shape = shape.removeSplitter()
    work = shape
    if getattr(work, "isValid", lambda: False)() is False:
        try:
            work.fix()
        except Exception:
            pass

    # Prefer an existing solid shell, otherwise assemble one from faces.
    candidates = []
    if getattr(work, "Solids", None):
        candidates.extend(work.Solids)
    elif getattr(work, "Shells", None):
        try:
            candidates.append(Part.Solid(work.Shells[0]))
        except Exception:
            pass
    try:
        candidates.append(Part.makeSolid(work))
    except Exception:
        pass

    for solid in candidates:
        try:
            solid.removeSplitter()
        except Exception:
            pass
        if not getattr(solid, "IsValid", False):
            continue
        if solid.Volume <= 0:
            continue
        if solid.SurfaceArea <= 0:
            continue
        if len(solid.Shells) < 1:
            continue
        return solid
    return None


def reconstruct_object(doc, name, interactive=False, tolerance=0.1):
    """Reconstruct analytic geometry from mesh feature ``name`` in ``doc``.

    Returns a dict with the shape that should be exported, the reconstruction
    status ("reconstructed" or "skipped"), an optional reason and statistics.
    Never requires a GUI selection; ``interactive`` is accepted for API parity.
    """
    feature = doc.getObject(name)
    if feature is None or not hasattr(feature, "Mesh"):
        raise ValueError("no mesh feature named %r in document" % name)

    raw = feature.Mesh
    mesh_shape = Part.Shape()
    mesh_shape.makeShapeFromMesh(raw.Topology, tolerance)

    stats = {
        "triangles": getattr(raw, "CountFacets", len(raw.Facets))
        if hasattr(raw, "Facets")
        else 0,
        "points": getattr(raw, "CountPoints", len(raw.Points))
        if hasattr(raw, "Points")
        else 0,
    }

    solid = _build_solid(mesh_shape, tolerance)

    if solid is not None:
        stats["volume"] = round(solid.Volume, 12)
        stats["surface_area"] = round(solid.SurfaceArea, 12)
        stats["shells"] = len(solid.Shells)
        stats["faces"] = len(solid.Faces)
        stats["face_histogram"] = _face_histogram(solid)
        return {
            "shape": solid,
            "status": "reconstructed",
            "reason": None,
            "stats": stats,
        }

    # Fall back to the refined source shape: exportable, but not a validated
    # analytic reconstruction. This is an intentional skip, not an error.
    fallback = mesh_shape
    try:
        fallback = mesh_shape.removeSplitter()
    except Exception:
        pass
    stats["volume"] = round(getattr(fallback, "Volume", 0.0), 12)
    stats["surface_area"] = round(getattr(fallback, "Area", 0.0), 12)
    stats["faces"] = len(fallback.Faces)
    stats["face_histogram"] = _face_histogram(fallback)
    stats["face_count"] = len(fallback.Faces)
    return {
        "shape": fallback,
        "status": "skipped",
        "reason": "reconstruction_failed_fallback_shape: analytic solid could not be built from the mesh.",
        "stats": stats,
    }


def describe_solid(shape):
    return {
        "valid": bool(getattr(shape, "IsValid", False)),
        "volume": round(getattr(shape, "Volume", 0.0), 12),
        "surface_area": round(getattr(shape, "Area", 0.0), 12),
        "shells": len(getattr(shape, "Shells", [])),
        "solids": len(getattr(shape, "Solids", [])),
        "faces": len(getattr(shape, "Faces", [])),
    }