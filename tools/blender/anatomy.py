"""Organs you push aside (unit radius, the game scales them) and anatomical targets (real size in meters).

Material names match the game's (src/Visuals/ModelSlot.cs): organ, flesh, blood_bag, bone.
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
    # Every other walk step is plenty for a smooth curve and keeps the triangle count down.
    path = _coil(np.random.default_rng(8), 190, 0.34, np.array([1.0, 0.42, 1.0]))[::2]
    count = len(path)
    ends = np.clip(np.minimum(np.arange(count), np.arange(count)[::-1]) / 2.0, 0.0, 1.0)
    radii = [float(0.35 + 0.65 * np.sqrt(e)) for e in ends]
    gut = scene.curve_tube("Bowel", path, 0.12, radii, resolution=10)
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


# The bridge of the nose drops this much per meter toward the forehead (face.py's nose runs 0.098 -> 0.118 over 4.4 cm).
NOSE_SLOPE = -0.45


def _bridge_y(x: float) -> float:
    """Height of the nasal ridge at x (+X toward the forehead), zero at the hump."""
    return NOSE_SLOPE * x + 0.0022 * float(np.exp(-(((x - 0.002) / 0.006) ** 2))) - 0.0022


def _tent(name: str, xs: NDArray[np.float64], half: NDArray[np.float64], drop: NDArray[np.float64], thick: float) -> bpy.types.Object:
    """A thin shell with an inverted-V cross section lofted along x: the two plates of the nose meeting in a ridge.
    half: how far each plate reaches to the side, drop: how far its edge hangs below the ridge, per x."""
    rings = []
    for x, h, d in zip(xs, half, drop, strict=True):
        y = _bridge_y(float(x))
        # Outer surface left edge, ridge, right edge, then back along the inner surface.
        rings.append([(-h, y - d), (0.0, y), (h, y - d), (h - thick, y - d), (0.0, y - thick * 1.6), (-h + thick, y - d)])
    n = len(rings[0])
    verts: list[Vec3] = [(float(x), float(py), float(pz)) for x, ring in zip(xs, rings, strict=True) for pz, py in ring]
    faces: list[list[int]] = []
    for i in range(len(rings) - 1):
        for j in range(n):
            a, b = i * n + j, i * n + (j + 1) % n
            faces.append([a, b, b + n, a + n])
    faces.append(list(range(n))[::-1])
    faces.append([(len(rings) - 1) * n + j for j in range(n)])
    shell = scene.mesh_object(name, verts, faces)
    sub = shell.modifiers.new("Smooth", "SUBSURF")
    sub.levels = sub.render_levels = 2
    return scene.bake(shell)


def nasal_hump() -> None:
    """The bony hump a nose job takes down: two thin nasal bones meeting in a ridge along the bridge, a bump in the
    middle, the pale upper cartilage carrying on toward the tip. About 3.5 cm long, the hump's top at the origin.
    +X runs toward the forehead, +Y out of the face, like the patient's face lying up."""
    xs = np.linspace(0.02, -0.011, 14)
    # Wider and deeper at the root between the eyes, narrowing toward the tip.
    bone = _tent("NasalBone", xs, 0.0062 + 0.12 * np.clip(xs, 0.0, None), 0.0065 + 0.08 * np.clip(xs, 0.0, None), 0.0013)
    bone = scene.displace(scene.remesh(bone, 0.00035, smooth=3), 0.00012, 0.003, seed=61)
    scene.finish(bone, scene.material("bone", BONE, roughness=0.55, subsurface=0.05))
    # Upper lateral cartilage: softer, paler and bluish, spreading into the tip.
    tip = np.linspace(-0.0105, -0.025, 8)
    cartilage = _tent("Cartilage", tip, 0.0062 - 0.1 * (tip + 0.0105), 0.0062 - 0.05 * (tip + 0.0105), 0.0011)
    cartilage = scene.remesh(cartilage, 0.00035, smooth=3)
    scene.finish(cartilage, scene.material("cartilage", (0.86, 0.84, 0.82), roughness=0.3, subsurface=0.3))


def _loft(name: str, rings: list[list[Vec3]], smooth: int = 2) -> bpy.types.Object:
    """Closed mesh through rings of points (same count each), capped at both ends, rounded by subdivision."""
    n = len(rings[0])
    verts = [p for ring in rings for p in ring]
    faces: list[list[int]] = []
    for i in range(len(rings) - 1):
        for j in range(n):
            a, b = i * n + j, i * n + (j + 1) % n
            faces.append([a, b, b + n, a + n])
    faces.append(list(range(n))[::-1])
    faces.append([(len(rings) - 1) * n + j for j in range(n)])
    obj = scene.mesh_object(name, verts, faces)
    sub = obj.modifiers.new("Smooth", "SUBSURF")
    sub.levels = sub.render_levels = smooth
    return scene.bake(obj)


def skull_flap() -> None:
    """The piece of skull a craniotomy lifts: a curved plate cut from the dome, about 7 x 6 cm and 6 mm thick,
    its top at the origin, +Y out of the head. Sawn edges all round."""
    radius = 0.09
    thick = 0.0065

    def dome(x: float, z: float) -> float:
        return float(np.sqrt(max(radius * radius - x * x - (z / 0.92) ** 2, 0.0)) - radius)

    rings: list[list[Vec3]] = []
    for x in np.linspace(-0.036, 0.036, 16):
        zs = np.linspace(-0.03, 0.03, 12)
        top = [(float(x), dome(float(x), float(z)), float(z)) for z in zs]
        under = [(float(x), dome(float(x), float(z)) - thick, float(z)) for z in zs[::-1]]
        rings.append(top + under)
    flap = _loft("SkullFlap", rings, smooth=1)
    flap = scene.displace(flap, 0.00025, 0.004, seed=71)
    scene.finish(flap, scene.material("bone", BONE, roughness=0.55, subsurface=0.05))


def sternum() -> None:
    """The breastbone a chest opening saws through: the wide manubrium at the top (+X, toward the head), the long flat
    body, the small xiphoid tip at the bottom, and stubs of rib cartilage along both sides. Top face at y 0."""
    # (x, half width) along the bone; the body narrows a little between the rib notches.
    outline = [
        (0.078, 0.012),
        (0.07, 0.024),
        (0.055, 0.025),
        (0.045, 0.015),
        (0.03, 0.017),
        (0.0, 0.019),
        (-0.03, 0.018),
        (-0.055, 0.013),
        (-0.065, 0.006),
        (-0.082, 0.003),
    ]
    rings: list[list[Vec3]] = []
    for x, w in outline:
        angles = np.linspace(0.0, 2.0 * np.pi, 12, endpoint=False)
        rings.append([(x, float(-0.0045 + np.sin(a) * 0.0045), float(np.cos(a) * w)) for a in angles])
    body = _loft("Sternum", rings)
    body = scene.displace(body, 0.00015, 0.004, seed=73)
    scene.finish(body, scene.material("bone", BONE, roughness=0.55, subsurface=0.05))
    stubs = []
    for x in (0.058, 0.03, 0.005, -0.02, -0.042):
        for side in (1.0, -1.0):
            stubs.append(capsule((x, -0.005, side * 0.014), (x - 0.006, -0.008, side * 0.04), 0.0038, 3.0))
    cartilage = scene.blobs("RibCartilage", stubs, resolution=0.0008)
    cartilage = scene.remesh(cartilage, 0.0006, smooth=3)
    scene.finish(cartilage, scene.material("cartilage", (0.86, 0.84, 0.82), roughness=0.3, subsurface=0.3))


HEART = (0.55, 0.13, 0.11)
LUNG = (0.8, 0.52, 0.52)
KIDNEY = (0.45, 0.14, 0.11)


def heart() -> None:
    """Heart, unit size: the ventricles' cone with the apex toward the feet and the patient's left (-X, +Z),
    the atria on top toward the head, the aorta arching out of it and the pulmonary trunk beside it.
    Coronary vessels in fat run down the grooves."""
    body = scene.blobs(
        "Heart",
        [
            ellipsoid((-0.05, 0.0, 0.0), (0.72, 0.55, 0.6)),
            ellipsoid((-0.45, -0.05, 0.2), (0.42, 0.36, 0.36), 2.5),
            ellipsoid((0.42, 0.08, -0.22), (0.34, 0.32, 0.3), 2.5),
            ellipsoid((0.4, 0.05, 0.25), (0.3, 0.28, 0.28), 2.5),
        ],
        resolution=0.03,
    )

    def cone(p: NDArray[np.float64]) -> NDArray[np.float64]:
        # The ventricles narrow into the apex.
        x, y, z = p[:, 0], p[:, 1], p[:, 2]
        taper = 1.0 - 0.6 * np.clip((0.2 - x) / 1.1, 0.0, 1.0) ** 1.4
        return np.column_stack([x, y * taper, 0.12 + (z - 0.12) * taper])

    body = scene.remesh(scene.deform(body, cone), 0.025, smooth=6)
    body = scene.sculpt(body, [scene.line((0.3, 0.5, 0.0), (-0.75, 0.2, 0.3), 0.07, -0.035)])
    body = scene.displace(body, 0.01, 0.35, seed=41)
    body = scene.finish(scene.decimate(body, 0.35), _wet("organ", HEART))
    aorta = scene.curve_tube("Aorta", [(0.3, 0.2, 0.0), (0.65, 0.42, 0.02), (0.9, 0.42, -0.12), (0.95, 0.25, -0.35)], 0.14, resolution=14)
    trunk = scene.curve_tube("Trunk", [(0.25, 0.25, 0.18), (0.55, 0.42, 0.26), (0.7, 0.4, 0.42)], 0.12, [1.0, 0.95, 0.8], resolution=14)
    scene.finish(scene.remesh(scene.join("Vessels", aorta, trunk), 0.02, smooth=2), _wet("vessel", VESSEL))
    grooves = [[(0.3, 0.45, 0.05), (0.0, 0.52, 0.15), (-0.4, 0.4, 0.3), (-0.75, 0.1, 0.35)], [(0.3, 0.3, -0.35), (-0.1, 0.1, -0.55), (-0.5, -0.1, -0.35)]]
    _vessels(body, grooves, 0.035)


def lung() -> None:
    """Right lung, unit size: a tall rounded wedge along X (apex toward the head at +X), the flat medial face toward +Z
    hollowed where the heart sits, the thin front edge lapping over. Mirror it for the left one."""
    lobes = scene.blobs(
        "Lung",
        [
            ellipsoid((-0.35, 0.0, 0.0), (0.7, 0.48, 0.62)),
            ellipsoid((0.35, 0.04, -0.04), (0.6, 0.38, 0.46), 2.5),
            ellipsoid((0.85, 0.05, -0.08), (0.3, 0.22, 0.24), 2.5),
        ],
        resolution=0.03,
    )

    def shape(p: NDArray[np.float64]) -> NDArray[np.float64]:
        x, y, z = p[:, 0], p[:, 1], p[:, 2]
        medial = np.clip(z / 0.5, 0.0, 1.0)
        z = z - 0.25 * medial**2 * np.clip(1.0 - ((x + 0.2) / 0.7) ** 2, 0.0, 1.0)
        y = y * (1.0 - 0.35 * medial)
        return np.column_stack([x, y, z])

    lobes = scene.remesh(scene.deform(lobes, shape), 0.022, smooth=6)
    # The fissures between the lobes.
    fissures = [scene.line((0.45, 0.5, -0.55), (-0.55, 0.45, 0.35), 0.05, -0.05), scene.line((0.0, 0.5, -0.2), (0.1, 0.45, -0.7), 0.04, -0.04)]
    lobes = scene.sculpt(lobes, fissures)
    lobes = scene.displace(lobes, 0.012, 0.25, detail=3, seed=43)
    scene.finish(scene.decimate(lobes, 0.3), _wet("organ", LUNG))


def kidney() -> None:
    """Kidney, unit size: a bean along X with the dent (hilum) toward +Z where the vessels and the ureter come out,
    sitting in a cushion of fat."""
    bean = scene.blobs("Kidney", [ellipsoid((0.0, 0.0, 0.0), (1.0, 0.42, 0.58))], resolution=0.03)

    def dent(p: NDArray[np.float64]) -> NDArray[np.float64]:
        x, y, z = p[:, 0], p[:, 1], p[:, 2]
        hollow = 0.3 * np.clip(1.0 - (x / 0.45) ** 2, 0.0, 1.0) * np.clip(z / 0.5, 0.0, 1.0)
        return np.column_stack([x, y, z - hollow])

    bean = scene.remesh(scene.deform(bean, dent), 0.02, smooth=6)
    bean = scene.displace(bean, 0.006, 0.4, seed=45)
    scene.finish(scene.decimate(bean, 0.35), _wet("organ", KIDNEY))
    ureter = scene.curve_tube("Ureter", [(0.0, -0.05, 0.3), (-0.2, -0.1, 0.55), (-0.8, -0.15, 0.7)], 0.08, [1.0, 0.8, 0.7], resolution=10)
    scene.finish(ureter, _wet("vessel", (0.85, 0.75, 0.6)))
    vessels = scene.curve_tube("Hilum", [(0.1, 0.05, 0.3), (0.15, 0.1, 0.75)], 0.1, resolution=10)
    scene.finish(vessels, _wet("vessel", VESSEL))


def aorta() -> None:
    """A length of aorta, unit size along X, with branch stubs and a vein running beside it."""
    main = scene.curve_tube("Aorta", [(-1.0, 0.0, 0.0), (-0.3, 0.03, 0.02), (0.4, 0.02, -0.02), (1.0, 0.0, 0.0)], 0.13, resolution=16)
    stubs = [(0.5, 1.0), (0.5, -1.0), (-0.1, 1.0), (-0.1, -1.0), (0.2, 0.0)]
    branches = [
        scene.curve_tube(f"Branch{i}", [(x, 0.05, 0.0), (x - 0.1, 0.1, side * 0.35)], 0.05, [1.0, 0.7], resolution=10) for i, (x, side) in enumerate(stubs)
    ]
    artery = scene.remesh(scene.join("Aorta", main, *branches), 0.018, smooth=2)
    scene.finish(scene.decimate(artery, 0.5), _wet("organ", VESSEL))
    vein = scene.curve_tube("Vein", [(-1.0, -0.02, -0.3), (0.0, 0.0, -0.28), (1.0, -0.02, -0.3)], 0.12, resolution=14)
    scene.finish(vein, _wet("vessel", (0.25, 0.1, 0.25)))
