"""Every grabbable tool. Grip at the origin, working tip at (0, 0, -length) with length from data/tools.cfg.

Moving parts are separate nodes the game animates (src/tools/tool_animator.gd):
JawA/JawB open and close, Plunger slides, Trigger squeezes, Blade oscillates, Flame/Glow/Light show while in use.
Level is liquid the game stretches along Z from its node origin by how full the tool is (syringe, vial).
Pool is liquid in an open dish the game raises from the dish floor.
"""

from __future__ import annotations

import re
from collections.abc import Callable
from functools import partial
from pathlib import Path

import numpy as np
import trimesh

from .geometry import Model, cylinder, ellipsoid, extrude, finish, lathe, merge, moved, rotated, scaled, superellipsoid, torus, tube

TOOLS_CFG = Path(__file__).resolve().parents[2] / "data" / "tools.cfg"
X, Y, Z = (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)


def tool_lengths() -> dict[str, float]:
    """Reads `length` per tool section from data/tools.cfg so tips land where the game expects them."""
    lengths: dict[str, float] = {}
    section = ""
    for line in TOOLS_CFG.read_text().splitlines():
        header = re.match(r"^\[(\w+)\]", line)
        if header:
            section = header.group(1)
        value = re.match(r"^length=([\d.]+)", line)
        if value and section:
            lengths[section] = float(value.group(1))
    return lengths


def along_z(mesh: trimesh.Trimesh, z: float = 0.0) -> trimesh.Trimesh:
    """Turns a lathe profile (built along +Y) to point down -Z, starting at z."""
    return moved(rotated(mesh, -np.pi / 2, X), (0.0, 0.0, z))


def rod(radius: float, z0: float, z1: float, y: float = 0.0, x: float = 0.0, sections: int = 16) -> trimesh.Trimesh:
    return cylinder(radius, (x, y, z0), (x, y, z1), sections)


def flat(outline: list[tuple[float, float]], thickness: float, plane: str = "yz") -> trimesh.Trimesh:
    """Extruded plate. Outline points are (a, b) in the given plane: 'yz' lies vertical, 'xz' lies flat."""
    mesh = extrude(outline, thickness)
    if plane == "yz":
        mesh = rotated(mesh, np.pi / 2, Y)
        mesh = rotated(mesh, np.pi / 2, Z)
    else:
        mesh = rotated(mesh, np.pi / 2, X)
    return mesh


def _scalpel(m: Model, length: float, dull: bool) -> None:
    """A scalpel, or with dull a switchblade. The blade stands in the YZ plane, edge down, and the flat handle
    lies in the same plane (thin across X), like a real one: the game cuts along where that plane meets the skin.
    A scalpel is a #3 handle with a #10 blade, about 4 cm of it showing; the switchblade's blade is most of its length."""
    start = -0.035 if dull else -length + 0.04
    back = length * 0.62 - 0.035 if dull else 0.075
    handle = superellipsoid((0.004, 0.011, back - start), 0.3, (0.0, 0.0, (back + start) * 0.5))
    grip = merge(*[superellipsoid((0.005, 0.012, 0.003), 0.4, (0.0, 0.0, z)) for z in np.linspace(-0.02, 0.02, 6)])
    m.add("Handle", merge(handle, grip), "black_plastic" if dull else "steel")
    # Straight spine on top, the belly of the edge curving down to the point.
    edge = [(start, 0.004), (start - 0.012, 0.006), (-length + 0.015, 0.004), (-length, -0.001), (-length + 0.02, -0.006), (start, -0.004)]
    blade = extrude([(z, y) for z, y in edge], 0.0012 if dull else 0.0008)
    # The outline runs along -Z from the handle; a quarter turn the other way would point the blade back through the hand.
    m.add("Blade", rotated(blade, -np.pi / 2, Y), "dark_steel" if dull else "chrome")


def _tweezers(m: Model, length: float, jaw_len: float, width: float, ringed: bool, material: str) -> None:
    """Forceps, hemostats and needle holders. The hinge sits at the back for forceps, at the box joint for ringed tools."""
    hinge_z = -length + jaw_len if ringed else 0.03
    for name, side in (("JawA", 1.0), ("JawB", -1.0)):
        if ringed:
            arm = tube(
                [(side * 0.006, 0.0, 0.05), (side * 0.004, 0.0, 0.0), (side * 0.0015, 0.0, hinge_z), (side * 0.0008, 0.0, -length)],
                [(0.0025, 0.002), (0.0022, 0.002), (0.002, 0.0018), (0.0012, 0.0012)],
                ring=10,
            )
            ring = torus(0.009, 0.0022, (side * 0.012, 0.0, 0.06), "y")
            mesh = merge(arm, ring)
        else:
            mesh = tube(
                [(0.0, 0.0, 0.03), (side * width, 0.0, -length * 0.2), (side * width * 0.4, 0.0, -length * 0.8), (side * 0.0008, 0.0, -length)],
                [(0.0012, 0.004), (0.0012, 0.0045), (0.001, 0.003), (0.0008, 0.0015)],
                ring=10,
            )
        m.add(name, mesh, material, (0.0, 0.0, hinge_z))


def _needle(m: Model, length: float) -> None:
    _tweezers(m, length, 0.018, 0.0, True, "steel")
    arc = [(0.012 * np.cos(t), 0.012 * np.sin(t) - 0.004, -length) for t in np.linspace(-0.2, np.pi, 8)]
    thread = tube([(0.012, -0.006, -length), (0.02, -0.02, -length + 0.02), (0.015, -0.03, -length + 0.05)], [(0.0006, 0.0006)] * 3, ring=6)
    m.add("SutureNeedle", tube(arc, [(0.0007, 0.0007)] * len(arc), ring=6), "chrome", (0.0, 0.0, -length), "JawA")
    m.add("Thread", thread, "fabric_dark", (0.0, 0.0, -length), "SutureNeedle")


def _retractor(m: Model, length: float) -> None:
    m.add("Handle", superellipsoid((0.018, 0.006, length * 0.6), 0.3, (0.0, 0.0, -length * 0.2)), "steel")
    hook = flat([(-length * 0.5, 0.0), (-length, 0.0), (-length, -0.03), (-length + 0.006, -0.03), (-length + 0.006, -0.004), (-length * 0.5, -0.004)], 0.026)
    m.add("Hook", hook, "chrome")


def _pistol_stapler(m: Model, length: float, office: bool) -> None:
    if office:
        base = superellipsoid((0.035, 0.012, length), 0.35, (0.0, -0.012, -length * 0.5))
        m.add("Base", base, "black_plastic")
        top = superellipsoid((0.03, 0.014, length * 0.95), 0.35, (0.0, 0.004, -length * 0.5))
        m.add("Trigger", top, "dark_steel", (0.0, 0.0, 0.0))
        return
    body = superellipsoid((0.03, 0.05, length * 0.8), 0.4, (0.0, 0.01, -length * 0.45))
    nose = superellipsoid((0.02, 0.018, 0.03), 0.4, (0.0, -0.005, -length + 0.012))
    m.add("Body", merge(body, nose), "blue_plastic")
    trigger = superellipsoid((0.018, 0.05, 0.016), 0.4, (0.0, -0.035, -0.02))
    m.add("Trigger", trigger, "plastic", (0.0, -0.01, -0.02))


def _tape(m: Model, length: float, color: str) -> None:
    roll = lathe([(0.012, -0.012), (0.026, -0.012), (0.026, 0.012), (0.012, 0.012), (0.012, -0.012)], 28)
    tail = flat([(0.0, 0.026), (-0.03, 0.026), (-0.03, 0.024), (0.0, 0.024)], 0.024)
    m.add("Roll", moved(merge(rotated(roll, np.pi / 2, Z), tail), (0.0, 0.0, -length * 0.5)), color)


def _pen(m: Model, length: float, body: str, tip: str, glow: bool) -> None:
    m.add(
        "Body",
        tube([(0.0, 0.0, 0.04), (0.0, 0.0, -length * 0.4), (0.0, 0.0, -length * 0.8)], [(0.006, 0.006), (0.0065, 0.0065), (0.005, 0.005)], ring=16),
        body,
    )
    m.add(
        "Tip",
        along_z(lathe([(0.0, 0.0), (0.004, 0.0), (0.0015, length * 0.2 - 0.002), (0.0, length * 0.2)], 12), -length * 0.8),
        tip,
        (0.0, 0.0, -length * 0.8),
    )
    if glow:
        m.add("Glow", ellipsoid((0.003, 0.003, 0.003), (0.0, 0.0, -length)), "flame", (0.0, 0.0, -length))
        m.add("Button", superellipsoid((0.004, 0.003, 0.012), 0.4, (0.0, 0.006, -0.01)), "yellow_plastic")


# Stroke digits for printed numbers, on a 2 x 4 grid (x across, y up): crisp at a millimeter or two, like a scale print.
DIGITS: dict[str, list[list[tuple[float, float]]]] = {
    "0": [[(0, 0), (2, 0), (2, 4), (0, 4), (0, 0)]],
    "1": [[(1, 0), (1, 4), (0.3, 3.2)]],
    "2": [[(0, 4), (2, 4), (2, 2), (0, 2), (0, 0), (2, 0)]],
    "3": [[(0, 4), (2, 4), (2, 0), (0, 0)], [(0.4, 2), (2, 2)]],
    "4": [[(0, 4), (0, 2), (2, 2)], [(2, 4), (2, 0)]],
    "5": [[(2, 4), (0, 4), (0, 2), (2, 2), (2, 0), (0, 0)]],
    "6": [[(2, 4), (0, 4), (0, 0), (2, 0), (2, 2), (0, 2)]],
    "7": [[(0, 4), (2, 4), (0.8, 0)]],
    "8": [[(0, 0), (2, 0), (2, 4), (0, 4), (0, 0)], [(0, 2), (2, 2)]],
    "9": [[(2, 2), (0, 2), (0, 4), (2, 4), (2, 0), (0, 0)]],
}
# Syringe scales by size: ml between the numbered marks and how many fine ticks each of those steps holds.
SCALES = {3.0: (1.0, 10), 10.0: (1.0, 5), 50.0: (10.0, 10)}


def _ink(radius: float, a: tuple[float, float], b: tuple[float, float], width: float) -> trimesh.Trimesh:
    """A printed stroke on a barrel along -Z: from a to b, each (z, angle around the barrel from tool -Y), wrapped
    onto the surface as a thin film so it bends with the barrel instead of cutting a chord through it."""
    inner, outer = radius + 0.00002, radius + 0.0001
    (z0, t0), (z1, t1) = a, b
    length = float(np.hypot(z1 - z0, (t1 - t0) * radius))
    steps = max(1, int(abs(t1 - t0) / 0.15))
    # Across the stroke, in (z, arc length) on the unrolled barrel.
    across = np.array([-(t1 - t0) * radius, z1 - z0]) / max(length, 1e-9) * width * 0.5
    verts = []
    for i in range(steps + 1):
        f = i / steps
        z, t = z0 + (z1 - z0) * f, t0 + (t1 - t0) * f
        for side in (-1.0, 1.0):
            zz, tt = z + across[0] * side, t + across[1] * side / radius
            angle = -np.pi / 2 + tt
            for r in (inner, outer):
                verts.append([r * np.cos(angle), r * np.sin(angle), zz])
    faces = []
    # Each section holds left-inner, left-outer, right-inner, right-outer: four sides around the stroke, two end caps.
    for i in range(steps):
        k, n = i * 4, i * 4 + 4
        for p, q in ((1, 3), (3, 2), (2, 0), (0, 1)):
            faces += [[k + p, k + q, n + q], [k + p, n + q, n + p]]
    last = steps * 4
    faces += [[0, 3, 1], [0, 2, 3], [last, last + 1, last + 3], [last, last + 3, last + 2]]
    return finish(trimesh.Trimesh(np.array(verts), np.array(faces), process=True))


def _number(radius: float, text: str, z: float, t: float, height: float, width: float) -> list[trimesh.Trimesh]:
    """Digits printed around a barrel, centered on z along it, their foot at angle t, reading toward the plunger
    with their tops toward tool -X (up when the syringe is held up to look at, see SurgeonHand.inspect_basis())."""
    unit = height / 4.0
    advance = unit * 2.9
    start = z - (advance * len(text) - unit * 0.9) / 2.0
    strokes = []
    for i, char in enumerate(text):
        for line in DIGITS[char]:
            points = [(start + i * advance + x * unit, t - y * unit / radius) for x, y in line]
            strokes += [_ink(radius, p, q, width) for p, q in zip(points, points[1:], strict=False)]
    return strokes


def _syringe(m: Model, length: float, radius: float, volume: float) -> None:
    """Glass barrel with a printed scale on one side (tool -Y, toward the eyes when held up to look at): fine ticks,
    longer ones halfway between numbers, the longest numbered in ml, like a real syringe's print. A tapered nozzle
    with a colored needle hub, a black rubber stopper on a cross-ribbed rod and a thumb press.
    Built empty: the game pulls the plunger back as it fills, the stopper's face reading the scale."""
    barrel_len = length * 0.62
    front = -barrel_len + 0.002
    travel = barrel_len * 0.85
    nozzle = barrel_len + 0.004
    barrel = lathe([(0.0, 0.0), (radius, 0.0), (radius, barrel_len - 0.003), (radius * 0.35, barrel_len), (0.0015, nozzle), (0.0, nozzle)], 24)
    m.add("Barrel", along_z(barrel), "glass")
    m.add("Level", rod(radius * 0.9, front, front + travel), "drug", (0.0, 0.0, front))
    numbered, fine = SCALES.get(volume, (volume / 10.0, 5))
    tick = max(0.00016, radius * 0.03)
    ink = []
    count = round(volume / numbered * fine)
    for i in range(count + 1):
        z = front + travel * i / count
        major = i % fine == 0
        half = not major and fine % 2 == 0 and i % (fine // 2) == 0
        # Ticks start on one baseline and run around the barrel, the longer the rounder the number.
        reach = 1.05 if major else 0.75 if half else 0.55
        ink.append(_ink(radius, (z, 0.45), (z, 0.45 - reach), tick))
        if major and i > 0:
            ink += _number(radius, f"{i // fine * numbered:g}", z, 0.45 - reach - 0.12, radius * 0.3, tick * 1.2)
    m.add("Marks", merge(*ink), "marks")
    m.add("Flange", superellipsoid((radius * 2.0 + 0.012, 0.0025, 0.006), 0.3, (0.0, 0.0, 0.0)), "clear_plastic")
    # The stopper's front face sits on the scale's zero; two ribs seal it against the barrel, a low cone faces the
    # needle. Profile from its back (y 0) to its front (y 0.0065), turned to face down -Z.
    seal = [(radius * 0.85, 0.0), (radius * 0.95, 0.0015), (radius * 0.88, 0.0028), (radius * 0.95, 0.0045), (radius * 0.7, 0.006)]
    stopper = lathe([(0.0, 0.0), *seal, (0.0, 0.0065)], 24)
    m.add("Plunger", along_z(stopper, front + 0.0065), "rubber")
    rod_from, rod_to = front + 0.0065, 0.013
    web = radius * 0.75
    webs = [superellipsoid((web * 2, 0.0009, rod_to - rod_from), 0.1, (0.0, 0.0, (rod_from + rod_to) / 2))]
    webs.append(rotated(webs[0], np.pi / 2, Z))
    thumb = lathe([(0.0, 0.0), (radius * 1.5, 0.0), (radius * 1.5, 0.002), (0.0, 0.002)], 24)
    m.add("Rod", merge(*webs, along_z(thumb, 0.015)), "clear_plastic", parent="Plunger")
    hub = lathe([(0.0, 0.0), (0.0024, 0.0), (0.0024, 0.0015), (0.0018, 0.002), (0.0011, 0.007), (0.0, 0.007)], 16)
    m.add("Hub", along_z(hub, -barrel_len - 0.003), "green_plastic")
    m.add("Needle", rod(0.00035, -barrel_len - 0.009, -length, sections=8), "chrome")


def _vial(m: Model, length: float) -> None:
    """Glass vial with a paper label and a tinted cap at the tip. Level is the drug left in it."""
    radius = 0.012
    body_len = length * 0.72
    m.add("Glass", along_z(lathe([(0.0, 0.0), (radius, 0.0), (radius, body_len), (radius * 0.55, body_len + 0.004), (0.0, body_len + 0.004)], 20)), "glass")
    m.add("Level", rod(radius * 0.88, -0.002, -body_len + 0.002), "drug", (0.0, 0.0, -0.002))
    label = [(0.0, body_len * 0.4), (radius + 0.0004, body_len * 0.4), (radius + 0.0004, body_len * 0.75), (0.0, body_len * 0.75)]
    m.add("Label", along_z(lathe(label, 20)), "paper")
    m.add("Cap", rod(radius * 0.7, -body_len - 0.004, -length), "tint")


def _bag(m: Model, length: float) -> None:
    pouch = superellipsoid((0.09, 0.018, length * 0.85), 0.35, (0.0, 0.0, -length * 0.45))
    m.add("Bag", pouch, "tint")
    m.add("Label", superellipsoid((0.05, 0.002, 0.04), 0.3, (0.0, 0.0185, -length * 0.4)), "paper")
    m.add("Port", merge(rod(0.004, -length * 0.87, -length), rod(0.006, 0.0, 0.02, 0.0, 0.0)), "plastic")


def _flask(m: Model, length: float) -> None:
    body = superellipsoid((0.07, 0.022, length * 0.8), 0.45, (0.0, 0.0, -length * 0.4))
    m.add("Flask", body, "steel")
    m.add("Wrap", superellipsoid((0.072, 0.023, length * 0.45), 0.45, (0.0, 0.0, -length * 0.42)), "leather")
    m.add("Cap", rod(0.008, -length * 0.8, -length), "chrome")


def _thermos(m: Model, length: float) -> None:
    m.add("Bottle", along_z(lathe([(0.0, 0.0), (0.03, 0.0), (0.03, length * 0.8), (0.022, length * 0.85), (0.0, length * 0.85)], 24)), "fabric_dark")
    m.add("Cap", rod(0.024, -length * 0.85, -length), "steel")


def _paddle(m: Model, length: float) -> None:
    m.add("Handle", superellipsoid((0.035, 0.06, 0.05), 0.4, (0.0, 0.02, 0.0)), "black_plastic")
    plate = superellipsoid((0.08, 0.012, 0.09), 0.25, (0.0, -0.02, -length + 0.045))
    m.add("Plate", plate, "chrome")
    m.add("Housing", superellipsoid((0.085, 0.025, 0.1), 0.35, (0.0, -0.005, -length + 0.05)), "yellow_plastic")
    m.add("Light", ellipsoid((0.006, 0.004, 0.006), (0.0, 0.052, 0.0)), "flame", (0.0, 0.052, 0.0))


def _saw(m: Model, length: float, powered: bool) -> None:
    if powered:
        m.add("Body", superellipsoid((0.05, 0.06, length * 0.7), 0.4, (0.0, 0.0, -length * 0.3)), "yellow_plastic")
        m.add("Grip", superellipsoid((0.04, 0.045, 0.1), 0.4, (0.0, -0.01, 0.03)), "rubber")
        blade = flat([(0.0, 0.012), (-0.07, 0.012), (-0.07, -0.012), (0.0, -0.012)], 0.002, "xz")
        teeth = merge(*[moved(flat([(0.0, 0.0), (-0.003, 0.004), (-0.006, 0.0)], 0.002, "xz"), (x, 0.0, -0.07)) for x in np.linspace(-0.011, 0.008, 5)])
        m.add("Blade", moved(merge(blade, teeth), (0.0, 0.0, -length * 0.65)), "chrome", (0.0, 0.0, -length * 0.65))
        return
    m.add("Grip", superellipsoid((0.03, 0.06, 0.09), 0.4, (0.0, 0.0, 0.02)), "black_plastic")
    blade = flat([(-0.02, 0.02), (-length, 0.01), (-length, -0.02), (-0.02, -0.03)], 0.0015)
    teeth = merge(*[moved(flat([(0.0, 0.0), (-0.003, -0.006), (-0.006, 0.0)], 0.0015), (0.0, -0.02, z)) for z in np.linspace(-0.025, -length, 22)])
    m.add("Blade", merge(blade, teeth), "steel")


def _hammer(m: Model, length: float) -> None:
    m.add("Handle", rod(0.011, 0.04, -length + 0.03, sections=14), "dark_steel")
    m.add("Grip", rod(0.014, 0.05, -0.06, sections=14), "rubber")
    m.add("Head", cylinder(0.022, (-0.045, 0.0, -length + 0.015), (0.045, 0.0, -length + 0.015), 20), "steel")


def _screwdriver(m: Model, length: float) -> None:
    m.add("Handle", along_z(lathe([(0.0, 0.0), (0.014, 0.004), (0.016, 0.07), (0.01, 0.09), (0.0, 0.09)], 8), 0.06), "yellow_plastic")
    m.add(
        "Shaft",
        merge(rod(0.0035, -0.05, -length + 0.008), flat([(-length + 0.008, 0.004), (-length, 0.001), (-length, -0.001), (-length + 0.008, -0.004)], 0.002)),
        "chrome",
    )


def _suction(m: Model, length: float, straw: bool) -> None:
    pts = [(0.0, 0.0, 0.05), (0.0, 0.0, -length * 0.5), (0.0, -0.01, -length * 0.8), (0.0, -0.03, -length)]
    radius = 0.004 if straw else 0.006
    m.add("Tube", tube(pts, [(radius, radius)] * 4, ring=14), "steel" if straw else "clear_plastic")
    if not straw:
        m.add("Bulb", ellipsoid((0.006, 0.006, 0.009), (0.0, -0.032, -length)), "clear_plastic")
        m.add("Hose", tube([(0.0, 0.0, 0.05), (0.0, -0.02, 0.1), (0.02, -0.08, 0.14)], [(0.007, 0.007)] * 3, ring=12), "clear_plastic")


def _swab(m: Model, length: float) -> None:
    layers = merge(*[superellipsoid((0.06, 0.004, 0.06), 0.3, (0.0, i * 0.0035, -length * 0.5), detail=10) for i in range(4)])
    m.add("Pad", layers, "cotton")


def _cotton_pad(m: Model, length: float) -> None:
    m.add("Pad", superellipsoid((length, 0.006, length), 0.9, (0.0, 0.0, -length * 0.5)), "cotton")


def _iodine_bottle(m: Model, length: float) -> None:
    """Brown bottle with a pouring spout at the tip."""
    m.add("Bottle", along_z(lathe([(0.0, 0.0), (0.024, 0.0), (0.024, length * 0.62), (0.01, length * 0.75), (0.0, length * 0.75)], 24)), "tint")
    m.add("Label", along_z(lathe([(0.0, length * 0.15), (0.0245, length * 0.15), (0.0245, length * 0.5), (0.0, length * 0.5)], 24)), "paper")
    m.add("Spout", along_z(lathe([(0.0, length * 0.74), (0.011, length * 0.74), (0.006, length * 0.94), (0.003, length), (0.0, length)], 16)), "plastic")


def _iodine_dish(m: Model, length: float) -> None:
    """Round steel bowl. The game shows Liquid while there's iodine in it."""
    bowl = lathe([(0.0, 0.0), (0.035, 0.0), (0.045, 0.022), (0.042, 0.023), (0.032, 0.004), (0.0, 0.004)], 32)
    liquid = lathe([(0.0, 0.004), (0.031, 0.004), (0.0355, 0.012), (0.0, 0.012)], 32)
    m.add("Dish", moved(bowl, (0.0, -0.01, -length * 0.5)), "steel")
    m.add("Liquid", moved(liquid, (0.0, -0.01, -length * 0.5)), "iodine", (0.0, -0.006, -length * 0.5))


def _misc(m: Model, kind: str, length: float) -> None:
    if kind == "switchblade":
        _scalpel(m, length, True)
    elif kind == "lighter":
        m.add("Body", superellipsoid((0.022, 0.012, length * 0.75), 0.35, (0.0, 0.0, -length * 0.35)), "tint")
        m.add("Hood", superellipsoid((0.018, 0.011, length * 0.22), 0.35, (0.0, 0.0, -length * 0.82)), "chrome")
        m.add("Wheel", cylinder(0.005, (-0.004, 0.004, -length * 0.9), (0.004, 0.004, -length * 0.9), 12), "dark_steel")
        m.add("Flame", ellipsoid((0.005, 0.005, 0.014), (0.0, 0.0, -length - 0.008)), "flame", (0.0, 0.0, -length))
    elif kind == "paper_clips":
        clips = []
        for i in range(3):
            loop = [
                (0.004, 0.0, 0.0),
                (0.004, 0.0, -length + 0.004),
                (-0.004, 0.0, -length + 0.004),
                (-0.004, 0.0, -0.006),
                (0.002, 0.0, -0.006),
                (0.002, 0.0, -length * 0.7),
            ]
            clips.append(moved(tube(loop, [(0.0006, 0.0006)] * len(loop), ring=6, smooth=3, caps=True), (i * 0.012 - 0.012, i * 0.0015, 0.0)))
        m.add("Clips", merge(*clips), "chrome")
    elif kind == "gas_mask":
        mask = along_z(lathe([(0.0, 0.0), (0.012, 0.0), (0.04, 0.05), (0.045, 0.06), (0.0, 0.06)], 24), -length + 0.06)
        m.add("Mask", mask, "clear_plastic")
        m.add("Hose", tube([(0.0, 0.0, -length + 0.06), (0.0, 0.0, 0.0), (0.0, -0.03, 0.08)], [(0.008, 0.008)] * 3, ring=12), "blue_plastic")
    elif kind == "cocaine":
        m.add("Bag", superellipsoid((0.04, 0.006, length), 0.4, (0.0, 0.0, -length * 0.5)), "clear_plastic")
        m.add("Powder", superellipsoid((0.03, 0.005, length * 0.6), 0.6, (0.0, -0.001, -length * 0.55)), "cotton")
    elif kind == "tourniquet":
        strap = tube(
            [(0.0, 0.0, 0.0), (0.02, 0.0, -length * 0.3), (0.0, 0.0, -length * 0.6), (-0.02, 0.0, -length * 0.3), (0.0, 0.0, 0.0)],
            [(0.003, 0.015)] * 5,
            ring=12,
            exponent=6.0,
        )
        m.add("Strap", strap, "fabric_dark")
        m.add("Buckle", superellipsoid((0.035, 0.01, 0.025), 0.3, (0.0, 0.0, -length)), "red_plastic")
        m.add("Windlass", rod(0.004, -length + 0.02, -length - 0.02, 0.012), "black_plastic")
    elif kind == "iv_catheter":
        m.add("Hub", superellipsoid((0.012, 0.012, 0.02), 0.5, (0.0, 0.0, 0.0)), "tint")
        m.add("Wings", superellipsoid((0.04, 0.002, 0.015), 0.4, (0.0, -0.004, -0.012)), "green_plastic")
        m.add("Needle", merge(rod(0.0014, -0.01, -length + 0.01), rod(0.0005, -length + 0.01, -length)), "chrome")
    elif kind == "skin_graft":
        m.add("Backing", superellipsoid((0.08, 0.002, length), 0.2, (0.0, -0.002, -length * 0.5)), "paper")
        m.add("Graft", superellipsoid((0.07, 0.0015, length * 0.9), 0.25, (0.0, 0.0005, -length * 0.5)), "skin")
    elif kind == "surgical_cap":
        m.add("Cap", superellipsoid((0.1, 0.06, 0.11), 0.7, (0.0, 0.0, -length * 0.5)), "tint")
    elif kind == "kidney_dish":
        outer = lathe([(0.0, 0.0), (0.06, 0.0), (0.07, 0.025), (0.066, 0.026), (0.056, 0.004), (0.0, 0.004)], 32)
        m.add("Dish", moved(scaled(outer, (0.7, 1.0, 1.5)), (0.0, -0.01, -length * 0.5)), "steel")
        # Liquid squirted in from a syringe, full to just under the rim. Its origin is on the dish floor, so the game
        # raises the level by scaling it up from there (src/tools/surgical_tool.gd show_liquid()).
        pool = lathe([(0.0, 0.004), (0.0555, 0.004), (0.0628, 0.02), (0.0, 0.02)], 32)
        m.add("Pool", moved(scaled(pool, (0.7, 1.0, 1.5)), (0.0, -0.01, -length * 0.5)), "drug", (0.0, -0.006, -length * 0.5))


def _misc_maker(kind: str, model: Model, length: float) -> None:
    _misc(model, kind, length)


def build() -> list[Model]:
    lengths = tool_lengths()
    makers: dict[str, Callable[[Model, float], None]] = {
        "scalpel": lambda m, length: _scalpel(m, length, False),
        "forceps": lambda m, length: _tweezers(m, length, 0.0, 0.006, False, "steel"),
        "hemostat": lambda m, length: _tweezers(m, length, 0.035, 0.0, True, "steel"),
        "retractor": _retractor,
        "needle": _needle,
        "skin_stapler": lambda m, length: _pistol_stapler(m, length, False),
        "office_stapler": lambda m, length: _pistol_stapler(m, length, True),
        "surgical_tape": lambda m, length: _tape(m, length, "fabric_white"),
        "duct_tape": lambda m, length: _tape(m, length, "dark_steel"),
        "cautery": lambda m, length: _pen(m, length, "green_plastic", "chrome", True),
        "marker": lambda m, length: _pen(m, length, "tint", "black_plastic", False),
        "syringe_3": partial(_syringe, radius=0.0045, volume=3.0),
        "syringe_10": partial(_syringe, radius=0.0065, volume=10.0),
        "syringe_50": partial(_syringe, radius=0.013, volume=50.0),
        "vial": _vial,
        "iv_bag": _bag,
        "whiskey_flask": _flask,
        "coffee_thermos": _thermos,
        "defibrillator": _paddle,
        "bone_saw": lambda m, length: _saw(m, length, False),
        "heavy_saw": lambda m, length: _saw(m, length, True),
        "mallet": _hammer,
        "screwdriver": _screwdriver,
        "suction": lambda m, length: _suction(m, length, False),
        "metal_straw": lambda m, length: _suction(m, length, True),
        "gauze": _swab,
        "cotton_pad": _cotton_pad,
        "iodine_bottle": _iodine_bottle,
        "iodine_dish": _iodine_dish,
    }
    for kind in ("switchblade", "lighter", "paper_clips", "gas_mask", "cocaine", "tourniquet", "iv_catheter", "skin_graft", "surgical_cap", "kidney_dish"):
        makers[kind] = partial(_misc_maker, kind)
    shared_length = {"vial": lengths.get("vial_propofol", 0.06), "iv_bag": lengths.get("saline_bag", 0.15)}
    models = []
    for name, maker in makers.items():
        model = Model("tools", name)
        maker(model, shared_length.get(name, lengths.get(name, 0.12)))
        models.append(model)
    return models
