"""Organs you push aside (unit radius, the game scales them) and anatomical targets (real size in meters).

Material names match the game's (src/visual/model_slot.gd): organ, flesh, blood_bag, bone.
"""

from __future__ import annotations

import bpy
import numpy as np
from numpy.typing import NDArray

from . import scene
from .scene import Vec3, capsule, ellipsoid

ORGAN = (0.6, 0.27, 0.25)
LIVER = (0.3, 0.07, 0.06)
TUMOR = (0.72, 0.58, 0.52)
CLOT = (0.2, 0.01, 0.02)
BONE = (0.86, 0.81, 0.68)
MARROW = (0.5, 0.16, 0.1)
VESSEL = (0.38, 0.03, 0.07)


def _wet(name: str, color: Vec3) -> bpy.types.Material:
    return scene.material(name, color, roughness=0.18, subsurface=0.15)


def _vessels(target: bpy.types.Object, paths: list[list[Vec3]], radius: float) -> bpy.types.Object:
    """Thin blood vessels shrinkwrapped onto the target so they lie on its surface, half sunk in."""
    parts = []
    for i, path in enumerate(paths):
        on = scene.on_surface(target, path, radius * 0.4)
        parts.append(scene.curve_tube(f"Vessel{i}", on, radius, list(np.linspace(1.0, 0.45, len(on))), resolution=8))
    vessels = scene.join("Vessels", *parts) if len(parts) > 1 else parts[0]
    return scene.finish(vessels, scene.material("vessel", VESSEL, roughness=0.3))


def _coil(rng: np.random.Generator, steps: int, spacing: float, bounds: NDArray[np.float64]) -> list[Vec3]:
    """Self-avoiding random walk inside an ellipsoid: loops of gut that pile up without passing through each other."""
    points = [np.array([0.0, -bounds[1] * 0.5, 0.0])]
    direction = np.array([1.0, 0.0, 0.0])
    for _ in range(steps):
        p = points[-1]
        push = np.zeros(3)
        for q in points[:-4]:
            d = p - q
            dist = float(np.linalg.norm(d))
            if dist < spacing:
                push += d / max(dist, 1e-4) * (spacing - dist) / spacing
        inside = p / bounds
        pull = -inside * max(0.0, float(np.linalg.norm(inside)) - 0.55) * 2.0
        direction = direction + rng.normal(size=3) * np.array([0.45, 0.2, 0.45]) + push * 1.5 + pull
        direction /= np.linalg.norm(direction)
        points.append(p + direction * 0.06)
    return [(float(p[0]), float(p[1]), float(p[2])) for p in points]


def bowel() -> None:
    """Small intestine: one long separate tube looping loosely over itself, never fused into a clump.
    The walk keeps loops a little more than a tube's width apart; the ends taper shut."""
    path = _coil(np.random.default_rng(8), 190, 0.34, np.array([1.0, 0.42, 1.0]))
    count = len(path)
    ends = np.clip(np.minimum(np.arange(count), np.arange(count)[::-1]) / 3.0, 0.0, 1.0)
    radii = [float(0.35 + 0.65 * np.sqrt(e)) for e in ends]
    gut = scene.curve_tube("Bowel", path, 0.12, radii, resolution=16)
    gut = scene.displace(gut, 0.006, 0.3, seed=2)
    scene.finish(gut, _wet("organ", ORGAN))


def _liver_shape(p: NDArray[np.float64]) -> NDArray[np.float64]:
    """Sculpts the dome into a lobe: thin toward the front edge and the left tip, hollowed underneath."""
    x, y, z = p[:, 0], p[:, 1], p[:, 2]
    front = np.clip((z + 0.1) / 1.0, 0.0, 1.0)
    left = np.clip(x / 1.8, 0.0, 1.0)
    y = y * (1.0 - 0.62 * front**1.5) * (1.0 - 0.4 * left)
    under = y < 0.0
    hollow = 0.22 * np.clip(1.0 - (x / 1.6) ** 2, 0.0, 1.0) * np.clip(1.0 - (z / 1.0) ** 2, 0.0, 1.0)
    y = np.where(under, y * 0.55 + hollow, y)
    return np.column_stack([x, y, z])


def lobe() -> None:
    """Liver lobe: a smooth dome, wedge thin at the front edge, hollow underneath."""
    liver = scene.blobs(
        "Lobe",
        [ellipsoid((-0.4, 0.0, 0.0), (1.1, 0.6, 1.0)), ellipsoid((0.6, 0.0, 0.05), (1.15, 0.5, 0.85)), ellipsoid((1.3, 0.0, 0.2), (0.55, 0.35, 0.5))],
        resolution=0.03,
    )
    liver = scene.remesh(scene.deform(liver, _liver_shape), 0.02, smooth=8)
    liver = scene.displace(liver, 0.006, 0.6, seed=5)
    scene.finish(scene.decimate(liver, 0.3), _wet("organ", LIVER))


def sac() -> None:
    """Stomach: rounded fundus, body curving down and across into the narrowing antrum, oesophagus and duodenum cut open."""
    arc = [(-0.45, 0.2, 0.0), (-0.25, -0.1, 0.04), (0.1, -0.24, 0.06), (0.45, -0.08, 0.04), (0.64, 0.2, 0.0), (0.8, 0.4, -0.02)]
    radii = (0.5, 0.46, 0.38, 0.26, 0.16, 0.13)
    elements = [ellipsoid((-0.5, 0.3, 0.0), (0.48, 0.46, 0.44))]
    elements += [capsule(arc[i], arc[i + 1], radii[i + 1], 2.5) for i in range(len(arc) - 1)]
    elements.append(capsule((-0.14, 0.45, 0.0), (0.0, 1.05, 0.0), 0.11, 3.0))
    stomach = scene.blobs("Sac", elements, resolution=0.03)
    stomach = scene.remesh(stomach, 0.018, smooth=6)
    stomach = scene.cut_below(scene.cut_below(stomach, 1, 0.9, above=True), 0, 0.76, above=True)
    stomach = scene.displace(stomach, 0.008, 0.3, seed=7)
    stomach = scene.finish(scene.decimate(stomach, 0.35), _wet("organ", ORGAN))
    greater = [(-0.6, -0.1, 0.35), (-0.3, -0.45, 0.3), (0.1, -0.55, 0.25), (0.45, -0.35, 0.2), (0.62, -0.05, 0.12)]
    _vessels(stomach, [greater, [(x, y, -z) for x, y, z in greater], [(-0.15, 0.3, 0.3), (0.1, 0.05, 0.3), (0.35, 0.1, 0.22)]], 0.022)


def appendix() -> None:
    """Inflamed appendix: a swollen, curling worm on a stub of the cecum it hangs from."""
    path: list[Vec3] = [
        (-0.03, 0.0, 0.0),
        (-0.012, 0.004, 0.006),
        (0.006, 0.002, 0.012),
        (0.024, -0.002, 0.008),
        (0.036, -0.004, -0.004),
        (0.04, -0.003, -0.016),
    ]
    worm = scene.blobs(
        "Appendix",
        [capsule(path[i], path[i + 1], r, 3.0) for i, r in enumerate((0.0072, 0.0078, 0.0074, 0.0066, 0.0058))]
        + [ellipsoid((-0.036, -0.002, -0.002), (0.014, 0.011, 0.013))],
        resolution=0.0008,
    )
    worm = scene.displace(scene.remesh(worm, 0.0006, smooth=6), 0.00025, 0.004, seed=3)
    worm = scene.finish(scene.decimate(worm, 0.35), _wet("organ", (0.7, 0.26, 0.22)))
    over = [(x, y + 0.01, z) for x, y, z in path[1:]]
    _vessels(worm, [over, [(x, y + 0.004, z + 0.01) for x, y, z in path[:4]]], 0.0008)


def tumor() -> None:
    """Lobulated mass: a cluster of rounded nodules bulging out of a core, veins crawling over it."""
    rng = np.random.default_rng(11)
    elements = [ellipsoid((0.0, 0.0, 0.0), (0.011, 0.009, 0.01))]
    for _ in range(8):
        d = rng.normal(size=3)
        d /= np.linalg.norm(d)
        c = d * np.array([0.014, 0.01, 0.013])
        r = rng.uniform(0.0065, 0.0095)
        elements.append(ellipsoid((float(c[0]), float(c[1]), float(c[2])), (r, r * 0.9, r), 3.0))
    mass = scene.blobs("Tumor", elements, resolution=0.0008)
    mass = scene.displace(scene.remesh(mass, 0.0005, smooth=4), 0.0003, 0.003, detail=2, seed=11)
    mass = scene.finish(scene.decimate(mass, 0.3), scene.material("flesh", TUMOR, roughness=0.3, subsurface=0.2))
    veins = []
    for _ in range(4):
        a = rng.normal(size=3)
        a /= np.linalg.norm(a)
        b = a + rng.normal(size=3) * 0.8
        b /= np.linalg.norm(b)
        arc = [a * (1 - t) + b * t for t in np.linspace(0.0, 1.0, 5)]
        veins.append([(float(v[0] * 0.03), float(v[1] * 0.03), float(v[2] * 0.03)) for v in arc])
    _vessels(mass, veins, 0.0007)


def clot() -> None:
    """Blood clot: a glossy dark jelly lump trailing a stringy tail."""
    lump = scene.blobs(
        "Clot", [ellipsoid((0.0, 0.0, 0.0), (0.011, 0.006, 0.008)), ellipsoid((0.007, 0.001, 0.004), (0.006, 0.005, 0.005), 2.5)], resolution=0.0006
    )
    tail = scene.curve_tube("Tail", [(-0.006, 0.0, 0.0), (-0.016, -0.001, -0.003), (-0.024, -0.002, 0.0), (-0.03, -0.002, 0.003)], 0.0024, [1.0, 0.7, 0.5, 0.3])
    lump = scene.remesh(scene.join("Clot", lump, tail), 0.0004, smooth=4)
    lump = scene.displace(lump, 0.0003, 0.003, detail=2, seed=9)
    scene.finish(scene.decimate(lump, 0.6), scene.material("blood_bag", CLOT, roughness=0.06, subsurface=0.05))


def bone() -> None:
    """Femur: ball head on an angled neck and the trochanter at one end, two condyles at the other, bowed shaft."""
    shaft = scene.curve_tube(
        "Shaft", [(-0.09, 0.0, 0.0), (-0.04, 0.003, 0.0), (0.03, 0.003, 0.0), (0.085, 0.0, 0.0)], 0.011, [1.15, 0.95, 1.0, 1.25], resolution=24
    )
    ends = scene.blobs(
        "Ends",
        [
            ellipsoid((-0.092, 0.0, 0.0), (0.018, 0.016, 0.02), 2.5),
            capsule((-0.092, 0.002, -0.004), (-0.108, 0.012, -0.018), 0.009, 3.0),
            ellipsoid((-0.112, 0.016, -0.024), (0.014, 0.014, 0.014), 3.0),
            ellipsoid((-0.1, -0.004, 0.012), (0.012, 0.012, 0.01), 2.5),
            ellipsoid((0.092, -0.002, 0.012), (0.016, 0.014, 0.013), 2.5),
            ellipsoid((0.092, -0.002, -0.012), (0.016, 0.014, 0.013), 2.5),
            ellipsoid((0.084, 0.004, 0.0), (0.014, 0.012, 0.02), 2.0),
        ],
        resolution=0.0008,
    )
    femur = scene.remesh(scene.join("Bone", shaft, ends), 0.0006, smooth=8)
    femur = scene.displace(femur, 0.0002, 0.01, seed=13)
    scene.finish(scene.decimate(femur, 0.15), scene.material("bone", BONE, roughness=0.55, subsurface=0.05))


def fragment() -> None:
    """A broken-off piece of shaft: jagged at both breaks, hollow, with spongy marrow inside."""
    piece = scene.remesh(scene.curve_tube("Fragment", [(-0.055, 0.0, 0.0), (0.0, 0.001, 0.0), (0.055, 0.0, 0.0)], 0.0115, resolution=32), 0.0005)
    for i, x in enumerate((0.058, -0.062)):
        cutter = scene.blobs(f"Break{i}", [ellipsoid((x, 0.004 * (1 - 2 * i), 0.0), (0.03, 0.03, 0.03), 2.0)], resolution=0.002)
        piece = scene.subtract(piece, scene.displace(scene.remesh(cutter, 0.0012), 0.009, 0.006, detail=2, seed=21 + i))
    piece = scene.subtract(piece, scene.curve_tube("Canal", [(-0.07, 0.0, 0.0), (0.07, 0.0, 0.0)], 0.0056, resolution=32))
    scene.finish(scene.decimate(piece, 0.2), scene.material("bone", BONE, roughness=0.55, subsurface=0.05))
    marrow = scene.remesh(scene.curve_tube("Marrow", [(-0.034, 0.0, 0.0), (0.028, 0.0, 0.0)], 0.0054, resolution=24), 0.0004)
    marrow = scene.displace(marrow, 0.0012, 0.0012, detail=3, seed=4)
    scene.finish(scene.decimate(marrow, 0.3), _wet("marrow", MARROW))


def _rib_curve(z: NDArray[np.float64]) -> NDArray[np.float64]:
    """Ribs bow back toward the spine as they run out to the side."""
    return -1.4 * z * z


def _flatten_rib(p: NDArray[np.float64]) -> NDArray[np.float64]:
    """A rib is a flat bar, not a rod: squash the tube front to back and keep its height."""
    x, y, z = p[:, 0], p[:, 1], p[:, 2]
    center = _rib_curve(np.clip(z, 0.0, None))
    return np.column_stack([x, center + (y - center) * 0.42, z])


# Boxes that snap the rib at the origin into a jagged, spiked break (center, size, rotation in degrees).
RIB_BREAK: list[tuple[Vec3, Vec3, Vec3]] = [
    ((0.006, 0.0, -0.009), (0.016, 0.03, 0.02), (0.0, 30.0, 0.0)),
    ((-0.007, 0.0, -0.006), (0.014, 0.03, 0.02), (0.0, -38.0, 0.0)),
    ((0.0, 0.009, -0.005), (0.03, 0.012, 0.016), (-25.0, 0.0, 0.0)),
    ((0.003, -0.006, 0.0005), (0.006, 0.006, 0.006), (20.0, 35.0, 10.0)),
]


def _rib_bar(name: str, radius: float) -> bpy.types.Object:
    zs = np.linspace(-0.012, 0.078, 10)
    path: list[Vec3] = [(0.0, float(_rib_curve(np.array([max(z, 0.0)]))[0]), float(z)) for z in zs]
    bar = scene.remesh(scene.curve_tube(name, path, radius, resolution=24), 0.0004)
    return scene.remesh(scene.deform(bar, _flatten_rib), 0.0004, smooth=3)


def rib() -> None:
    """One end of a broken rib: a curved flat bar from the jagged break at the origin out along +Z.
    Hard bone outside, spongy red marrow inside that shows at the break."""
    shell = scene.subtract(_rib_bar("Rib", 0.0066), _rib_bar("Hollow", 0.0048))
    shell = scene.fracture(shell, RIB_BREAK)
    shell = scene.displace(shell, 0.0001, 0.004, seed=32)
    scene.finish(scene.decimate(shell, 0.3), scene.material("bone", BONE, roughness=0.55, subsurface=0.05))
    core = scene.fracture(_rib_bar("Marrow", 0.0049), RIB_BREAK)
    core = scene.displace(core, 0.00035, 0.0008, detail=3, seed=33)
    scene.finish(scene.decimate(core, 0.3), _wet("marrow", MARROW))


def splinter() -> None:
    """A sliver of rib that broke off and stuck in the lung: long flat facets where it split,
    a ragged blunt end where it snapped off, a needle point that went in."""
    rng = np.random.default_rng(5)
    # Irregular cross-section, wider than thick, like a strip of cortex.
    angles = np.array([0.0, 0.9, 1.7, 2.9, 3.6, 4.7, 5.5])
    radii = np.array([1.0, 0.75, 0.9, 1.0, 0.7, 0.85, 0.8])
    section = np.column_stack([np.sin(angles) * radii * 0.0021, np.cos(angles) * radii * 0.0036])
    xs = np.array([-0.014, -0.0125, -0.008, -0.002, 0.004, 0.009, 0.0125, 0.0155])
    scale = np.array([0.72, 0.95, 1.0, 0.95, 0.75, 0.48, 0.22, 0.02])
    verts: list[Vec3] = []
    for x, k in zip(xs, scale, strict=True):
        wobble = rng.normal(size=(len(angles), 2)) * 0.00025 * (1.0 if x < -0.013 else 0.3)
        bend = 6.0 * x * x
        for (y, z), (dy, dz) in zip(section * k, wobble, strict=True):
            verts.append((float(x), float(y + dy + bend), float(z + dz)))
    n = len(angles)
    faces: list[list[int]] = []
    for i in range(len(xs) - 1):
        for j in range(n):
            a, b = i * n + j, i * n + (j + 1) % n
            faces.append([a, b, b + n, a + n])
    faces.append(list(range(n))[::-1])
    faces.append([(len(xs) - 1) * n + j for j in range(n)])
    shard = scene.mesh_object("Splinter", verts, faces)
    # Keep the split facets crisp but break up their flat faces with fine grain.
    sub = shard.modifiers.new("Detail", "SUBSURF")
    sub.subdivision_type = "SIMPLE"
    sub.levels = sub.render_levels = 3
    shard = scene.displace(scene.bake(shard), 0.00007, 0.0006, detail=3, seed=41)
    scene.finish(shard, scene.material("bone", BONE, roughness=0.55, subsurface=0.05))
