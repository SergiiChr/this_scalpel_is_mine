"""Every grabbable tool. Grip at the origin, working tip at (0, 0, -length) with length from data/tools.cfg.

Moving parts are separate nodes the game animates (src/Tools/ToolAnimator.cs):
JawA/JawB open and close (a Gelpi retractor's stand as far open as its wheel set), Plunger slides, Trigger squeezes,
Blade oscillates, Flame/Glow/Light show while in use.
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
from shapely.geometry import Polygon

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
    # The sharp point is the tool's exact gameplay tip. Six millimetres back, the curve crosses the holder's axis
    # between both jaws; the old transverse arc floated around their ends instead of being gripped by them.
    radius = 0.012
    clamp_back = 0.006
    center_y = -np.sqrt(radius * radius - (clamp_back * 0.5) ** 2)
    center_z = -length + clamp_back * 0.5
    tip_angle = np.arctan2(-clamp_back * 0.5, -center_y)
    # Its plane is vertical when the holder is horizontal: the curve hangs below the jaws and its sharp tangent
    # points down and forward into the aiming point, like a real loaded needle holder.
    arc = [(0.0, center_y + radius * np.cos(t), center_z + radius * np.sin(t)) for t in np.linspace(tip_angle, 2.7, 14)]
    m.add("SutureNeedle", tube(arc, [(0.0007, 0.0007)] * len(arc), ring=6, up=(1.0, 0.0, 0.0)), "chrome", (0.0, 0.0, -length), "JawA")


def _retractor(m: Model, length: float) -> None:
    """A skin hook on an elastic stay, like a ring retractor's: a thin wire running out of a blue stay and ending in a
    small sharp hook bent down and back toward the stay, which catches a wound's edge. Its front is the tool's tip, so
    lying on the skin the hook hangs into the cut."""
    wire = 0.0007
    m.add("Handle", tube([(0.0, 0.0, 0.0), (0.0, 0.0, -length * 0.55)], [(0.0022, 0.0022)] * 2, ring=12), "blue_plastic")
    hook = tube(
        [(0.0, 0.0, -length * 0.55), (0.0, 0.0, -length + 0.004), (0.0, -0.001, -length), (0.0, -0.004, -length + 0.0005), (0.0, -0.005, -length + 0.003)],
        [(wire, wire)] * 4 + [(0.0002, 0.0002)],
        ring=8,
        up=(1.0, 0.0, 0.0),
        smooth=4,
    )
    m.add("Hook", hook, "dark_steel")


# A Gelpi retractor's tips stand this far apart closed (ToolActions.SPREAD_RANGE.x in the game), this far in front of
# the joint its arms swing about. Its points hang this far below the arms (SurgicalTool.GELPI_DROP in the game).
GELPI_CLOSED = 0.012
GELPI_REACH = 0.11
GELPI_DROP = 0.015


def _gelpi(m: Model, length: float) -> None:
    """A Gelpi self-retaining retractor, made to lie along a cut: ring handles with a ratchet between them rising to a
    box joint, and two long arms bowing apart, each ending in a sharp point bent down under it to hook a wound's edge.
    The points' tips are on the tool's axis, so the game's tip is where they go in. The game swings each arm about the
    joint (the part origin, an upright axis) so the points stand as far apart as the wheel set
    (src/Tools/ToolAnimator.cs OpenTo()). The handles stay put, so the fingers in the rings don't have to follow."""
    joint = -length + GELPI_REACH
    half = GELPI_CLOSED / 2
    handles = []
    for side in (-1.0, 1.0):
        arm = tube(
            [(side * 0.006, 0.0, 0.05), (side * 0.004, 0.0, 0.0), (side * 0.002, GELPI_DROP * 0.6, joint * 0.6), (0.0, GELPI_DROP, joint)],
            [(0.0025, 0.002), (0.0022, 0.002), (0.0022, 0.002), (0.0024, 0.0022)],
            ring=10,
        )
        handles += [arm, torus(0.009, 0.0022, (side * 0.012, 0.0, 0.06), "y")]
    # The ratchet: a toothed bar from one handle across to the other, which it catches.
    handles.append(tube([(-0.0055, 0.0, 0.032), (-0.001, 0.0, 0.026), (0.006, 0.0, 0.024)], [(0.0012, 0.0009)] * 3, ring=8))
    handles += [superellipsoid((0.0008, 0.0012, 0.0006), 0.5, (x, 0.0, 0.0255 - x * 0.2)) for x in (-0.002, 0.0, 0.002)]
    m.add("Handle", merge(*handles), "steel")
    m.add("Joint", cylinder(0.004, (0.0, GELPI_DROP - 0.0035, joint), (0.0, GELPI_DROP + 0.0035, joint), 14), "chrome")
    for name, side in (("JawA", -1.0), ("JawB", 1.0)):
        end = -length + 0.002
        arm = tube(
            [(0.0, GELPI_DROP, joint), (side * (half + 0.006), GELPI_DROP, (joint + end) * 0.5), (side * (half - 0.001), GELPI_DROP, end)],
            [(0.0024, 0.0022), (0.0019, 0.0019), (0.0016, 0.0016)],
            ring=10,
        )
        # The point: out from the arm's end and bent down to a sharp tip where the jaw holds the skin.
        point = tube(
            [(side * (half - 0.001), GELPI_DROP, end), (side * (half + 0.0005), GELPI_DROP - 0.002, -length), (side * half, 0.0, -length)],
            [(0.0016, 0.0016), (0.0012, 0.0012), (0.0003, 0.0003)],
            ring=8,
            up=(1.0, 0.0, 0.0),
            smooth=3,
        )
        m.add(name, merge(arm, point), "steel", (0.0, GELPI_DROP, joint))


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


def _cautery(m: Model, length: float) -> None:
    """A slim cautery pencil with a short steel electrode and a heat envelope hugging its tip."""
    scale = length / 0.16

    def z(value: float) -> float:
        return value * scale

    m.add(
        "Body",
        tube(
            [(0.0, 0.0, z(0.040)), (0.0, 0.0, z(0.032)), (0.0, 0.0, z(-0.015)), (0.0, 0.0, z(-0.055))],
            [(0.0035, 0.0045), (0.0048, 0.006), (0.005, 0.0065), (0.0048, 0.006)],
            ring=16,
            exponent=2.5,
            smooth=3,
        ),
        "plastic",
    )
    m.add(
        "Grip",
        tube(
            [(0.0, 0.0, z(-0.055)), (0.0, 0.0, z(-0.068)), (0.0, 0.0, z(-0.100))],
            [(0.0048, 0.006), (0.0046, 0.0058), (0.0038, 0.0048)],
            ring=16,
            exponent=2.5,
            smooth=3,
        ),
        "blue_plastic",
    )
    m.add(
        "Nose",
        tube(
            [(0.0, 0.0, z(-0.100)), (0.0, 0.0, z(-0.112)), (0.0, 0.0, z(-0.130))],
            [(0.0038, 0.0048), (0.0032, 0.004), (0.0017, 0.002)],
            ring=16,
            exponent=2.5,
            smooth=3,
        ),
        "plastic",
    )
    m.add("Button", superellipsoid((0.0045, 0.0022, 0.019), 0.45, (0.0, 0.0058, z(-0.017)), detail=10), "blue_plastic")
    electrode = extrude(
        [(-0.0014, z(-0.144)), (0.0014, z(-0.144)), (0.0014, z(-0.158)), (0.001, z(-0.160)), (-0.001, z(-0.160)), (-0.0014, z(-0.158))],
        0.0007,
    )
    electrode.vertices = electrode.vertices[:, [0, 2, 1]]
    electrode = finish(electrode)
    electrode.unmerge_vertices()
    m.add("Tip", merge(cylinder(0.0012, (0.0, 0.0, z(-0.129)), (0.0, 0.0, z(-0.147)), 12), electrode), "steel", (0.0, 0.0, z(-0.130)))
    # The glow is deliberately small and centered at the gameplay tip.
    m.add("Glow", ellipsoid((0.0015, 0.0008, 0.002), (0.0, 0.0, z(-0.158)), subdivisions=1), "flame", (0.0, 0.0, z(-0.160)))


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


def _flask(m: Model, length: float) -> None:
    body = superellipsoid((0.07, 0.022, length * 0.8), 0.45, (0.0, 0.0, -length * 0.4))
    m.add("Flask", body, "steel")
    m.add("Wrap", superellipsoid((0.072, 0.023, length * 0.45), 0.45, (0.0, 0.0, -length * 0.42)), "leather")
    m.add("Cap", rod(0.008, -length * 0.8, -length), "chrome")


def _thermos(m: Model, length: float) -> None:
    m.add("Bottle", along_z(lathe([(0.0, 0.0), (0.03, 0.0), (0.03, length * 0.8), (0.022, length * 0.85), (0.0, length * 0.85)], 24)), "fabric_dark")
    m.add("Cap", rod(0.024, -length * 0.85, -length), "steel")


def _paddle(m: Model, length: float) -> None:
    """Compact defibrillator paddle with a transverse bridge handle and a broad electrode at -length."""
    outline = [
        (-0.026, -0.052),
        (0.026, -0.052),
        (0.038, -0.046),
        (0.043, -0.034),
        (0.043, 0.034),
        (0.038, 0.046),
        (0.026, 0.052),
        (-0.026, 0.052),
        (-0.038, 0.046),
        (-0.043, 0.034),
        (-0.043, -0.034),
        (-0.038, -0.046),
    ]
    plate = extrude(outline, 0.002)
    plate.apply_translation((0.0, 0.0, -length + 0.001))
    plate = finish(plate)
    plate.unmerge_vertices()
    m.add("Plate", plate, "steel")
    m.add("Housing", superellipsoid((0.092, 0.112, 0.013), 0.22, (0.0, 0.0, -length + 0.0085), detail=16), "black_plastic")

    # Two short supports leave an open finger well behind the broad plate.
    supports = [superellipsoid((0.029, 0.025, 0.049), 0.3, (0.0, y, -length + 0.036), detail=10) for y in (-0.040, 0.040)]
    m.add("Bridge", merge(*supports), "black_plastic")
    m.add(
        "Handle",
        tube(
            [(0.0, -0.043, 0.0), (0.0, -0.034, 0.0), (0.0, 0.034, 0.0), (0.0, 0.043, 0.0)],
            [(0.010, 0.011), (0.012, 0.014), (0.012, 0.014), (0.010, 0.011)],
            ring=16,
            exponent=3.0,
            smooth=3,
        ),
        "black_plastic",
    )
    m.add("Button", superellipsoid((0.014, 0.011, 0.004), 0.35, (0.0, 0.039, 0.016), detail=8), "orange_plastic")
    m.add("Light", ellipsoid((0.002, 0.002, 0.001), (0.004, 0.039, 0.0195), subdivisions=1), "flame", (0.004, 0.039, 0.0195))
    lead = [
        (0.0, -0.046, -0.012),
        (0.0, -0.063, -0.012),
        (0.003, -0.078, -0.014),
        (0.014, -0.088, -0.018),
        (0.025, -0.087, -0.021),
        (0.031, -0.078, -0.023),
        (0.027, -0.071, -0.025),
        (0.019, -0.073, -0.026),
        (0.018, -0.081, -0.027),
        (0.025, -0.094, -0.029),
    ]
    m.add("Cord", tube(lead, [(0.0023, 0.0023)] * len(lead), ring=8, smooth=3), "rubber")


def _saw(m: Model, length: float, powered: bool) -> None:
    if powered:
        body = tube(
            [(0.0, 0.003, 0.002), (0.0, 0.008, -0.030), (0.0, 0.008, -0.105), (0.0, 0.003, -0.160), (0.0, 0.0, -0.184)],
            [(0.022, 0.022), (0.026, 0.024), (0.024, 0.021), (0.014, 0.014), (0.012, 0.011)],
            ring=16,
            exponent=3.5,
            smooth=3,
        )
        m.add("Body", body, "plastic")
        m.add("MotorCap", superellipsoid((0.046, 0.046, 0.018), 0.3, (0.0, 0.003, 0.002), detail=12), "blue_plastic")
        grip = tube(
            [(0.0, -0.008, 0.077), (0.0, -0.008, 0.068), (0.0, -0.008, 0.040), (0.0, -0.005, 0.004)],
            [(0.011, 0.011), (0.020, 0.016), (0.022, 0.018), (0.017, 0.016)],
            ring=16,
            exponent=3.0,
            smooth=3,
        )
        m.add("Grip", grip, "black_plastic")
        m.add("Button", superellipsoid((0.012, 0.006, 0.026), 0.35, (0.0, 0.0365, -0.033), detail=8), "blue_plastic")
        carrier = tube(
            [(0.0, 0.0, -0.181), (-0.016, 0.0, -0.204), (-0.016, 0.0, -0.255)],
            [(0.011, 0.010), (0.008, 0.006), (0.008, 0.006)],
            ring=12,
            smooth=3,
        )
        m.add("Carrier", carrier, "steel")
        center_z, radius = -0.255, 0.045
        m.add("Spindle", cylinder(0.006, (-0.022, 0.0, center_z), (0.006, 0.0, center_z), 20), "dark_steel")
        m.add("Hub", cylinder(0.009, (0.001, 0.0, center_z), (0.005, 0.0, center_z), 24), "steel")

        def circular_plate(outline: list[tuple[float, float]], thickness: float, holes: list[list[tuple[float, float]]] | None = None) -> trimesh.Trimesh:
            if holes:
                mesh = trimesh.creation.extrude_polygon(Polygon(outline, holes), thickness)
                mesh.apply_translation((0.0, 0.0, -thickness / 2))
            else:
                mesh = extrude(outline, thickness)
            mesh.vertices = mesh.vertices[:, [2, 1, 0]]
            mesh = finish(mesh)
            mesh.unmerge_vertices()
            return mesh

        # The 36 tooth profiles form a continuous circular blade. Its centerline is
        # in YZ, so the Blade node rotates around X and its forward tooth reaches
        # the tool's established gameplay tip at z=-0.30.
        outline: list[tuple[float, float]] = []
        pitch = 2 * np.pi / 36
        for angle in np.arange(36) * pitch:
            for offset, blade_radius in ((-0.35, 0.0425), (0.0, radius), (0.25, radius), (0.55, 0.0425)):
                t = angle + offset * pitch
                outline.append((float(center_z - blade_radius * np.cos(t)), float(blade_radius * np.sin(t))))
        hole = [(float(center_z + 0.0065 * np.cos(t)), float(0.0065 * np.sin(t))) for t in np.linspace(0, 2 * np.pi, 32, endpoint=False)]
        m.add("Blade", circular_plate(outline, 0.0015, [hole]), "steel", (0.0, 0.0, center_z))

        # A fixed upper/rear guard leaves the leading and lower cutting arc exposed.
        angles = np.linspace(-0.75, 1.65, 28)
        guard = [(float(center_z + 0.052 * np.sin(t)), float(0.052 * np.cos(t))) for t in angles]
        guard += [(float(center_z + 0.048 * np.sin(t)), float(0.048 * np.cos(t))) for t in reversed(angles)]
        bridge = cylinder(0.0035, (-0.018, 0.007, -0.205), (0.001, 0.007, -0.205), 12)
        m.add("Guard", merge(circular_plate(guard, 0.007), bridge), "dark_steel")
        return
    # The fist stays at the origin; the palm swell tapers into a steel collar.
    grip = tube(
        [(0.0, 0.001, 0.065), (0.0, 0.001, 0.057), (0.0, -0.001, 0.025), (0.0, -0.002, -0.006), (0.0, 0.0, -0.024)],
        [(0.01, 0.008), (0.019, 0.014), (0.022, 0.0145), (0.018, 0.012), (0.013, 0.009)],
        ring=16,
        exponent=3.0,
        smooth=3,
    )
    m.add("Grip", grip, "black_plastic")
    m.add("Bolster", superellipsoid((0.022, 0.034, 0.01), 0.25, (0.0, 0.0, -0.025), detail=10), "steel")

    def plate(points: list[tuple[float, float]], thickness: float) -> trimesh.Trimesh:
        # A (Z,Y) outline extruded across X: the broad face and teeth share one vertical plane.
        mesh = extrude(points, thickness)
        mesh.vertices = mesh.vertices[:, [2, 1, 0]]
        return finish(mesh)

    # A continuous tooth outline replaces the detached triangles. The blunt nose reaches the gameplay tip.
    outline = [(-0.026, 0.016), (-0.048, 0.025), (-length + 0.011, 0.025), (-length + 0.001, 0.021), (-length, 0.0), (-length + 0.005, -0.021)]
    for z in np.linspace(-length + 0.01, -0.055, 22):
        root = -0.021 - 0.001 * ((z + length - 0.01) / (length - 0.065))
        outline.extend([(float(z), root), (float(z + 0.0033), root - 0.0042), (float(z + 0.0061), root)])
    outline += [(-0.042, -0.017), (-0.026, -0.01)]
    blade = plate(outline, 0.0015)
    tang = plate([(-0.027, 0.01), (0.041, 0.01), (0.041, -0.009), (-0.027, -0.009)], 0.003)
    m.add("Blade", merge(blade, tang), "steel")
    m.add("Rivet", cylinder(0.0032, (-0.0148, 0.002, 0.032), (0.0148, 0.002, 0.032), 12), "steel")


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


def _cig_pack(m: Model, length: float) -> None:
    """Paper pack with a colored band at the top, three filters sticking out."""
    m.add("Pack", superellipsoid((0.055, 0.022, length * 0.85), 0.15, (0.0, 0.0, -length * 0.425)), "paper")
    m.add("Band", superellipsoid((0.056, 0.023, length * 0.3), 0.15, (0.0, 0.0, -length * 0.7)), "tint")
    filters = [cylinder(0.004, (x, 0.0, -length * 0.85), (x, 0.0, -length), 10) for x in (-0.012, 0.0, 0.012)]
    m.add("Filters", merge(*filters), "iodine")


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
        # raises the level by scaling it up from there (src/Tools/SurgicalTool.cs ShowLiquid()).
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
        "gelpi": _gelpi,
        "needle": _needle,
        "skin_stapler": lambda m, length: _pistol_stapler(m, length, False),
        "office_stapler": lambda m, length: _pistol_stapler(m, length, True),
        "surgical_tape": lambda m, length: _tape(m, length, "fabric_white"),
        "duct_tape": lambda m, length: _tape(m, length, "dark_steel"),
        "cautery": _cautery,
        "marker": lambda m, length: _pen(m, length, "tint", "black_plastic", False),
        "syringe_3": partial(_syringe, radius=0.0045, volume=3.0),
        "syringe_10": partial(_syringe, radius=0.0065, volume=10.0),
        "syringe_50": partial(_syringe, radius=0.013, volume=50.0),
        "vial": _vial,
        "whiskey_flask": _flask,
        "coffee_thermos": _thermos,
        "cig_pack": _cig_pack,
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
    shared_length = {"vial": lengths.get("vial_propofol", 0.06)}
    models = []
    for name, maker in makers.items():
        model = Model("tools", name)
        maker(model, shared_length.get(name, lengths.get(name, 0.12)))
        models.append(model)
    return models
