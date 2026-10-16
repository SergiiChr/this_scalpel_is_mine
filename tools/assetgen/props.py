"""Room furniture and stations. Origin on the floor under the prop's center unless noted."""

from __future__ import annotations

from itertools import pairwise
from pathlib import Path

import numpy as np
import trimesh

from .geometry import Model, cylinder, ellipsoid, lathe, merge, moved, superellipsoid, torus, tube

TABLE_HEIGHT = 0.85
# The rail across the table's head end that the patient card hangs on: its hook (CardHook in src/World/Room.cs) goes
# over the bar's top.
HEAD_RAIL_X = 1.025
HEAD_RAIL_TOP = 0.939
# Small trays on the instrument tray for scalpel and forceps, and for cotton pads: corner x, z, width, depth from the
# tray's middle. Same as "instruments" and "swabs" in Room.TrayZones (src/World/Room.cs).
SMALL_TRAYS = ((0.09, -0.36, 0.16, 0.26), (0.14, -0.07, 0.06, 0.06))
# Hand-made models some props are built from. The .gdignore there keeps Godot from importing them as game assets.
SOURCES = Path(__file__).resolve().parent / "sources"
# glove_box.glb node -> part and palette material. Every other node is a glove.
GLOVE_BOX_PARTS = {
    "Box_Cardboard": ("Box", "carton"),
    "Printed_Blue_Edge_Trim": ("Print", "carton_print"),
    "Inset_Dark_Slot": ("Slot", "black_plastic"),
}


def _wheels(radius: float, spread: float, y: float = 0.0) -> trimesh.Trimesh:
    parts = []
    for i in range(5):
        a = i * 2 * np.pi / 5
        end = (spread * np.cos(a), y + 0.04, spread * np.sin(a))
        parts.append(cylinder(0.012, (0.0, y + 0.06, 0.0), end, 8))
        parts.append(cylinder(radius, (end[0] - 0.01, y + radius, end[2]), (end[0] + 0.01, y + radius, end[2]), 12))
    return merge(*parts)


def _operating_table() -> Model:
    m = Model("props", "operating_table")
    m.add("Base", superellipsoid((0.7, 0.08, 0.45), 0.25, (0.0, 0.04, 0.0)), "dark_steel")
    m.add("Column", superellipsoid((0.22, TABLE_HEIGHT - 0.2, 0.16), 0.3, (0.0, (TABLE_HEIGHT - 0.2) / 2 + 0.08, 0.0)), "steel")
    # Runs from x -1.3 (feet) to 1.0 (head), long enough for the whole patient to lie on it.
    m.add("Frame", superellipsoid((2.3, 0.05, 0.56), 0.15, (-0.15, TABLE_HEIGHT - 0.075, 0.0)), "steel")
    pads = [superellipsoid((length, 0.06, 0.56), 0.35, (x, TABLE_HEIGHT - 0.03, 0.0)) for x, length in ((-0.85, 0.88), (0.0, 0.8), (0.62, 0.42), (0.92, 0.16))]
    m.add("Pads", merge(*pads), "mattress")
    low = TABLE_HEIGHT - 0.07
    rails = [cylinder(0.008, (-1.25, low, s * 0.3), (0.95, low, s * 0.3), 8) for s in (-1, 1)]
    # The side rails carry on round the head end and up into a bar above the pads.
    bends = [(0.95, low, -0.3), (HEAD_RAIL_X, low, -0.3), (HEAD_RAIL_X, HEAD_RAIL_TOP, -0.3)]
    bends += [(x, y, -z) for x, y, z in reversed(bends)]
    rails += [cylinder(0.008, a, b, 8) for a, b in pairwise(bends)]
    rails += [ellipsoid((0.008, 0.008, 0.008), bend, 1) for bend in bends[1:-1]]
    m.add("Rails", merge(*rails), "chrome")
    m.add("Pedals", merge(*[superellipsoid((0.08, 0.02, 0.05), 0.4, (x, 0.09, 0.25)) for x in (-0.12, 0.12)]), "black_plastic")
    return m


def _instrument_tray() -> Model:
    m = Model("props", "instrument_tray")
    m.add("Base", _wheels(0.025, 0.3), "dark_steel")
    m.add("Pole", cylinder(0.02, (0.0, 0.05, 0.0), (0.0, 0.88, 0.0)), "steel")
    tray = superellipsoid((0.7, 0.02, 0.8), 0.12, (0.0, 0.895, 0.0))
    rim = merge(
        *[
            superellipsoid(size, 0.2, c)
            for size, c in (
                ((0.72, 0.03, 0.015), (0.0, 0.91, 0.4)),
                ((0.72, 0.03, 0.015), (0.0, 0.91, -0.4)),
                ((0.015, 0.03, 0.8), (0.35, 0.91, 0.0)),
                ((0.015, 0.03, 0.8), (-0.35, 0.91, 0.0)),
            )
        ]
    )
    m.add("Tray", merge(tray, rim), "steel")
    m.add("Drape", superellipsoid((0.66, 0.004, 0.76), 0.15, (0.0, 0.907, 0.0)), "gown")
    m.add("SmallTrays", merge(*[_small_tray(*area) for area in SMALL_TRAYS]), "steel")
    return m


def _small_tray(x: float, z: float, width: float, depth: float) -> trimesh.Trimesh:
    """A shallow steel tray on the drape, its corner at (x, z). Floor top 4 mm and rim 16 mm above the surface."""
    cx, cz = x + width / 2, z + depth / 2
    floor = superellipsoid((width, 0.006, depth), 0.2, (cx, 0.916, cz))
    walls = [
        superellipsoid(size, 0.2, c)
        for size, c in (
            ((width, 0.018, 0.006), (cx, 0.922, z + 0.003)),
            ((width, 0.018, 0.006), (cx, 0.922, z + depth - 0.003)),
            ((0.006, 0.018, depth), (x + 0.003, 0.922, cz)),
            ((0.006, 0.018, depth), (x + width - 0.003, 0.922, cz)),
        )
    ]
    return merge(floor, *walls)


def _iv_stand() -> Model:
    m = Model("props", "iv")
    m.add("Base", _wheels(0.022, 0.25), "dark_steel")
    m.add("Pole", cylinder(0.012, (0.0, 0.05, 0.0), (0.0, 1.95, 0.0)), "chrome")
    hooks = [tube([(0.0, 1.9, 0.0), (s * 0.08, 1.93, 0.0), (s * 0.1, 1.88, 0.0)], [(0.004, 0.004)] * 3, ring=8) for s in (-1, 1)]
    m.add("Hooks", merge(*hooks), "chrome")
    # The bag itself is a tool hung on the hook (iv_drip in data/tools.cfg), so a syringe can go into it.
    m.add("Chamber", merge(cylinder(0.008, (0.08, 1.66, 0.0), (0.08, 1.6, 0.0), 12), ellipsoid((0.004, 0.005, 0.004), (0.08, 1.63, 0.0))), "clear_plastic")
    # The tubing itself is drawn by the game (src/World/IvLine.cs), from the chamber to wherever the line goes in.
    return m


def _shelf() -> Model:
    m = Model("props", "manual")
    m.add("Board", superellipsoid((0.8, 0.03, 0.3), 0.15, (0.0, 1.2, 0.0)), "wood")
    brackets = [
        merge(superellipsoid((0.02, 0.25, 0.02), 0.2, (x, 1.07, -0.13)), superellipsoid((0.02, 0.02, 0.26), 0.2, (x, 1.18, 0.0))) for x in (-0.33, 0.33)
    ]
    m.add("Brackets", merge(*brackets), "dark_steel")
    books = []
    rng = np.random.default_rng(4)
    x = -0.36
    for _ in range(9):
        w = rng.uniform(0.025, 0.045)
        h = rng.uniform(0.2, 0.27)
        books.append(superellipsoid((w, h, 0.18), 0.2, (x + w / 2, 1.215 + h / 2, 0.0)))
        x += w + 0.003
    m.add("Books", merge(*books), "leather")
    m.add("Manual", superellipsoid((0.05, 0.28, 0.2), 0.2, (0.2, 1.355, 0.0)), "red_plastic")
    m.add("Spine", superellipsoid((0.052, 0.04, 0.2), 0.2, (0.2, 1.42, 0.0)), "gold")
    return m


def _clipboard() -> Model:
    m = Model("props", "card")
    board = superellipsoid((0.23, 0.32, 0.008), 0.2, (0.0, 0.75, 0.02))
    m.add("Board", board, "wood")
    m.add("Paper", superellipsoid((0.21, 0.28, 0.002), 0.1, (0.0, 0.735, 0.026)), "paper")
    m.add("Clip", merge(superellipsoid((0.08, 0.03, 0.012), 0.3, (0.0, 0.9, 0.028)), cylinder(0.004, (-0.03, 0.915, 0.036), (0.03, 0.915, 0.036), 8)), "chrome")
    m.add("Hook", tube([(0.0, 0.91, 0.02), (0.0, 0.95, 0.0), (0.0, 0.95, -0.03), (0.0, 0.9, -0.04)], [(0.003, 0.003)] * 4, ring=8), "chrome")
    return m


def _cabinet(m: Model) -> None:
    m.add("Cabinet", superellipsoid((0.6, 0.9, 0.45), 0.04, (0.0, 0.45, 0.0)), "steel")
    m.add("Doors", merge(*[superellipsoid((0.27, 0.7, 0.01), 0.1, (x, 0.42, 0.226)) for x in (-0.145, 0.145)]), "dark_steel")
    m.add("Handles", merge(*[cylinder(0.006, (x, 0.5, 0.24), (x, 0.62, 0.24), 8) for x in (-0.03, 0.03)]), "chrome")


def _bell() -> Model:
    m = Model("props", "bell")
    _cabinet(m)
    m.add("BellBase", cylinder(0.05, (0.0, 0.9, 0.0), (0.0, 0.915, 0.0), 24), "wood")
    dome = lathe([(0.0, 0.0), (0.042, 0.0), (0.04, 0.02), (0.03, 0.038), (0.012, 0.046), (0.0, 0.047)], 24)
    m.add("Dome", moved(dome, (0.0, 0.915, 0.0)), "brass")
    m.add("Striker", cylinder(0.004, (0.0, 0.962, 0.0), (0.0, 0.985, 0.0), 8), "brass", (0.0, 0.962, 0.0))
    return m


def _gloves() -> Model:
    """Cabinet with a dispenser box of gloves on top and a loose pair in front of it, from a hand-made source model."""
    m = Model("props", "gloves")
    _cabinet(m)
    source = trimesh.load_scene(SOURCES / "glove_box.glb")
    # The source is Z up with the loose pair toward -Y: stand it Y up with the pair toward the doors (+Z), centered on the cabinet's top.
    place = trimesh.transformations.translation_matrix((0.0, 0.9, -0.12)) @ trimesh.transformations.rotation_matrix(-np.pi / 2, (1.0, 0.0, 0.0))
    parts: dict[tuple[str, str], list[trimesh.Trimesh]] = {}
    for node in source.graph.nodes_geometry:
        transform, geometry = source.graph[node]
        part = GLOVE_BOX_PARTS.get(node, ("Gloves", "glove"))
        parts.setdefault(part, []).append(source.geometry[geometry].copy().apply_transform(place @ transform))
    for (name, material), meshes in parts.items():
        m.add(name, merge(*meshes), material)
    return m


def _sanitizer() -> Model:
    m = Model("props", "sanitizer")
    _cabinet(m)
    basin = lathe([(0.0, 0.0), (0.15, 0.0), (0.17, 0.12), (0.16, 0.12), (0.14, 0.01), (0.0, 0.01)], 32)
    m.add("Basin", moved(basin, (0.0, 0.9, 0.0)), "steel")
    m.add("Alcohol", cylinder(0.145, (0.0, 0.91, 0.0), (0.0, 0.98, 0.0), 32), "clear_plastic")
    bottle = lathe([(0.0, 0.0), (0.035, 0.0), (0.035, 0.16), (0.012, 0.19), (0.012, 0.22), (0.0, 0.22)], 16)
    m.add("Bottle", moved(bottle, (0.2, 0.9, 0.1)), "blue_plastic")
    return m


def _lamp() -> Model:
    """Ceiling mounted surgical light. Origin at the lamp head's center."""
    m = Model("props", "surgical_lamp")
    m.add("Mount", merge(cylinder(0.02, (0.0, 0.08, 0.0), (0.0, 0.7, 0.0)), cylinder(0.05, (0.0, 0.7, 0.0), (0.0, 0.76, 0.0), 20)), "steel")
    head = lathe([(0.0, 0.08), (0.08, 0.08), (0.3, 0.0), (0.32, -0.03), (0.0, -0.03)], 40)
    m.add("Head", head, "plastic")
    m.add("Lens", cylinder(0.28, (0.0, -0.031, 0.0), (0.0, -0.035, 0.0), 40), "flame")
    m.add("Handle", cylinder(0.02, (0.0, -0.03, 0.0), (0.0, -0.12, 0.0), 12), "green_plastic")
    return m


def _monitor() -> Model:
    """Bedside monitor casing, origin at the screen center, screen facing +Z."""
    m = Model("props", "monitor")
    m.add("Casing", superellipsoid((0.46, 0.34, 0.08), 0.2, (0.0, 0.0, -0.04)), "plastic")
    m.add("Screen", superellipsoid((0.4, 0.27, 0.01), 0.1, (0.0, 0.01, 0.0)), "screen")
    m.add("Knobs", merge(*[cylinder(0.01, (0.2, y, 0.0), (0.2, y, 0.012), 12) for y in (-0.08, -0.12)]), "black_plastic")
    m.add("Arm", merge(cylinder(0.015, (0.0, -0.17, -0.06), (0.0, -0.4, -0.2)), superellipsoid((0.08, 0.02, 0.08), 0.3, (0.0, -0.41, -0.2))), "dark_steel")
    return m


# Mobile X-ray unit, front (-Z) toward the table. The arm slides straight up and down the column, level, and carries the
# tube head; the game moves Arm along Y, its node origin at the tube head's. Same as XrayCart (src/World/XrayCart.cs).
XRAY_HEAD = (0.0, 1.6, -0.72)
# The column stands left of the middle, so a player pushing the cart sees past it.
XRAY_COLUMN = (-0.17, -0.03)
# How far the arm runs above the tube head's origin.
XRAY_ARM_ABOVE = 0.24


def _block(size: tuple[float, float, float], center: tuple[float, float, float]) -> trimesh.Trimesh:
    """A box with just its edges eased: sheet metal and steel section, not moulded plastic.
    Six rings already keep the eased edges, and the cart is made of many of these."""
    return superellipsoid(size, 0.06, center, detail=6)


def _beam(a: tuple[float, float, float], b: tuple[float, float, float], width: float, height: float) -> trimesh.Trimesh:
    """A level rectangular steel section from a to b."""
    va, vb = np.asarray(a, float), np.asarray(b, float)
    run = vb - va
    beam = _block((width, height, float(np.linalg.norm(run))), (0.0, 0.0, 0.0))
    beam.apply_transform(trimesh.transformations.rotation_matrix(float(np.arctan2(run[0], run[2])), (0.0, 1.0, 0.0)))
    beam.apply_translation((va + vb) / 2)
    return beam


def _caster(x: float, z: float) -> list[trimesh.Trimesh]:
    """A swivel caster under a rail end: its plate, the fork either side of the wheel and the stem, steel."""
    return [
        _block((0.06, 0.008, 0.06), (x, 0.096, z)),
        cylinder(0.012, (x, 0.092, z), (x, 0.075, z), 10),
        _block((0.005, 0.05, 0.04), (x - 0.022, 0.06, z + 0.012)),
        _block((0.005, 0.05, 0.04), (x + 0.022, 0.06, z + 0.012)),
        cylinder(0.006, (x - 0.026, 0.045, z + 0.02), (x + 0.026, 0.045, z + 0.02), 8),
    ]


def _xray() -> Model:
    """Mobile X-ray unit, industrial: an open H frame of square steel on swivel casters, a square column at its front left
    braced by a gas strut, and a level steel arm from a carriage on the column to the box-shaped tube head, which hangs
    from a yoke. The control panel is on the head's back, toward the pusher; the push bar and the cassette cabinet are at
    the back.
    """
    m = Model("props", "xray")
    rails = [_block((0.06, 0.05, 0.78), (x, 0.125, 0.02)) for x in (-0.24, 0.24)]
    cross = [_block((0.42, 0.045, 0.05), (0.0, 0.125, z)) for z in (-0.03, 0.3)]
    # The column's foot: a plate on the front cross bar, and gussets either side.
    foot = _block((0.12, 0.012, 0.12), (XRAY_COLUMN[0], 0.155, XRAY_COLUMN[1]))
    gussets = [_block((0.008, 0.12, 0.1), (XRAY_COLUMN[0] + s * 0.045, 0.21, XRAY_COLUMN[1])) for s in (-1, 1)]
    frame = [*rails, *cross, foot, *gussets]
    casters = [_caster(x, z) for x in (-0.24, 0.24) for z in (-0.33, 0.37)]
    forks = [part for caster in casters for part in caster]
    wheels = [cylinder(0.045, (x - 0.014, 0.045, z + 0.02), (x + 0.014, 0.045, z + 0.02), 20) for x in (-0.24, 0.24) for z in (-0.33, 0.37)]
    m.add("Wheels", merge(*wheels), "rubber")
    cx, cz = XRAY_COLUMN
    # Tall enough that the carriage at its highest still clamps it.
    frame.append(_block((0.08, 1.96, 0.08), (cx, 1.14, cz)))
    # The adjustment holes up the column's side, and the lock lever on its slot.
    holes = [cylinder(0.008, (cx + 0.039, y, cz), (cx + 0.042, y, cz), 10) for y in (0.55, 0.75, 0.95, 1.15, 1.35)]
    lever = merge(
        _block((0.03, 0.08, 0.03), (cx + 0.055, 0.85, cz)),
        cylinder(0.006, (cx + 0.07, 0.86, cz), (cx + 0.12, 0.9, cz), 8),
        superellipsoid((0.03, 0.018, 0.018), 0.4, (cx + 0.125, 0.905, cz)),
    )
    fittings = [*holes, lever]
    # The gas strut bracing the column to the left rail, behind it: its cylinder, rod and the teal collar.
    strut_from, strut_to = (-0.24, 0.17, 0.25), (cx, 0.55, cz + 0.05)
    mid = tuple(a + (b - a) * 0.45 for a, b in zip(strut_from, strut_to, strict=True))
    fittings += [cylinder(0.018, strut_from, mid, 12), cylinder(0.008, mid, strut_to, 10)]
    collar_at = tuple(a + (b - a) * 0.3 for a, b in zip(strut_from, strut_to, strict=True))
    collar_to = tuple(a + (b - a) * 0.36 for a, b in zip(strut_from, strut_to, strict=True))
    m.add("Collar", cylinder(0.021, collar_at, collar_to, 12), "blue_plastic")
    # The cassette cabinet between the rails at the back, with vents on its sides, and the holder on top.
    cabinet = _block((0.36, 0.56, 0.24), (0.03, 0.43, 0.18))
    vents = [_block((0.004, 0.012, 0.16), (0.03 + s * 0.181, y, 0.18)) for s in (-1, 1) for y in (0.3, 0.34, 0.38, 0.42)]
    frame.append(cabinet)
    fittings += [*vents, _block((0.34, 0.24, 0.1), (0.0, 0.83, 0.22)), _block((0.36, 0.02, 0.12), (0.0, 0.72, 0.22))]
    # The push bar behind the cabinet: two posts up from the rails' ends and a cross bar with black grips at its ends.
    posts = [_block((0.03, 0.77, 0.03), (x, 0.535, 0.38)) for x in (-0.24, 0.24)]
    bar = cylinder(0.014, (-0.32, 0.92, 0.38), (0.32, 0.92, 0.38), 12)
    forks += [*posts, bar]
    grips = [cylinder(0.02, (s * 0.255, 0.92, 0.38), (s * 0.32, 0.92, 0.38), 14) for s in (-1, 1)]
    caps = [cylinder(0.022, (s * 0.32, 0.92, 0.38), (s * 0.33, 0.92, 0.38), 14) for s in (-1, 1)]
    m.add("Grips", merge(*grips), "black_plastic")
    # Few parts, each of one material: every one is a draw, and lit up while the cart is looked at, a change.
    m.add("Frame", merge(*frame, *caps), "plastic")
    m.add("Fittings", merge(*fittings), "dark_steel")
    m.add("Steel", merge(*forks), "steel")
    # The floor lock: a pedal behind the cabinet, pressed down while the cart is locked.
    m.add("Pedal", _block((0.08, 0.025, 0.06), (0.12, 0.17, 0.36)), "red_plastic", (0.12, 0.17, 0.32))
    hx, hy, hz = XRAY_HEAD
    # The arm: a carriage clamped round the column, with the crank that winds it on its side, and a straight steel beam
    # from it to over the tube head, which hangs from a yoke at its end. The screen on the head's back stays clear.
    ay = hy + XRAY_ARM_ABOVE
    carriage = merge(
        _block((0.14, 0.22, 0.14), (cx, ay - 0.02, cz)),
        _block((0.15, 0.02, 0.15), (cx, ay - 0.13, cz)),
        cylinder(0.03, (cx + 0.07, ay - 0.05, cz), (cx + 0.095, ay - 0.05, cz), 16),
    )
    crank = merge(
        _block((0.012, 0.11, 0.02), (cx + 0.1, ay - 0.1, cz)),
        cylinder(0.006, (cx + 0.1, ay - 0.15, cz), (cx + 0.13, ay - 0.15, cz), 8),
    )
    beam = _beam((cx, ay, cz), (hx, ay, hz - 0.02), 0.06, 0.08)
    yoke = merge(
        _block((0.12, 0.03, 0.12), (hx, ay - 0.05, hz)),
        _block((0.02, 0.24, 0.07), (hx + 0.175, hy + 0.1, hz)),
        _block((0.2, 0.02, 0.07), (hx + 0.085, ay - 0.05, hz)),
    )
    m.add("Arm", merge(carriage, beam, yoke, crank), "dark_steel", XRAY_HEAD)
    m.add("Knob", superellipsoid((0.035, 0.022, 0.022), 0.4, (cx + 0.15, ay - 0.15, cz), detail=8), "plastic", XRAY_HEAD, "Arm")
    m.add(
        "Pivot",
        merge(
            cylinder(0.04, (hx + 0.16, hy + 0.02, hz), (hx + 0.19, hy + 0.02, hz), 20),
            cylinder(0.015, (hx + 0.19, hy + 0.02, hz), (hx + 0.2, hy + 0.02, hz), 12),
        ),
        "steel",
        XRAY_HEAD,
        "Arm",
    )
    # The tube head: a sheet-metal box with a seam round it, the control panel on its back with its keys, and under it
    # the collimator housing with its two shutter knobs.
    back = hz + 0.13
    keys = [_block((0.022, 0.016, 0.006), (hx - 0.06 + i * 0.03, hy + y, back + 0.006)) for i in range(5) for y in (0.035, 0.005)]
    head = merge(
        _block((0.3, 0.28, 0.26), (hx, hy + 0.03, hz)),
        _block((0.31, 0.012, 0.27), (hx, hy - 0.04, hz)),
        *keys[:-1],
        _block((0.2, 0.08, 0.18), (hx, hy - 0.14, hz)),
        _block((0.22, 0.012, 0.2), (hx, hy - 0.1, hz)),
        cylinder(0.018, (hx - 0.1, hy - 0.14, hz + 0.04), (hx - 0.12, hy - 0.14, hz + 0.04), 12),
        cylinder(0.018, (hx - 0.1, hy - 0.14, hz - 0.04), (hx - 0.12, hy - 0.14, hz - 0.04), 12),
    )
    m.add("Head", head, "plastic", XRAY_HEAD, "Arm")
    m.add("Panel", _block((0.2, 0.2, 0.008), (hx, hy + 0.05, back + 0.002)), "dark_steel", XRAY_HEAD, "Head")
    m.add("Screen", _block((0.12, 0.06, 0.006), (hx, hy + 0.1, back + 0.006)), "screen", XRAY_HEAD, "Head")
    m.add("Expose", keys[-1], "orange_plastic", XRAY_HEAD, "Head")
    m.add("Badge", _block((0.1, 0.025, 0.006), (hx, hy - 0.08, back - 0.004)), "blue_plastic", XRAY_HEAD, "Head")
    # A coiled lead from the collimator, and the lens.
    coil = [(hx + 0.08 + 0.012 * np.cos(t), hy - 0.15 - t * 0.006, hz + 0.09 + 0.012 * np.sin(t)) for t in np.linspace(0, 6 * np.pi, 40)]
    m.add("Lead", tube(coil, [(0.003, 0.003)] * len(coil), ring=6), "fabric_white", XRAY_HEAD, "Head")
    m.add("Lens", cylinder(0.06, (hx, hy - 0.18, hz), (hx, hy - 0.185, hz), 20), "glass", XRAY_HEAD, "Head")
    return m


def _stretcher_straps() -> Model:
    """Ambulance and street: straps over the table pads."""
    m = Model("props", "straps")
    straps = [torus(0.34, 0.006, (x, TABLE_HEIGHT + 0.0, 0.0), "x") for x in (-0.6, 0.2)]
    m.add("Straps", merge(*straps), "fabric_dark")
    return m


def _streetlight() -> Model:
    m = Model("props", "streetlight")
    m.add("Pole", merge(cylinder(0.07, (0.0, 0.0, 0.0), (0.0, 0.4, 0.0), 12), cylinder(0.05, (0.0, 0.4, 0.0), (0.0, 4.4, 0.0), 12)), "dark_steel")
    m.add("Arm", tube([(0.0, 4.3, 0.0), (0.0, 4.55, -0.2), (0.0, 4.55, -0.8)], [(0.03, 0.03)] * 3, ring=10), "dark_steel")
    m.add("Lamp", superellipsoid((0.25, 0.1, 0.45), 0.4, (0.0, 4.5, -0.95)), "dark_steel")
    m.add("Bulb", superellipsoid((0.2, 0.02, 0.38), 0.4, (0.0, 4.45, -0.95)), "flame")
    return m


def _delivery_tray() -> Model:
    """Where the nurse leaves what you ordered: a small trolley with a rimmed top at 0.9 m."""
    m = Model("props", "delivery_tray")
    m.add("Base", _wheels(0.022, 0.2), "dark_steel")
    m.add("Pole", cylinder(0.018, (0.0, 0.05, 0.0), (0.0, 0.88, 0.0), 12), "steel")
    top = superellipsoid((0.5, 0.02, 0.38), 0.12, (0.0, 0.895, 0.0))
    rim = merge(
        *[
            superellipsoid(size, 0.2, c)
            for size, c in (
                ((0.52, 0.04, 0.015), (0.0, 0.92, 0.19)),
                ((0.52, 0.04, 0.015), (0.0, 0.92, -0.19)),
                ((0.015, 0.04, 0.38), (0.25, 0.92, 0.0)),
                ((0.015, 0.04, 0.38), (-0.25, 0.92, 0.0)),
            )
        ]
    )
    m.add("Tray", merge(top, rim), "steel")
    m.add("Tag", superellipsoid((0.16, 0.05, 0.004), 0.1, (0.0, 0.915, 0.198)), "yellow_plastic")
    return m


def _defib_cart() -> Model:
    """Crash cart: red drawers, the defibrillator unit on top with its screen facing +Z. The paddles rest on the top."""
    m = Model("props", "defib_cart")
    m.add("Body", superellipsoid((0.55, 0.85, 0.45), 0.08, (0.0, 0.47, 0.0)), "red_plastic")
    drawers = [superellipsoid((0.48, 0.02, 0.01), 0.2, (0.0, y, 0.228)) for y in (0.25, 0.45, 0.65)]
    m.add("Handles", merge(*drawers), "steel")
    m.add("Wheels", merge(*[cylinder(0.035, (x - 0.01, 0.035, z), (x + 0.01, 0.035, z), 12) for x in (-0.22, 0.22) for z in (-0.17, 0.17)]), "black_plastic")
    m.add("Unit", superellipsoid((0.34, 0.16, 0.26), 0.2, (0.0, 0.98, -0.07)), "plastic")
    m.add("Screen", superellipsoid((0.2, 0.1, 0.01), 0.1, (0.0, 1.0, 0.062)), "screen")
    return m


def _sink() -> Model:
    """Scrub sink: a steel basin on a cabinet with a gooseneck tap. Basin rim at 0.9 m, front toward +Z."""
    m = Model("props", "sink")
    m.add("Cabinet", superellipsoid((0.7, 0.82, 0.5), 0.05, (0.0, 0.41, 0.0)), "steel")
    # Out along the bottom and up the wall, then back down the inside: a hollow bowl.
    basin = lathe([(0.0, 0.0), (0.2, 0.0), (0.26, 0.12), (0.27, 0.13), (0.25, 0.13), (0.19, 0.02), (0.0, 0.02)], 28)
    m.add("Basin", moved(basin, (0.0, 0.78, 0.02)), "dark_steel")
    # Up and over in one plane: its rings are laid out from across that plane, or they twist into a pinch where it
    # runs straight up.
    tap = [(0.0, 0.91, -0.2), (0.0, 1.12, -0.2), (0.0, 1.16, -0.1), (0.0, 1.06, -0.02)]
    m.add("Tap", tube(tap, [(0.012, 0.012)] * len(tap), ring=10, up=(1.0, 0.0, 0.0)), "chrome")
    m.add("Lever", cylinder(0.008, (-0.06, 1.0, -0.2), (0.06, 1.0, -0.2), 8), "chrome")
    return m


def _smoking() -> Model:
    """Standing ashtray: a steel bin with a sand-filled bowl on top, a few butts stubbed out in it."""
    m = Model("props", "smoking")
    bin_ = lathe([(0.0, 0.0), (0.13, 0.0), (0.13, 0.02), (0.1, 0.04), (0.1, 0.62), (0.14, 0.66), (0.14, 0.7), (0.0, 0.7)], 24)
    m.add("Bin", bin_, "dark_steel")
    m.add("Sand", cylinder(0.125, (0.0, 0.7, 0.0), (0.0, 0.705, 0.0), 24), "paper")
    butts = [cylinder(0.004, (x, 0.705, z), (x + 0.02, 0.712, z + 0.01), 8) for x, z in ((-0.06, 0.02), (0.01, -0.05), (0.04, 0.04), (-0.02, -0.01))]
    m.add("Butts", merge(*butts), "iodine")
    return m


def build() -> list[Model]:
    return [
        _operating_table(),
        _instrument_tray(),
        _iv_stand(),
        _shelf(),
        _clipboard(),
        _bell(),
        _gloves(),
        _sanitizer(),
        _lamp(),
        _monitor(),
        _xray(),
        _stretcher_straps(),
        _streetlight(),
        _delivery_tray(),
        _defib_cart(),
        _sink(),
        _smoking(),
    ]
