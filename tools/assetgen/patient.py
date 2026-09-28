"""The patient, lying face up along +X (head at +X), origin on the body's center line.

Joints are separate parts pivoted where they bend, so the game can animate breathing, flinching,
seizures, panic and talking. +Z is the patient's left side.
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import trimesh
from numpy.typing import NDArray

from .geometry import Model, ellipsoid, merge, moved, rotated, superellipsoid, tube


def _arm(model: Model, side: float, suffix: str) -> None:
    z = 0.25 * side
    shoulder = (0.4, 0.0, 0.215 * side)
    elbow = (0.13, -0.025, z)
    wrist = (-0.14, -0.04, 0.258 * side)
    upper = tube(
        [(0.44, 0.0, 0.19 * side), (0.38, 0.0, 0.232 * side), (0.25, -0.01, 0.245 * side), elbow],
        [(0.056, 0.05), (0.05, 0.05), (0.047, 0.046), (0.041, 0.04)],
    )
    fore = tube(
        [elbow, (0.05, -0.03, 0.252 * side), (-0.08, -0.035, 0.256 * side), wrist],
        [(0.041, 0.043), (0.042, 0.045), (0.031, 0.036), (0.022, 0.03)],
    )
    palm = superellipsoid((0.1, 0.028, 0.075), 0.35, (-0.19, -0.046, 0.26 * side))
    fingers = superellipsoid((0.085, 0.02, 0.07), 0.5, (-0.265, -0.05, 0.258 * side))
    thumb = rotated(ellipsoid((0.035, 0.011, 0.012), (-0.18, -0.04, (0.26 + 0.045) * side)), 0.5 * side, (0, 1, 0), (-0.16, -0.04, 0.26 * side))
    model.add(f"UpperArm{suffix}", upper, "skin", shoulder)
    model.add(f"Forearm{suffix}", fore, "skin", elbow, f"UpperArm{suffix}")
    model.add(f"Hand{suffix}", merge(palm, fingers, thumb), "skin", wrist, f"Forearm{suffix}")


def _leg(model: Model, side: float, suffix: str) -> None:
    hip = (-0.36, -0.015, 0.1 * side)
    knee = (-0.8, -0.03, 0.105 * side)
    ankle = (-1.2, -0.05, 0.11 * side)
    thigh = tube(
        [(-0.3, -0.012, 0.1 * side), (-0.45, -0.02, 0.1 * side), (-0.62, -0.025, 0.103 * side), knee],
        [(0.092, 0.086), (0.085, 0.08), (0.07, 0.067), (0.052, 0.055)],
    )
    shin = tube(
        [knee, (-0.9, -0.032, 0.107 * side), (-1.05, -0.04, 0.109 * side), ankle],
        [(0.052, 0.055), (0.055, 0.052), (0.042, 0.04), (0.03, 0.032)],
    )
    foot = merge(
        superellipsoid((0.075, 0.17, 0.085), 0.45, (-1.235, 0.01, 0.112 * side)),
        superellipsoid((0.08, 0.05, 0.075), 0.5, (-1.24, -0.075, 0.11 * side)),
    )
    model.add(f"Thigh{suffix}", thigh, "skin", hip)
    model.add(f"Shin{suffix}", shin, "skin", knee, f"Thigh{suffix}")
    model.add(f"Foot{suffix}", rotated(foot, 0.12 * side, (1, 0, 0), ankle), "skin", ankle, f"Shin{suffix}")


def build() -> list[Model]:
    model = Model("patient", "body")
    torso = tube(
        [
            (-0.44, 0.0, 0.0),
            (-0.36, 0.0, 0.0),
            (-0.2, 0.0, 0.0),
            (-0.05, 0.0, 0.0),
            (0.1, 0.0, 0.0),
            (0.25, 0.0, 0.0),
            (0.38, 0.0, 0.0),
            (0.45, 0.0, 0.0),
            (0.5, 0.0, 0.0),
        ],
        [(0.07, 0.15), (0.1, 0.18), (0.1, 0.168), (0.106, 0.152), (0.108, 0.165), (0.11, 0.185), (0.105, 0.2), (0.085, 0.17), (0.05, 0.09)],
        ring=40,
        exponent=2.6,
    )
    model.add("Torso", torso, "skin", (0.0, -0.11, 0.0))
    gown = tube(
        [(-0.47, 0.0, 0.0), (-0.36, 0.0, 0.0), (-0.2, 0.0, 0.0), (-0.1, 0.0, 0.0)],
        [(0.08, 0.16), (0.107, 0.19), (0.107, 0.178), (0.109, 0.162)],
        ring=40,
        exponent=2.6,
    )
    model.add("Gown", gown, "gown", (0.0, -0.11, 0.0), "Torso")
    model.add("Neck", tube([(0.44, 0.0, 0.0), (0.52, 0.005, 0.0), (0.6, 0.012, 0.0)], [(0.052, 0.058), (0.05, 0.054), (0.05, 0.052)]), "skin", (0.47, 0.0, 0.0))

    head_pivot = (0.58, 0.01, 0.0)
    skull = merge(
        ellipsoid((0.1, 0.092, 0.08), (0.68, 0.02, 0.0), 4),
        ellipsoid((0.02, 0.012, 0.06), (0.705, 0.095, 0.0)),
        rotated(ellipsoid((0.024, 0.018, 0.011), (0.662, 0.118, 0.0)), -0.3, (0, 0, 1), (0.662, 0.118, 0.0)),
        ellipsoid((0.026, 0.03, 0.008), (0.672, 0.015, 0.082)),
        ellipsoid((0.026, 0.03, 0.008), (0.672, 0.015, -0.082)),
    )
    model.add("Head", skull, "skin", head_pivot, "Neck")
    model.add("Hair", ellipsoid((0.098, 0.088, 0.085), (0.71, -0.008, 0.0), 4), "hair", head_pivot, "Head")
    model.add("Lips", ellipsoid((0.008, 0.006, 0.022), (0.628, 0.1, 0.0)), "lips", head_pivot, "Head")
    model.add("Jaw", ellipsoid((0.04, 0.03, 0.05), (0.604, 0.07, 0.0)), "skin", (0.64, 0.035, 0.0), "Head")
    eyes = merge(ellipsoid((0.011, 0.011, 0.011), (0.69, 0.086, 0.03)), ellipsoid((0.011, 0.011, 0.011), (0.69, 0.086, -0.03)))
    irises = merge(ellipsoid((0.005, 0.004, 0.005), (0.69, 0.096, 0.03)), ellipsoid((0.005, 0.004, 0.005), (0.69, 0.096, -0.03)))
    lids = merge(ellipsoid((0.014, 0.013, 0.016), (0.69, 0.088, 0.03)), ellipsoid((0.014, 0.013, 0.016), (0.69, 0.088, -0.03)))
    model.add("Eyes", eyes, "eye", head_pivot, "Head")
    model.add("Irises", irises, "iris", head_pivot, "Head")
    model.add("Lids", lids, "skin", head_pivot, "Head")
    for side, suffix in ((1.0, "L"), (-1.0, "R")):
        _arm(model, side, suffix)
        _leg(model, side, suffix)
    bake_site_heights(model)
    return [model]


SITES_FILE = Path(__file__).resolve().parents[2] / "data" / "patient_sites.json"
HEIGHTS_FILE = Path(__file__).resolve().parents[2] / "assets" / "models" / "patient" / "site_heights.json"
GRID = 33


def _world_mesh(model: Model) -> trimesh.Trimesh:
    """Every skin part in model space, at rest pose."""
    meshes = [moved(p.mesh, model.world_pivot(p.name)) for p in model.parts if p.material in ("skin", "gown")]
    return merge(*meshes)


def _ray_heights(mesh: trimesh.Trimesh, origins: NDArray[np.float64], direction: NDArray[np.float64]) -> NDArray[np.float64]:
    """Distance from each origin to the first triangle hit along direction (Moller-Trumbore), inf on a miss."""
    tris = mesh.triangles
    v0, e1, e2 = tris[:, 0], tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0]
    p = np.cross(direction, e2)
    det = np.einsum("ij,ij->i", e1, p)
    valid = np.abs(det) > 1e-12
    inv = np.where(valid, 1.0 / np.where(valid, det, 1.0), 0.0)
    out = np.full(len(origins), np.inf)
    for i, origin in enumerate(origins):
        s = origin - v0
        u = np.einsum("ij,ij->i", s, p) * inv
        q = np.cross(s, e1)
        v = (q @ direction) * inv
        t = np.einsum("ij,ij->i", e2, q) * inv
        hit = valid & (u >= 0) & (v >= 0) & (u + v <= 1) & (t > 0)
        if hit.any():
            out[i] = t[hit].min()
    return out


def bake_site_heights(model: Model) -> None:
    """Skin height above/below each flat site plane, so the operable patch hugs the body."""
    sites = {k: v for k, v in json.loads(SITES_FILE.read_text()).items() if not k.startswith("_")}
    mesh = _world_mesh(model)
    result: dict[str, object] = {"grid": GRID}
    for name, site in sites.items():
        normal = np.array([0.0, -1.0 if site.get("back") else 1.0, 0.0])
        pos = np.array(site["pos"], float)
        w, h = site["size"]
        lin = np.linspace(-0.5, 0.5, GRID)
        grid = np.array([[pos[0] + x * w, 0.0, pos[2] + z * h] for z in lin for x in lin])
        start = pos[1] + normal[1] * 0.15
        grid[:, 1] = start
        dist = _ray_heights(mesh, grid, -normal)
        heights = 0.15 - dist
        heights[~np.isfinite(heights)] = -0.06
        result[name] = [round(float(v), 4) for v in np.clip(heights, -0.06, 0.03)]
    HEIGHTS_FILE.parent.mkdir(parents=True, exist_ok=True)
    HEIGHTS_FILE.write_text(json.dumps(result))
