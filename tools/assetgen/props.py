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
# The monitor's screen above the table's head end: its glass center and its turn about Y from facing +Z (degrees), same
# as MonitorScreen and MonitorYaw in src/World/Room.cs. It faces down the table, turned a little toward the near side.
MONITOR_SCREEN = (1.02, 1.45, -0.32)
MONITOR_YAW = -70.0
# Where the monitor's pole stands, x and z: just outside the head rail, clear of the patient card in its middle. Room.cs
# makes it solid there (MonitorPole).
MONITOR_POLE = (1.075, -0.2)
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


def _block(size: tuple[float, float, float], center: tuple[float, float, float]) -> trimesh.Trimesh:
    """A box with just its edges eased: sheet metal and steel section, not moulded plastic.
    Six rings already keep the eased edges, and the carts are made of many of these."""
    return superellipsoid(size, 0.06, center, detail=6)


def _caster(x: float, z: float) -> list[trimesh.Trimesh]:
    """A swivel caster under a corner: its plate, the fork either side of the wheel and the stem, steel."""
    return [
        _block((0.06, 0.008, 0.06), (x, 0.096, z)),
        cylinder(0.012, (x, 0.092, z), (x, 0.075, z), 10),
        _block((0.005, 0.05, 0.04), (x - 0.022, 0.06, z + 0.012)),
        _block((0.005, 0.05, 0.04), (x + 0.022, 0.06, z + 0.012)),
        cylinder(0.006, (x - 0.026, 0.045, z + 0.02), (x + 0.026, 0.045, z + 0.02), 8),
    ]


def _wheel(x: float, z: float) -> trimesh.Trimesh:
    """The rubber wheel in a caster's fork."""
    return cylinder(0.045, (x - 0.014, 0.045, z + 0.02), (x + 0.014, 0.045, z + 0.02), 20)


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


# The glove cart: a white drawer cart with a flat top, the glove box standing on it under the organizer at the back.
CART_TOP = 0.9
CART_WIDTH, CART_DEPTH = 0.56, 0.44
# Drawer rows top down, one shallow row over three deep ones, in two columns.
DRAWER_ROWS = (0.08, 0.195, 0.195, 0.195)
DRAWER_GAP = 0.012


def _drawer_cart(m: Model) -> None:
    """A hospital cart: a white body on a steel chassis and four swivel casters, a flat top, two columns of blue
    drawers with a chrome bar handle each, a push handle on the left and a flat chart holder on the right.
    The top is closed, so things stand on it. The body fills the station's solid footprint; the handles and the holder
    stand a few centimeters proud of it, like the old cabinet's door handles did.
    """
    hw, hd = CART_WIDTH / 2, CART_DEPTH / 2
    top = _block((CART_WIDTH + 0.04, 0.03, CART_DEPTH + 0.02), (0.0, CART_TOP - 0.015, 0.0))
    body = _block((CART_WIDTH, CART_TOP - 0.15, CART_DEPTH), (0.0, (CART_TOP - 0.03 + 0.12) / 2, 0.0))
    chassis = _block((CART_WIDTH - 0.06, 0.03, CART_DEPTH - 0.08), (0.0, 0.105, 0.0))
    corners = [(x, z) for x in (-hw + 0.06, hw - 0.06) for z in (-hd + 0.08, hd - 0.08)]
    casters = [part for x, z in corners for part in _caster(x, z)]
    wheels = [_wheel(x, z) for x, z in corners]
    # The drawer fronts stand proud of the body, their handles a bar on two stand-offs just below the top edge.
    fronts, handles = [], []
    y = CART_TOP - 0.03 - DRAWER_GAP
    for height in DRAWER_ROWS:
        for x in (-hw / 2, hw / 2):
            fronts.append(_block((hw - DRAWER_GAP, height, 0.016), (x, y - height / 2, hd + 0.006)))
            bar_y, bar_z = y - 0.03, hd + 0.032
            handles.append(cylinder(0.006, (x - 0.09, bar_y, bar_z), (x + 0.09, bar_y, bar_z), 10))
            handles += [cylinder(0.005, (x + s * 0.08, bar_y, hd + 0.014), (x + s * 0.08, bar_y, bar_z), 8) for s in (-1, 1)]
        y -= height + DRAWER_GAP
    # The push handle: a loop out of the left side behind the middle, its grip in the middle.
    loop = [(-hw, 0.62, -0.15), (-hw - 0.03, 0.62, -0.15), (-hw - 0.03, CART_TOP - 0.06, -0.15), (-hw, CART_TOP - 0.06, -0.15)]
    handles += [cylinder(0.008, a, b, 10) for a, b in pairwise(loop)]
    handles += [ellipsoid((0.008, 0.008, 0.008), bend, 1) for bend in loop[1:-1]]
    m.add("Grip", cylinder(0.011, (-hw - 0.03, 0.68, -0.15), (-hw - 0.03, 0.78, -0.15), 12), "black_plastic")
    # A flat steel chart holder hangs on the right side from a rail screwed into the body.
    holder = merge(
        _block((0.008, 0.36, 0.28), (hw + 0.012, 0.66, 0.0)),
        _block((0.03, 0.012, 0.28), (hw + 0.001, 0.845, 0.0)),
    )
    m.add("Cabinet", merge(top, body), "plastic")
    m.add("Drawers", merge(*fronts), "blue_plastic")
    m.add("Handles", merge(*handles), "chrome")
    m.add("Steel", merge(chassis, holder, *casters), "steel")
    m.add("Wheels", merge(*wheels), "rubber")


def _organizer(m: Model) -> None:
    """The open organizer standing at the back of the cart top: two uprights with a back panel, two tiers of frosted
    bins on shelves that reach forward over the glove box, and a top shelf with a lip. It clears the box and the gloves
    fanned out of its slot, and the player at the station looks under its front edge at the box.
    """
    hw, hd = CART_WIDTH / 2, CART_DEPTH / 2
    depth, back = 0.17, -hd - 0.005
    front = back + depth
    tiers = (1.12, 1.36)
    top_y = tiers[-1] + 0.22
    frame = [_block((0.025, top_y - CART_TOP, 0.025), (s * (hw - 0.0125), (top_y + CART_TOP) / 2, back + 0.0125)) for s in (-1, 1)]
    frame.append(_block((CART_WIDTH - 0.05, top_y - CART_TOP - 0.02, 0.008), (0.0, (top_y + CART_TOP) / 2, back + 0.004)))
    frame += [_block((CART_WIDTH, 0.015, depth), (0.0, y - 0.0075, back + depth / 2)) for y in (*tiers, top_y)]
    frame += [_block((0.012, 0.2, depth), (s * (hw - 0.006), y + 0.1, back + depth / 2)) for y in tiers for s in (-1, 1)]
    frame.append(_block((CART_WIDTH, 0.03, 0.01), (0.0, top_y + 0.015, front - 0.005)))
    bins, labels = [], []
    pitch = (CART_WIDTH - 0.05) / 3
    for y in tiers:
        for x in (-pitch, 0.0, pitch):
            bins.append(superellipsoid((pitch - 0.015, 0.13, depth - 0.02), 0.08, (x, y + 0.065, back + depth / 2), detail=8))
            labels.append(_block((pitch - 0.07, 0.03, 0.004), (x, y + 0.105, front - 0.008)))
    m.add("Organizer", merge(*frame), "plastic")
    m.add("Bins", merge(*bins), "frosted_plastic")
    m.add("Labels", merge(*labels), "blue_plastic")


def _gloves() -> Model:
    """Glove cart: a drawer cart with the organizer on its top and a dispenser box of gloves under the organizer's front
    edge, a loose pair in front of the box, from a hand-made source model."""
    m = Model("props", "gloves")
    _drawer_cart(m)
    _organizer(m)
    source = trimesh.load_scene(SOURCES / "glove_box.glb")
    # The source is Z up with the loose pair toward -Y: stand it Y up with the pair toward the drawers (+Z), on the
    # cart's top just in front of the organizer's uprights, so the pair ends at the top's front edge.
    place = trimesh.transformations.translation_matrix((0.0, CART_TOP, -0.085)) @ trimesh.transformations.rotation_matrix(-np.pi / 2, (1.0, 0.0, 0.0))
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
    """Vital signs monitor on the table's head end, origin at the screen center, screen facing +Z.
    A pole clamped to the head rail and the frame's end carries a short folded arm, the module rack hanging under its
    elbow and the flat screen on the white mounting block under it, at the arm's end.
    Built where it stands by the table, then moved into the screen's frame, since the game places it by the screen.
    """
    m = Model("props", "monitor")
    screen = trimesh.transformations.rotation_matrix(np.radians(MONITOR_YAW), (0.0, 1.0, 0.0))
    screen[:3, 3] = MONITOR_SCREEN
    to_screen = np.linalg.inv(screen)

    def by_table(mesh: trimesh.Trimesh) -> trimesh.Trimesh:
        """A mesh built in the table's space, moved into the screen's."""
        out = mesh.copy()
        out.apply_transform(to_screen)
        return out

    def on_table(point: tuple[float, float, float]) -> tuple[float, float, float]:
        x, y, z = (screen @ (*point, 1.0))[:3]
        return (float(x), float(y), float(z))

    # The flat panel: black bezel and glass in front, a light housing behind, and the white block under it that the arm
    # holds it by.
    m.add("Bezel", superellipsoid((0.434, 0.302, 0.026), 0.04, (0.0, 0.0, -0.013)), "black_plastic")
    m.add("Screen", superellipsoid((0.39, 0.258, 0.004), 0.02, (0.0, 0.0, 0.0)), "screen")
    m.add("Housing", merge(superellipsoid((0.4, 0.27, 0.03), 0.1, (0.0, 0.0, -0.035)), _block((0.3, 0.045, 0.06), (0.0, -0.17, -0.025))), "plastic")
    # Pole: clamped round the head rail's top bar and bolted to the frame's end below it.
    x, z = MONITOR_POLE
    pole_top = 1.2
    clamps = [
        _block((x - HEAD_RAIL_X + 0.04, 0.04, 0.04), ((x + HEAD_RAIL_X) / 2, HEAD_RAIL_TOP, z)),
        _block((x - 0.98, 0.03, 0.04), ((x + 0.98) / 2, TABLE_HEIGHT - 0.075, z)),
    ]
    pole = [cylinder(0.016, (x, TABLE_HEIGHT - 0.1, z), (x, pole_top, z), 16)]
    m.add("Pole", by_table(merge(*clamps, *pole)), "steel")
    # Arm: out from the pole top to an elbow beyond the head end, back to under the screen, and up into its block.
    tilt = on_table((0.0, -0.19, -0.025))
    elbow = (1.14, pole_top, -0.3)
    below_tilt = (tilt[0], pole_top, tilt[2])
    arm = [_beam((x, pole_top, z), elbow, 0.036, 0.03), _beam(elbow, below_tilt, 0.036, 0.03)]
    knuckles = [cylinder(0.022, (px, pole_top - 0.022, pz), (px, pole_top + 0.022, pz), 20) for px, _, pz in ((x, 0, z), elbow, below_tilt)]
    riser = cylinder(0.016, below_tilt, tilt, 16)
    m.add("Arm", by_table(merge(*arm, *knuckles, riser)), "plastic")
    # Module rack hanging under the elbow, facing the same way as the screen: a small screen and plug-in modules.
    rack = screen.copy()
    rack[:3, 3] = (elbow[0], pole_top - 0.13, elbow[2])

    def by_rack(mesh: trimesh.Trimesh) -> trimesh.Trimesh:
        out = mesh.copy()
        out.apply_transform(rack)
        return by_table(out)

    hanger = cylinder(0.01, (0.0, 0.04, 0.0), (0.0, 0.11, 0.0), 10)
    m.add("Rack", by_rack(merge(_block((0.17, 0.08, 0.08), (0.0, 0.0, 0.0)), hanger)), "plastic")
    modules = [_block((0.018, 0.06, 0.004), (0.015 + i * 0.022, 0.0, 0.04)) for i in range(3)]
    m.add("Modules", by_rack(merge(_block((0.06, 0.045, 0.004), (-0.045, 0.005, 0.04)), *modules)), "black_plastic")
    return m


# Mobile X-ray unit, front (-Z) toward the table. The arm slides straight up and down the column, level, and carries the
# tube head; the game moves Arm along Y, its node origin at the tube head's. Same as XrayCart (src/World/XrayCart.cs).
XRAY_HEAD = (0.0, 1.6, -0.72)
# The column stands left of the middle, so a player pushing the cart sees past it.
XRAY_COLUMN = (-0.17, -0.03)
# How far the arm runs above the tube head's origin.
XRAY_ARM_ABOVE = 0.24


def _beam(a: tuple[float, float, float], b: tuple[float, float, float], width: float, height: float) -> trimesh.Trimesh:
    """A level rectangular steel section from a to b."""
    va, vb = np.asarray(a, float), np.asarray(b, float)
    run = vb - va
    beam = _block((width, height, float(np.linalg.norm(run))), (0.0, 0.0, 0.0))
    beam.apply_transform(trimesh.transformations.rotation_matrix(float(np.arctan2(run[0], run[2])), (0.0, 1.0, 0.0)))
    beam.apply_translation((va + vb) / 2)
    return beam


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
    corners = [(x, z) for x in (-0.24, 0.24) for z in (-0.33, 0.37)]
    forks = [part for x, z in corners for part in _caster(x, z)]
    wheels = [_wheel(x, z) for x, z in corners]
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
