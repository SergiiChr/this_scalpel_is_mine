"""Every grabbable tool. Grip at the origin, working tip at (0, 0, -length) with length from data/tools.cfg.

Moving parts are separate nodes the game animates (src/tools/tool_animator.gd):
JawA/JawB open and close, Plunger slides, Trigger squeezes, Blade oscillates, Flame/Glow/Light show while in use.
Level is liquid the game stretches along Z from its node origin by how full the tool is (syringe, vial).
"""

from __future__ import annotations

import re
from collections.abc import Callable
from functools import partial
from pathlib import Path

import numpy as np
import trimesh

from .geometry import Model, cylinder, ellipsoid, extrude, lathe, merge, moved, rotated, scaled, superellipsoid, torus, tube

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
    handle_len = length * 0.62
    handle = superellipsoid((0.011, 0.004, handle_len), 0.3, (0.0, 0.0, handle_len * 0.5 - 0.035))
    grip = merge(*[superellipsoid((0.012, 0.005, 0.003), 0.4, (0.0, 0.0, z)) for z in np.linspace(-0.02, 0.02, 6)])
    m.add("Handle", merge(handle, grip), "black_plastic" if dull else "steel")
    start = -0.035
    edge = [(start, 0.004), (start - 0.012, 0.006), (-length + 0.015, 0.004), (-length, -0.001), (-length + 0.02, -0.006), (start, -0.004)]
    blade = extrude([(z, y) for z, y in edge], 0.0012)
    m.add("Blade", rotated(rotated(blade, np.pi / 2, Y), 0.0, Z), "dark_steel" if dull else "chrome")


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


def _syringe(m: Model, length: float, radius: float) -> None:
    """Glass barrel with a line all the way round every tenth of its volume, heavier at the half and the full, so it
    reads from any side. Built empty: the game pulls the plunger back as it fills."""
    barrel_len = length * 0.62
    front = -barrel_len + 0.002
    travel = barrel_len * 0.85
    barrel = lathe([(0.0, 0.0), (radius, 0.0), (radius, barrel_len), (0.0, barrel_len)], 20)
    m.add("Barrel", along_z(barrel), "glass")
    m.add("Level", rod(radius * 0.9, front, front + travel), "drug", (0.0, 0.0, front))
    ticks = []
    for i in range(1, 11):
        half = 0.0006 if i % 5 == 0 else 0.0003
        z = front + travel * i / 10
        ticks.append(rod(radius + 0.0003, z - half, z + half, sections=20))
    m.add("Marks", merge(*ticks), "marks")
    m.add("Flange", superellipsoid((radius * 2.0 + 0.012, 0.003, 0.008), 0.4, (0.0, 0.0, 0.0)), "clear_plastic")
    stopper = rod(radius * 0.95, front + 0.004, front)
    stem = rod(radius * 0.3, front + 0.004, 0.012)
    thumb = superellipsoid((radius * 3.0, radius * 3.0, 0.003), 0.5, (0.0, 0.0, 0.014))
    m.add("Plunger", merge(stopper, stem, thumb), "rubber")
    m.add("Needle", merge(rod(0.0022, -barrel_len, -barrel_len - 0.006), rod(0.0005, -barrel_len - 0.006, -length)), "chrome")


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
        "syringe_3": partial(_syringe, radius=0.0045),
        "syringe_10": partial(_syringe, radius=0.0065),
        "syringe_50": partial(_syringe, radius=0.013),
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
