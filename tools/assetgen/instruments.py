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
from numpy.typing import NDArray
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


def _office_stapler(m: Model, length: float) -> None:
    """Desktop stapler with a fixed rail and an upper lever that closes around its rear hinge."""
    factor = length / 0.14

    def scaled_z(mesh: trimesh.Trimesh) -> trimesh.Trimesh:
        return scaled(mesh, (1.0, 1.0, factor))

    base = tube(
        [(0, -0.012, 0.004), (0, -0.012, -0.004), (0, -0.012, -0.127), (0, -0.012, -0.14)],
        [(0.004, 0.013), (0.006, 0.019), (0.006, 0.019), (0.003, 0.012)],
        ring=16,
        exponent=3,
        smooth=3,
    )
    support = superellipsoid((0.022, 0.008, 0.016), 0.25, (0, -0.006, -0.131), detail=8)
    m.add("Base", scaled_z(merge(base, support)), "black_plastic")
    feet = [superellipsoid((0.028, 0.003, 0.018), 0.35, (0, -0.018, z), detail=8) for z in (-0.014, -0.119)]
    m.add("Feet", scaled_z(merge(*feet)), "rubber")
    # The fixed steel channel is open at the front and reveals the forming plate.
    rail = merge(*[superellipsoid((0.0025, 0.007, 0.103), 0.2, (x, -0.003, -0.068), detail=8) for x in (-0.012, 0.012)])
    m.add("Rail", scaled_z(rail), "steel")
    anvil = superellipsoid((0.023, 0.003, 0.018), 0.2, (0, -0.002, -0.131), detail=8)
    m.add("Anvil", scaled_z(anvil), "chrome")
    # A raised top rotates down through the existing 0.35 rad runtime squeeze at the rear hinge.
    top = tube(
        [(0, 0.003, -0.003), (0, 0.012, -0.023), (0, 0.041, -0.097), (0, 0.054, -0.130)],
        [(0.005, 0.012), (0.009, 0.016), (0.009, 0.016), (0.006, 0.012)],
        ring=16,
        exponent=3.5,
        smooth=3,
    )
    top.vertices[:, 1] += (-top.vertices[:, 2] / 0.13) * 0.007 + 0.005 * (1 + top.vertices[:, 2] / 0.16)
    m.add("Trigger", scaled_z(top), "dark_steel", (0, 0, 0))
    pad = superellipsoid((0.025, 0.003, 0.060), 0.25, (0, 0.027, -0.062), detail=8)
    # Follow the top's slope rather than floating a level pad above it.
    pad.vertices[:, 1] += -pad.vertices[:, 2] * 0.38 - 0.0235 + (-pad.vertices[:, 2] / 0.13) * 0.007 + 0.005 * (1 + pad.vertices[:, 2] / 0.16)
    m.add("GripPad", scaled_z(pad), "black_plastic", parent="Trigger")
    hinge = cylinder(0.008, (-0.020, 0, -0.006), (0.020, 0, -0.006), 24)
    m.add("Hinge", scaled_z(hinge), "steel")


def _pistol_stapler(m: Model, length: float, office: bool) -> None:
    if office:
        _office_stapler(m, length)
        return

    # The approved proposal is modeled at 0.12 m. Scale its Z coordinates with the configured tool length so
    # the forming shoe stays at the gameplay endpoint when that length changes.
    z_scale = length / 0.12

    def scaled_z(mesh: trimesh.Trimesh) -> trimesh.Trimesh:
        return scaled(mesh, (1.0, 1.0, z_scale))

    body = tube(
        [(0, 0.005, 0.024), (0, 0.013, 0.012), (0, 0.014, -0.035), (0, 0.008, -0.064), (0, 0.001, -0.082)],
        [(0.010, 0.011), (0.019, 0.022), (0.018, 0.020), (0.013, 0.014), (0.009, 0.009)],
        ring=16,
        exponent=3.5,
        smooth=3,
    )
    m.add("Body", scaled_z(body), "plastic")
    cartridge = superellipsoid((0.018, 0.015, 0.044), 0.23, (0, -0.0015, -0.091), detail=10)
    m.add("Cartridge", scaled_z(cartridge), "plastic")
    channel = superellipsoid((0.014, 0.003, 0.038), 0.2, (0, -0.008, -0.091), detail=8)
    m.add("Channel", scaled_z(channel), "dark_steel")
    shoe = extrude(
        [(-0.007, -0.005), (0.007, -0.005), (0.007, 0.004), (0.003, 0.004), (0.003, -0.001), (-0.003, -0.001), (-0.003, 0.004), (-0.007, 0.004)],
        0.003,
    )
    shoe = moved(finish(shoe), (0, 0, -0.1185))
    shoe.unmerge_vertices()
    supports = [superellipsoid((0.003, 0.009, 0.011), 0.2, (x, -0.0005, -0.112), detail=6) for x in (-0.0055, 0.0055)]
    m.add("Anvil", scaled_z(merge(shoe, *supports)), "steel")
    lever = tube(
        [(0, -0.012, -0.068), (0, -0.028, -0.055), (0, -0.047, -0.015), (0, -0.044, 0.012)],
        [(0.008, 0.005), (0.013, 0.006), (0.014, 0.007), (0.010, 0.005)],
        ring=12,
        exponent=3,
        smooth=3,
    )
    trigger_pivot = (0, -0.010, -0.070 * z_scale)
    m.add("Trigger", scaled_z(lever), "blue_plastic", trigger_pivot)
    hinge = superellipsoid((0.022, 0.009, 0.010), 0.5, (0, -0.010, -0.068), detail=8)
    m.add("Hinge", scaled_z(hinge), "blue_plastic")
    grip = superellipsoid((0.034, 0.003, 0.039), 0.3, (0, 0.034, -0.010), detail=10)
    m.add("GripPad", scaled_z(grip), "blue_plastic")


def _tape(m: Model, length: float, color: str) -> None:
    roll = lathe([(0.012, -0.012), (0.026, -0.012), (0.026, 0.012), (0.012, 0.012), (0.012, -0.012)], 28)
    tail = flat([(0.0, 0.026), (-0.03, 0.026), (-0.03, 0.024), (0.0, 0.024)], 0.024)
    m.add("Roll", moved(merge(rotated(roll, np.pi / 2, Z), tail), (0.0, 0.0, -length * 0.5)), color)


def _pen(m: Model, length: float, body: str, tip: str) -> None:
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
    """Handled vacuum flask with an insulated sleeve, steel trim and a seated cup cap."""
    z_scale = length / 0.18

    def turned(rows: list[tuple[float, float]]) -> trimesh.Trimesh:
        return along_z(lathe([(radius, z * z_scale) for radius, z in rows], 48))

    m.add(
        "Bottle",
        turned(
            [(0, 0), (0.024, 0), (0.028, 0.003), (0.029, 0.009), (0.029, 0.127), (0.028, 0.134), (0.022, 0.144), (0.017, 0.148), (0.017, 0.161), (0, 0.161)]
        ),
        "steel",
    )
    m.add("Sleeve", turned([(0.0285, 0.020), (0.030, 0.020), (0.030, 0.120), (0.0285, 0.120), (0.0285, 0.020)]), "fabric_dark")
    m.add("SleeveTrim", merge(*[turned([(0.0285, z), (0.0306, z), (0.0306, z + 0.003), (0.0285, z + 0.003), (0.0285, z)]) for z in (0.019, 0.118)]), "steel")
    m.add(
        "Base",
        turned([(0, -0.001), (0.024, -0.001), (0.028, 0.001), (0.0295, 0.004), (0.0295, 0.010), (0.029, 0.013), (0.027, 0.014), (0, 0.014)]),
        "black_plastic",
    )
    m.add("NeckSeal", turned([(0, 0.145), (0.018, 0.145), (0.019, 0.148), (0.019, 0.155), (0.018, 0.158), (0, 0.158)]), "black_plastic")
    # The stainless cup-cap widens gently toward its closed end and overlaps the neck seal.
    m.add("Cap", turned([(0, 0.151), (0.021, 0.151), (0.024, 0.153), (0.025, 0.157), (0.026, 0.173), (0.025, 0.178), (0.023, 0.180), (0, 0.180)]), "steel")
    m.add("CapBand", turned([(0.024, 0.157), (0.0264, 0.157), (0.0264, 0.161), (0.024, 0.161), (0.024, 0.157)]), "black_plastic")
    # An open side handle stays clear of the body and joins two short mounting pads.
    handle = tube(
        [(0.026, 0, -0.028), (0.037, 0, -0.028), (0.044, 0, -0.037), (0.045, 0, -0.105), (0.040, 0, -0.116), (0.027, 0, -0.119)],
        [(0.0045, 0.005)] * 6,
        ring=12,
        exponent=3,
        smooth=4,
    )
    m.add("Handle", scaled(handle, (1, 1, z_scale)), "black_plastic")
    mounts = [superellipsoid((0.008, 0.012, 0.012 * z_scale), 0.55, (0.030, 0, z * z_scale), detail=8) for z in (-0.028, -0.119)]
    m.add("Mounts", merge(*mounts), "black_plastic")


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
    """Molded orthopedic mallet with a compact crosshead at the gameplay endpoint."""
    z_scale = length / 0.28

    part_start = len(m.parts)
    m.add(
        "Grip",
        tube(
            [(0, 0, v) for v in (0.049, 0.038, -0.015, -0.060, -0.076)],
            [(0.010, 0.012), (0.013, 0.015), (0.012, 0.014), (0.011, 0.013), (0.008, 0.009)],
            ring=16,
            exponent=3,
            smooth=3,
        ),
        "rubber",
    )
    m.add(
        "Handle",
        tube(
            [(0, 0, v) for v in (-0.063, -0.087, -0.190, -0.247)],
            [(0.008, 0.008), (0.007, 0.006), (0.0065, 0.0055), (0.009, 0.008)],
            ring=12,
            exponent=2.5,
            smooth=2,
        ),
        "dark_steel",
    )
    m.add("ButtCap", cylinder(0.0115, (0, 0, 0.047), (0, 0, 0.051), 16), "steel")
    head = tube(
        [(-0.048, 0, -0.258), (-0.043, 0, -0.258), (0.043, 0, -0.258), (0.048, 0, -0.258)],
        [(0.019, 0.019), (0.024, 0.024), (0.024, 0.024), (0.019, 0.019)],
        ring=20,
        smooth=1,
    )
    head = head.slice_plane((0, 0, -0.28), (0, 0, 1), cap=True)
    m.add("Head", finish(head), "steel")
    for side, name in [(-1, "FaceL"), (1, "FaceR")]:
        m.add(name, cylinder(0.018, (side * 0.0475, 0, -0.258), (side * 0.052, 0, -0.258), 20), "plastic")
    for part in m.parts[part_start:]:
        part.mesh.vertices[:, 2] *= z_scale


def _screwdriver(m: Model, length: float) -> None:
    """Contoured molded handle with a collared shaft and centered flat tip."""
    handle = lathe(
        [
            (0.0, 0.0),
            (0.009, 0.002),
            (0.0145, 0.006),
            (0.016, 0.013),
            (0.013, 0.025),
            (0.0125, 0.040),
            (0.0145, 0.055),
            (0.0155, 0.066),
            (0.014, 0.078),
            (0.010, 0.088),
            (0.0, 0.094),
        ],
        16,
    )
    m.add("Handle", along_z(handle, 0.06), "yellow_plastic")

    ferrule = cylinder(0.0065, (0.0, 0.0, -0.049), (0.0, 0.0, -0.029), 16)
    m.add("Ferrule", ferrule, "chrome")
    m.add("Shaft", rod(0.0034, -0.044, -length + 0.012, sections=16), "chrome")

    # The thin plate has a square chisel edge at the configured contact plane.
    blade = extrude(
        [
            (-length + 0.014, 0.004),
            (-length, 0.004),
            (-length, -0.004),
            (-length + 0.014, -0.004),
        ],
        0.0016,
        faceted=False,
    )
    blade.vertices = blade.vertices[:, [2, 1, 0]]
    m.add("Tip", finish(blade), "chrome")


def _suction(m: Model, length: float, straw: bool) -> None:
    if straw:
        pts = [(0.0, 0.0, 0.05), (0.0, 0.0, -length * 0.5), (0.0, -0.01, -length * 0.8), (0.0, -0.03, -length)]
        m.add("Tube", tube(pts, [(0.004, 0.004)] * 4, ring=14), "steel")
        return

    # This geometry is modeled at 0.22 m; scale its Z coordinates with the configured length so the inlet
    # remains at the gameplay endpoint when that value changes.
    z_scale = length / 0.22

    def z(value: float) -> float:
        return value * z_scale

    m.add(
        "Handle",
        tube(
            [(0.0, 0.0, z(0.049)), (0.0, 0.0, z(0.034)), (0.0, 0.0, z(-0.024)), (0.0, 0.001, z(-0.051))],
            [(0.006, 0.006), (0.009, 0.010), (0.008, 0.009), (0.005, 0.005)],
            ring=16,
            exponent=2.8,
            smooth=3,
        ),
        "plastic",
    )
    m.add(
        "Tube",
        tube(
            [
                (0.0, 0.001, z(-0.045)),
                (0.0, 0.006, z(-0.090)),
                (0.0, 0.016, z(-0.147)),
                (0.0, 0.015, z(-0.177)),
                (0.0, 0.004, z(-0.198)),
                (0.0, 0.0, z(-0.203)),
            ],
            [(0.0048, 0.0048), (0.0045, 0.0045), (0.0043, 0.0043), (0.0042, 0.0042), (0.004, 0.004), (0.0038, 0.0038)],
            ring=12,
            smooth=3,
        ),
        "plastic",
    )
    profile = [
        (0.0038, 0.0),
        (0.0052, z(0.004)),
        (0.0065, z(0.009)),
        (0.0061, z(0.013)),
        (0.0045, z(0.018)),
        (0.0030, z(0.018)),
        (0.0028, z(0.013)),
        (0.0028, 0.0),
        (0.0038, 0.0),
    ]
    m.add("Bulb", along_z(lathe(profile, 20), z(-0.202)), "plastic")
    m.add("Bore", cylinder(0.0027, (0.0, 0.0, z(-0.205)), (0.0, 0.0, z(-0.206)), 20), "rubber")
    vent_profile = [(0.0036, 0.0), (0.0046, 0.0008), (0.0046, 0.0018), (0.0026, 0.0018), (0.0026, 0.0), (0.0036, 0.0)]
    m.add("Vent", moved(lathe(vent_profile, 16), (0.0, 0.0092, z(-0.008))), "blue_plastic")
    m.add("VentWell", cylinder(0.0025, (0.0, 0.0092, z(-0.008)), (0.0, 0.0097, z(-0.008)), 16), "rubber")
    m.add(
        "Connector",
        tube(
            [(0.0, 0.0, z(0.040)), (0.0, 0.0, z(0.048)), (0.0, 0.0, z(0.058))],
            [(0.006, 0.006), (0.007, 0.007), (0.0058, 0.0058)],
            ring=12,
            smooth=2,
        ),
        "blue_plastic",
    )
    m.add(
        "Hose",
        tube(
            [(0.0, 0.0, z(0.054)), (0.0, -0.003, z(0.075)), (0.0, -0.018, z(0.101)), (0.012, -0.044, z(0.130))],
            [(0.0053, 0.0053)] * 4,
            ring=12,
            smooth=3,
        ),
        "blue_plastic",
    )


def _swab(m: Model, length: float) -> None:
    layers = merge(*[superellipsoid((0.06, 0.004, 0.06), 0.3, (0.0, i * 0.0035, -length * 0.5), detail=10) for i in range(4)])
    m.add("Pad", layers, "cotton")


def _cotton_pad(m: Model, length: float) -> None:
    m.add("Pad", superellipsoid((length, 0.006, length), 0.9, (0.0, 0.0, -length * 0.5)), "cotton")


def _iodine_bottle(m: Model, length: float) -> None:
    """Shaped amber bottle with a paper band, ribbed screw collar and open dispensing nozzle."""
    z_scale = length / 0.14
    # Rounded rectangular horizontal sections, with continuous shoulders into the neck.
    angles = np.linspace(0, 2 * np.pi, 48, endpoint=False)
    outline: NDArray[np.float64] = np.column_stack(
        (np.sign(np.cos(angles)) * np.abs(np.cos(angles)) ** 0.65, np.sign(np.sin(angles)) * np.abs(np.sin(angles)) ** 0.65)
    )
    rows = [
        (0.003, 0.020, 0.014),
        (-0.001, 0.024, 0.018),
        (-0.007, 0.025, 0.019),
        (-0.075, 0.025, 0.019),
        (-0.084, 0.024, 0.018),
        (-0.097, 0.016, 0.013),
        (-0.103, 0.010, 0.010),
        (-0.116, 0.010, 0.010),
    ]
    verts: list[list[float]] = [[x * rx, y * ry, z * z_scale] for z, rx, ry in rows for x, y in outline]
    faces: list[list[int]] = []
    for i in range(len(rows) - 1):
        for j in range(len(angles)):
            a = i * len(angles) + j
            b = i * len(angles) + (j + 1) % len(angles)
            c = a + len(angles)
            d = b + len(angles)
            faces.extend([[a, c, b], [b, c, d]])
    for row in [0, len(rows) - 1]:
        center = len(verts)
        verts.append([0, 0, rows[row][0] * z_scale])
        for j in range(len(angles)):
            triangle = [center, row * len(angles) + j, row * len(angles) + (j + 1) % len(angles)]
            faces.append(triangle if row == 0 else triangle[::-1])
    body = finish(trimesh.Trimesh(verts, faces))
    m.add("Bottle", body, "tint")
    # Thin wraparound paper band follows the rounded body, leaving amber visible at base and shoulders.
    label_rows = [(-0.022, 0.02525, 0.01925), (-0.066, 0.02525, 0.01925)]
    label_verts = np.array([[x * rx, y * ry, z * z_scale] for z, rx, ry in label_rows for x, y in outline])
    label_faces: list[list[int]] = []
    for j in range(len(angles)):
        a = j
        b = (j + 1) % len(angles)
        c = a + len(angles)
        d = b + len(angles)
        label_faces.extend([[a, c, b], [b, c, d]])
    m.add("Label", finish(trimesh.Trimesh(label_verts, label_faces)), "paper")
    # Raised amber identification stripe on the front of the paper band.
    stripe = superellipsoid((0.0035, 0.0006, 0.027 * z_scale), 0.25, (0, 0.0195, -0.044 * z_scale), detail=8)
    m.add("Mark", stripe, "tint")
    collar = along_z(
        lathe(
            [
                (0, 0.107 * z_scale),
                (0.011, 0.107 * z_scale),
                (0.012, 0.109 * z_scale),
                (0.012, 0.122 * z_scale),
                (0.010, 0.124 * z_scale),
                (0, 0.124 * z_scale),
            ],
            32,
        )
    )
    ridges = []
    for angle in np.linspace(0, 2 * np.pi, 16, endpoint=False):
        x, y = 0.012 * np.cos(angle), 0.012 * np.sin(angle)
        ridges.append(superellipsoid((0.0015, 0.0015, 0.011 * z_scale), 0.65, (x, y, -0.1155 * z_scale), detail=6))
    m.add("Collar", merge(collar, *ridges), "plastic")
    # Annular tip is genuinely open at the configured pour point.
    nozzle = along_z(
        lathe(
            [
                (0.006, 0.122 * z_scale),
                (0.0055, 0.127 * z_scale),
                (0.0035, length),
                (0.002, length),
                (0.002, 0.127 * z_scale),
                (0.004, 0.122 * z_scale),
                (0.006, 0.122 * z_scale),
            ],
            32,
        )
    )
    m.add("Spout", nozzle, "plastic")


def _iodine_dish(m: Model, length: float) -> None:
    """Rounded oblong steel basin with a rolled lip and a floor-pivoted iodine pool."""
    sections = 64
    angles = np.linspace(0.0, 2.0 * np.pi, sections, endpoint=False)
    exponent = 3.5
    outline: NDArray[np.float64] = np.column_stack(
        (
            0.039 * np.sign(np.cos(angles)) * np.abs(np.cos(angles)) ** (2.0 / exponent),
            0.048 * np.sign(np.sin(angles)) * np.abs(np.sin(angles)) ** (2.0 / exponent),
        )
    )
    assert Polygon(outline).is_valid
    center_z = -length * 0.5

    def loft(rows: list[tuple[float, float]]) -> trimesh.Trimesh:
        vertices: list[list[float]] = []
        for size, y in rows:
            vertices.extend([[float(x * size), y, float(z * size + center_z)] for x, z in outline])
        faces: list[list[int]] = []
        for row in range(len(rows) - 1):
            for col in range(sections):
                a = row * sections + col
                b = row * sections + (col + 1) % sections
                c, d = a + sections, b + sections
                faces.extend(([a, c, b], [b, c, d]))
        for end, reverse in ((0, False), (len(rows) - 1, True)):
            size, y = rows[end]
            points, triangles = trimesh.creation.triangulate_polygon(Polygon(outline * size), engine="earcut")
            offset = len(vertices)
            vertices.extend([[float(x), y, float(z + center_z)] for x, z in points])
            for triangle in triangles:
                oriented = triangle[::-1] if reverse else triangle
                faces.append([int(index) + offset for index in oriented])
        mesh = finish(trimesh.Trimesh(vertices, faces))
        assert mesh.is_watertight
        return mesh

    # Rounded oval profile: low flat underside, shallow outer wall, rolled rim, and broad interior floor.
    dish_rows = [
        (0.72, -0.012),
        (0.82, -0.011),
        (0.96, 0.005),
        (1.00, 0.0115),
        (1.02, 0.013),
        (1.025, 0.0145),
        (1.015, 0.016),
        (0.99, 0.016),
        (0.97, 0.014),
        (0.94, 0.010),
        (0.83, -0.003),
        (0.72, -0.006),
    ]
    dish = loft(dish_rows)
    m.add("Dish", dish, "steel")

    # ShowLiquid scales this mesh uniformly from its floor pivot, from 60% to full size.
    # The upper surface stays well inside the rounded wall throughout that expansion.
    liquid_rows = [(0.64, -0.006), (0.88, 0.006)]
    liquid = loft(liquid_rows)
    liquid_pivot = (0.0, -0.006, center_z)
    m.add("Liquid", liquid, "iodine", liquid_pivot)

    # Interpolate the actual inner wall contour at each transformed liquid ring height.
    cavity = [(0.72, -0.006), (0.83, -0.003), (0.94, 0.010), (0.97, 0.014), (0.99, 0.016)]
    cavity_y = np.array([row[1] for row in cavity])
    cavity_size = np.array([row[0] for row in cavity])
    amounts = (0.0, 0.01, 0.25, 0.5, 0.65, 1.0)
    for amount in amounts:
        runtime_scale = 0.6 + 0.4 * amount
        for size, y in liquid_rows:
            runtime_y = liquid_pivot[1] + (y - liquid_pivot[1]) * runtime_scale
            wall_size = float(np.interp(runtime_y, cavity_y, cavity_size))
            liquid_size = size * runtime_scale
            liquid_contour = Polygon(outline * liquid_size)
            cavity_contour = Polygon(outline * wall_size)
            assert cavity_contour.buffer(-1e-9).covers(liquid_contour), (
                f"liquid contour escapes cavity at {amount:.0%} fill, y={runtime_y:.6f}, pool={liquid_size:.5f}, wall={wall_size:.5f}"
            )


def _cig_pack(m: Model, length: float) -> None:
    """Paper pack with a colored band at the top, three filters sticking out."""
    m.add("Pack", superellipsoid((0.055, 0.022, length * 0.85), 0.15, (0.0, 0.0, -length * 0.425)), "paper")
    m.add("Band", superellipsoid((0.056, 0.023, length * 0.3), 0.15, (0.0, 0.0, -length * 0.7)), "tint")
    filters = [cylinder(0.004, (x, 0.0, -length * 0.85), (x, 0.0, -length), 10) for x in (-0.012, 0.0, 0.012)]
    m.add("Filters", merge(*filters), "iodine")


def _tourniquet(m: Model, length: float) -> None:
    """A fabric loop with an open buckle, windlass and retaining hardware."""
    z_scale = length / 0.2

    def scaled_z(mesh: trimesh.Trimesh) -> trimesh.Trimesh:
        return scaled(mesh, (1.0, 1.0, z_scale))

    def mounted_hardware(mesh: trimesh.Trimesh) -> trimesh.Trimesh:
        """Hardware centered at z=-0.14 is mounted at the top by runtime, so keep it compact there."""
        out = scaled_z(mesh)
        out.vertices[:, 0] *= 0.65
        mount_z = -0.14 * z_scale
        out.vertices[:, 2] = mount_z + (out.vertices[:, 2] - mount_z) * 0.65
        return out

    vertices = []
    for angle in np.linspace(0.0, 2 * np.pi, 64, endpoint=False):
        center = np.array([0.038 * np.sin(angle), 0.0, -0.091 + 0.087 * np.cos(angle)])
        normal = np.array([0.087 * np.sin(angle), 0.0, 0.038 * np.cos(angle)])
        normal /= np.linalg.norm(normal)
        for width, height in ((-0.015, -0.002), (0.015, -0.002), (0.015, 0.002), (-0.015, 0.002)):
            vertices.append(center + normal * width + np.array([0.0, height, 0.0]))
    faces: list[list[int]] = []
    for index in range(64):
        for side in range(4):
            a, b = 4 * index + side, 4 * index + (side + 1) % 4
            c, d = 4 * ((index + 1) % 64) + side, 4 * ((index + 1) % 64) + (side + 1) % 4
            faces.extend(([a, c, b], [b, c, d]))
    strap = finish(trimesh.Trimesh(vertices, faces))
    m.add("Strap", scaled_z(strap), "fabric_dark")
    m.add("Tail", scaled_z(superellipsoid((0.029, 0.004, 0.042), 0.25, (0.0, 0.003, -0.181), detail=8)), "fabric_dark")
    m.add("PullTab", mounted_hardware(superellipsoid((0.030, 0.0045, 0.014), 0.25, (0.0, 0.0035, -0.197), detail=8)), "red_plastic")

    m.add("Saddle", mounted_hardware(superellipsoid((0.076, 0.006, 0.052), 0.35, (0.0, 0.009, -0.140), detail=8)), "black_plastic")
    rails = [superellipsoid((0.006, 0.009, 0.031), 0.3, (x, 0.007, -0.170), detail=8) for x in (-0.019, 0.019)]
    rails.extend(superellipsoid((0.043, 0.009, 0.006), 0.3, (0.0, 0.007, z), detail=8) for z in (-0.186, -0.154))
    m.add("Buckle", mounted_hardware(merge(*rails)), "black_plastic", parent="Saddle")
    m.add(
        "TensionBand",
        mounted_hardware(
            tube(
                [(0.0, 0.009, -0.164), (0.0, 0.011, -0.143), (0.0, 0.016, -0.127), (0.0, 0.013, -0.110), (0.0, 0.009, -0.100)],
                [(0.002, 0.008)] * 5,
                ring=8,
                exponent=5,
                smooth=2,
            )
        ),
        "fabric_dark",
        parent="Saddle",
    )
    m.add(
        "Windlass",
        mounted_hardware(
            tube(
                [
                    (-0.048, 0.022, -0.127),
                    (-0.043, 0.022, -0.127),
                    (-0.020, 0.022, -0.127),
                    (0.0, 0.022, -0.127),
                    (0.020, 0.022, -0.127),
                    (0.043, 0.022, -0.127),
                    (0.048, 0.022, -0.127),
                ],
                [(0.004, 0.004), (0.006, 0.006), (0.006, 0.006), (0.0045, 0.0045), (0.006, 0.006), (0.006, 0.006), (0.004, 0.004)],
                ring=12,
                smooth=1,
            )
        ),
        "dark_steel",
        parent="Saddle",
    )
    for x, name in ((-0.028, "RetainerL"), (0.028, "RetainerR")):
        m.add(name, mounted_hardware(superellipsoid((0.009, 0.021, 0.016), 0.3, (x, 0.013, -0.127), detail=8)), "black_plastic", parent="Saddle")
    m.add("Keeper", scaled_z(superellipsoid((0.027, 0.003, 0.014), 0.3, (0.037, 0.003, -0.068), detail=8)), "plastic")


def _gas_mask(m: Model, length: float) -> None:
    """Shaped anesthetic mask with an open shell, hollow connector and hose."""
    sections = 40

    def contour(angle: float) -> NDArray[np.float64]:
        return np.array([0.037 * (1 - 0.22 * np.sin(angle)) * np.cos(angle), 0.050 * np.sin(angle)])

    def cushion() -> trimesh.Trimesh:
        vertices: list[NDArray[np.float64]] = []
        for angle in np.linspace(0.0, 2 * np.pi, sections, endpoint=False):
            center = contour(angle)
            epsilon = 0.00001
            tangent = contour(angle + epsilon) - contour(angle - epsilon)
            tangent /= np.linalg.norm(tangent)
            outward = np.array([tangent[1], -tangent[0]])
            # Include the front contact plane in the sampled cushion profile.
            for around in np.linspace(-np.pi / 2, 3 * np.pi / 2, 10, endpoint=False):
                xy = center + outward * 0.0055 * np.cos(around)
                vertices.append(np.array([xy[0], xy[1], -0.094 + 0.006 * np.sin(around)]))
        faces: list[list[int]] = []
        for index in range(sections):
            for side in range(10):
                a = index * 10 + side
                b = index * 10 + (side + 1) % 10
                c = ((index + 1) % sections) * 10 + side
                d = ((index + 1) % sections) * 10 + (side + 1) % 10
                faces.extend(([a, c, b], [b, c, d]))
        return finish(trimesh.Trimesh(vertices, faces))

    def shell() -> trimesh.Trimesh:
        rows = [(0.0, -0.029), (0.25, -0.038), (0.58, -0.060), (0.87, -0.080), (1.0, -0.092)]
        vertices: list[list[float]] = []
        for inner in (False, True):
            for fraction, z in rows:
                for angle in np.linspace(0.0, 2 * np.pi, sections, endpoint=False):
                    circle = np.array([np.cos(angle), np.sin(angle)]) * 0.0105
                    xy = circle * (1 - fraction) + contour(angle) * fraction * 0.98
                    if inner:
                        xy -= xy / np.linalg.norm(xy) * 0.0015
                    vertices.append([float(xy[0]), float(xy[1]), z])
        faces: list[list[int]] = []
        count = len(rows) * sections
        for side in range(2):
            start = side * count
            for row in range(len(rows) - 1):
                for col in range(sections):
                    a = start + row * sections + col
                    b = start + row * sections + (col + 1) % sections
                    c, d = a + sections, b + sections
                    triangles = [[a, c, b], [b, c, d]]
                    faces.extend(triangles if side == 0 else [triangle[::-1] for triangle in triangles])
        for row in (0, len(rows) - 1):
            for col in range(sections):
                a = row * sections + col
                b = row * sections + (col + 1) % sections
                c, d = a + count, b + count
                caps = [[a, b, c], [b, d, c]]
                faces.extend(caps if row == 0 else [triangle[::-1] for triangle in caps])
        return finish(trimesh.Trimesh(vertices, faces))

    def hollow_tube(path: list[tuple[float, float, float]], radius: float, wall: float, ring: int = 12) -> trimesh.Trimesh:
        points: list[NDArray[np.float64]] = []
        for index in range(len(path) - 1):
            for fraction in np.linspace(0.0, 1.0, 3, endpoint=False):
                points.append(np.array(path[index]) * (1 - fraction) + np.array(path[index + 1]) * fraction)
        points.append(np.array(path[-1]))
        vertices: list[NDArray[np.float64]] = []
        for tube_radius in (radius, radius - wall):
            for index, point in enumerate(points):
                tangent = points[min(index + 1, len(points) - 1)] - points[max(index - 1, 0)]
                tangent /= np.linalg.norm(tangent)
                first = np.array([1.0, 0.0, 0.0])
                second = np.cross(tangent, first)
                for angle in np.linspace(0.0, 2 * np.pi, ring, endpoint=False):
                    vertices.append(point + first * tube_radius * np.cos(angle) + second * tube_radius * np.sin(angle))
        faces: list[list[int]] = []
        count = len(points) * ring
        for side in range(2):
            offset = side * count
            for index in range(len(points) - 1):
                for section in range(ring):
                    a = offset + index * ring + section
                    b = offset + index * ring + (section + 1) % ring
                    c, d = a + ring, b + ring
                    triangles = [[a, c, b], [b, c, d]]
                    faces.extend(triangles if side == 0 else [triangle[::-1] for triangle in triangles])
        for index in (0, len(points) - 1):
            for section in range(ring):
                a = index * ring + section
                b = index * ring + (section + 1) % ring
                c, d = a + count, b + count
                caps = [[a, b, c], [b, d, c]]
                faces.extend(caps if index == 0 else [triangle[::-1] for triangle in caps])
        return finish(trimesh.Trimesh(vertices, faces))

    z_scale = length / 0.1
    parts = (
        ("Cushion", cushion(), "plastic"),
        ("Cup", shell(), "frosted_plastic"),
        ("Connector", hollow_tube([(0, 0, -0.032), (0, 0, -0.010)], 0.012, 0.002), "blue_plastic"),
        ("Elbow", hollow_tube([(0, 0, -0.014), (0, 0, 0), (0, -0.004, 0.012), (0, -0.014, 0.020), (0, -0.027, 0.022)], 0.0095, 0.0018), "plastic"),
        (
            "Hose",
            hollow_tube(
                [(0, -0.023, 0.022), (0, -0.041, 0.022), (0, -0.055, 0.032), (0, -0.060, 0.055), (0, -0.060, 0.075)],
                0.008,
                0.0016,
            ),
            "blue_plastic",
        ),
        ("HoseCollar", hollow_tube([(0, -0.023, 0.022), (0, -0.032, 0.022)], 0.010, 0.0022), "blue_plastic"),
    )
    for name, mesh, material_name in parts:
        m.add(name, scaled(mesh, (1.0, 1.0, z_scale)), material_name)


def _kidney_dish(m: Model, length: float) -> None:
    """Kidney-shaped basin with a rolled lip and a fillable pool."""
    sections = 64
    angles = np.linspace(0.0, 2 * np.pi, sections, endpoint=False)
    outline: NDArray[np.float64] = np.column_stack(
        (
            1.15 * (0.049 * np.cos(angles) - 0.035 * np.exp(-((np.sin(angles) / 0.50) ** 2)) * np.maximum(np.cos(angles), 0) ** 2),
            0.100 * np.sin(angles),
        )
    )
    outline_polygon = Polygon(outline)
    assert outline_polygon.is_valid

    def loft(rows: list[tuple[float, float]]) -> trimesh.Trimesh:
        """Join matching contours, closing only the two floor faces to leave the basin cavity open."""
        vertices: list[list[float]] = []
        for size, y in rows:
            vertices.extend([[float(x * size), y, float(z * size - 0.10)] for x, z in outline])
        faces: list[list[int]] = []
        for row in range(len(rows) - 1):
            for col in range(sections):
                a = row * sections + col
                b = row * sections + (col + 1) % sections
                c, d = a + sections, b + sections
                faces.extend(([a, c, b], [b, c, d]))
        for end, reverse in ((0, False), (len(rows) - 1, True)):
            size, y = rows[end]
            points, triangles = trimesh.creation.triangulate_polygon(Polygon(outline * size), engine="earcut")
            offset = len(vertices)
            vertices.extend([[float(x), y, float(z - 0.10)] for x, z in points])
            for triangle in triangles:
                oriented = triangle[::-1] if reverse else triangle
                faces.append([int(index) + offset for index in oriented])
        mesh = finish(trimesh.Trimesh(vertices, faces))
        assert mesh.is_watertight
        return mesh

    # Underside, rising outer wall, rolled lip, inner wall and the broad flat basin floor.
    dish = loft(
        [
            (0.82, -0.010),
            (0.86, -0.008),
            (0.985, 0.013),
            (0.999, 0.0155),
            (1.002, 0.017),
            (0.996, 0.0185),
            (0.982, 0.019),
            (0.966, 0.018),
            (0.956, 0.015),
            (0.83, -0.005),
            (0.70, -0.005),
        ]
    )
    z_scale = length / 0.2
    m.add("Dish", scaled(dish, (1.0, 1.0, z_scale)), "steel")

    # ShowLiquid scales X/Z from 0.88 to 1 and raises the pool from the flat floor.
    # Leave a small clearance at the wall, including after Godot mesh import and collision processing.
    pool = loft([(0.82, -0.005), (0.925, 0.012), (0.84, 0.012)])
    pool_pivot = (0.0, -0.005, -length * 0.5)
    m.add("Pool", scaled(pool, (1.0, 1.0, z_scale)), "drug", pool_pivot)

    # Check that the liquid contour stays inside the tapered basin throughout its fill animation.
    for amount in (0.01, 0.25, 0.5, 0.65, 1.0):
        top_factor = 0.925 * (0.88 + 0.12 * amount)
        wall_factor = 0.83 + (0.956 - 0.83) * (0.017 * amount / 0.020)
        assert top_factor < wall_factor
        assert Polygon(outline * wall_factor).buffer(1e-9).covers(Polygon(outline * top_factor))


def _iv_catheter(m: Model, length: float) -> None:
    """Tapered peripheral catheter with wings, capped port, flash chamber and introducer."""
    z_scale = length / 0.07

    def scaled_z(mesh: trimesh.Trimesh) -> trimesh.Trimesh:
        return scaled(mesh, (1.0, 1.0, z_scale))

    hub = tube(
        [(0.0, 0.0, -0.006), (0.0, 0.0, -0.012), (0.0, 0.0, -0.027), (0.0, 0.0, -0.036)],
        [(0.0045, 0.0045), (0.0045, 0.0045), (0.0035, 0.0035), (0.0020, 0.0020)],
        ring=16,
        smooth=2,
    )
    m.add("Hub", scaled_z(hub), "plastic")

    wings = [superellipsoid((0.014, 0.002, 0.012), 0.35, (0.0, -0.003, -0.023), detail=8)]
    for side in (-1.0, 1.0):
        wings.append(superellipsoid((0.016, 0.0018, 0.015), 0.35, (side * 0.0105, -0.003, -0.023), detail=8))
    m.add("Wings", scaled_z(merge(*wings)), "plastic")
    m.add("Port", scaled_z(cylinder(0.0032, (0.0, 0.001, -0.021), (0.0, 0.009, -0.021), 16)), "plastic")
    m.add(
        "PortCap",
        scaled_z(
            tube(
                [(0.0, 0.008, -0.021), (0.0, 0.009, -0.021), (0.0, 0.012, -0.021)],
                [(0.0035, 0.0035), (0.0047, 0.0047), (0.0042, 0.0042)],
                ring=16,
                smooth=1,
            )
        ),
        "yellow_plastic",
    )
    m.add(
        "FlashChamber",
        scaled_z(
            tube(
                [(0.0, 0.0, -0.007), (0.0, 0.0, 0.014), (0.0, 0.0, 0.018)],
                [(0.0038, 0.0038), (0.0038, 0.0038), (0.0032, 0.0032)],
                ring=16,
                smooth=1,
            )
        ),
        "frosted_plastic",
    )
    m.add("BackCap", scaled_z(cylinder(0.0043, (0.0, 0.0, 0.017), (0.0, 0.0, 0.021), 16)), "plastic")
    m.add(
        "Catheter",
        scaled_z(
            tube(
                [(0.0, 0.0, -0.034), (0.0, 0.0, -0.040), (0.0, 0.0, -0.065), (0.0, 0.0, -0.067)],
                [(0.0013, 0.0013), (0.0010, 0.0010), (0.00085, 0.00085), (0.0007, 0.0007)],
                ring=12,
                smooth=1,
            )
        ),
        "plastic",
    )
    needle = along_z(lathe([(0.0, 0.0), (0.00052, 0.0), (0.00052, 0.032), (0.00008, 0.0348), (0.0, 0.035)], 12), -0.035)
    m.add("Needle", scaled_z(needle), "chrome")


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
        _gas_mask(m, length)
    elif kind == "cocaine":
        m.add("Bag", superellipsoid((0.04, 0.006, length), 0.4, (0.0, 0.0, -length * 0.5)), "clear_plastic")
        m.add("Powder", superellipsoid((0.03, 0.005, length * 0.6), 0.6, (0.0, -0.001, -length * 0.55)), "cotton")
    elif kind == "tourniquet":
        _tourniquet(m, length)
    elif kind == "iv_catheter":
        _iv_catheter(m, length)
    elif kind == "skin_graft":
        m.add("Backing", superellipsoid((0.08, 0.002, length), 0.2, (0.0, -0.002, -length * 0.5)), "paper")
        m.add("Graft", superellipsoid((0.07, 0.0015, length * 0.9), 0.25, (0.0, 0.0005, -length * 0.5)), "skin")
    elif kind == "surgical_cap":
        m.add("Cap", superellipsoid((0.1, 0.06, 0.11), 0.7, (0.0, 0.0, -length * 0.5)), "tint")
    elif kind == "kidney_dish":
        _kidney_dish(m, length)


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
        "marker": lambda m, length: _pen(m, length, "tint", "black_plastic"),
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
