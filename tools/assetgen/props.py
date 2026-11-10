"""Room furniture and stations. Origin on the floor under the prop's center unless noted."""

from __future__ import annotations

from itertools import pairwise
from pathlib import Path

import numpy as np
import trimesh

from .geometry import Model, cylinder, ellipsoid, lathe, merge, moved, rotated, rounded_slab, scaled, superellipsoid, torus, tube

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
# The instrument tray's casters are this size of a cart's, their plates just under its legs' ends.
CASTER_SIZE = 0.62
# Hand-made models some props are built from. The .gdignore there keeps Godot from importing them as game assets.
SOURCES = Path(__file__).resolve().parent / "sources"
# glove_box.glb node -> part and palette material. Every other node is a glove.
GLOVE_BOX_PARTS = {
    "Box_Cardboard": ("Box", "carton"),
    "Printed_Blue_Edge_Trim": ("Print", "carton_print"),
    "Inset_Dark_Slot": ("Slot", "black_plastic"),
}
# Top of the defibrillator on its cart, where the game parks its paddle: Room.DefibRoof (src/World/Room.cs).
DEFIB_ROOF = 1.16
# How much narrower the defibrillator is at its roof than at its foot, as a share of its width.
DEFIB_TAPER = 0.18


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


def _caster(x: float, z: float, size: float = 1.0, heading: float = 0.0) -> list[trimesh.Trimesh]:
    """A swivel caster under a corner: its plate, the fork either side of the wheel and the stem, steel.
    size scales it from the floor up; heading turns it about its stem, from rolling along Z."""
    parts = [
        _block((0.06, 0.008, 0.06), (0.0, 0.096, 0.0)),
        cylinder(0.012, (0.0, 0.092, 0.0), (0.0, 0.075, 0.0), 10),
        _block((0.005, 0.05, 0.04), (-0.022, 0.06, 0.012)),
        _block((0.005, 0.05, 0.04), (0.022, 0.06, 0.012)),
        cylinder(0.006, (-0.026, 0.045, 0.02), (0.026, 0.045, 0.02), 8),
    ]
    return [_placed(part, x, z, size, heading) for part in parts]


def _wheel(x: float, z: float, size: float = 1.0, heading: float = 0.0) -> trimesh.Trimesh:
    """The rubber wheel in a caster's fork, sized and turned as the caster."""
    return _placed(cylinder(0.045, (-0.014, 0.045, 0.02), (0.014, 0.045, 0.02), 20), x, z, size, heading)


def _placed(part: trimesh.Trimesh, x: float, z: float, size: float, heading: float) -> trimesh.Trimesh:
    """A caster part built about its stem on the floor, scaled, turned about the stem and moved to (x, z)."""
    return moved(rotated(scaled(part, (size, size, size)), heading, (0.0, 1.0, 0.0)), (x, 0.0, z))


def _operating_table() -> Model:
    """A general surgery table: a floor plinth with brake pedals, one column flaring into the tabletop, sectioned foam
    pads on a steel frame with hinge pins, and flat accessory rails on standoffs along both sides.
    A hand pendant hangs on the foot end.
    The pads' top stays at TABLE_HEIGHT: the game lands blood on pads and frame (BedLiquidSurface)."""
    m = Model("props", "operating_table")
    m.add("Base", superellipsoid((0.82, 0.05, 0.54), 0.15, (0.0, 0.025, 0.0), detail=6), "dark_steel")
    # One lofted pillar: flared onto the plinth, a slim waist, and flared again into the block under the tabletop.
    column = [(0.05, 0.2, 0.14), (0.1, 0.15, 0.11), (0.2, 0.13, 0.095), (0.58, 0.11, 0.08), (0.68, 0.12, 0.09), (0.73, 0.2, 0.14), (0.75, 0.23, 0.16)]
    m.add("Column", tube([(0.0, y, 0.0) for y, _, _ in column], [(x, z) for _, x, z in column], ring=20, exponent=4.0, up=(1.0, 0.0, 0.0)), "steel")
    # Runs from x -1.3 (feet) to 1.0 (head), long enough for the whole patient to lie on it, in four sections: legs,
    # seat and back, chest and the head rest. The foam pads sit on one steel frame, with hinge pins under the joints.
    sections = ((-0.85, 0.88), (0.0, 0.8), (0.62, 0.42), (0.92, 0.16))
    frame = [rounded_slab((2.29, 0.04, 0.54), 0.02, 0.006, (-0.145, TABLE_HEIGHT - 0.075, 0.0))]
    hinges = [cylinder(0.011, (x, TABLE_HEIGHT - 0.085, -0.25), (x, TABLE_HEIGHT - 0.085, 0.25), 10) for x in (-0.405, 0.405, 0.835)]
    m.add("Frame", merge(*frame, *hinges), "steel")
    pads = [rounded_slab((length, 0.06, 0.56), 0.035, 0.018, (x, TABLE_HEIGHT - 0.03, 0.0)) for x, length in sections]
    m.add("Pads", merge(*pads), "mattress")
    low = TABLE_HEIGHT - 0.07
    # Flat side rails, the kind arm boards and screens clamp onto, held off the frame by standoffs.
    rails = []
    for s in (-1, 1):
        rails.append(moved(trimesh.creation.box((2.2, 0.03, 0.01)), (-0.15, low, s * 0.3)))
        rails += [moved(trimesh.creation.box((0.03, 0.02, 0.03)), (x, low, s * 0.285)) for x in (-1.15, -0.45, 0.25, 0.9)]
    # The rails carry on round the head end and up into a bar above the pads.
    bends = [(0.95, low, -0.3), (HEAD_RAIL_X, low, -0.3), (HEAD_RAIL_X, HEAD_RAIL_TOP, -0.3)]
    bends += [(x, y, -z) for x, y, z in reversed(bends)]
    rails += [cylinder(0.008, a, b, 8) for a, b in pairwise(bends)]
    rails += [ellipsoid((0.008, 0.008, 0.008), bend, 1) for bend in bends[1:-1]]
    m.add("Rails", merge(*rails), "chrome")
    pedals = [superellipsoid((0.09, 0.025, 0.06), 0.3, (x, 0.04, 0.3), detail=4) for x in (-0.13, 0.13)]
    # The hand pendant hangs by its hook from the foot end rail, its coiled cord running down into the base.
    pendant_x, pendant_z = -1.05, 0.315
    hook_path = [(pendant_x, low - 0.06, pendant_z), (pendant_x, low + 0.02, pendant_z), (pendant_x, low + 0.02, pendant_z - 0.02)]
    hook = tube(hook_path, [(0.003, 0.003)] * 3, ring=6)
    body = superellipsoid((0.06, 0.15, 0.03), 0.3, (pendant_x, low - 0.135, pendant_z + 0.005), detail=4)
    cord_path = [(pendant_x, low - 0.21, pendant_z), (pendant_x + 0.1, 0.3, pendant_z - 0.02), (-0.4, 0.1, 0.25), (-0.32, 0.04, 0.2)]
    cord = tube(cord_path, [(0.004, 0.004)] * 4, ring=6)
    m.add("Pendant", merge(body, hook, cord), "black_plastic")
    m.add("Pedals", merge(*pedals), "black_plastic")
    return m


def _instrument_tray() -> Model:
    """A steel instrument table on one telescoping column: a sheet tray with a rolled rim over two rails, a height collar
    with its knob, and a five-leg base on casters. Its top stays at TraySurface (src/World/Room.cs), where tools lie."""
    m = Model("props", "instrument_tray")
    legs, wheels = [], []
    for i in range(5):
        a = i * 2 * np.pi / 5 + np.pi / 2
        end = np.array([np.cos(a), 0.0, np.sin(a)]) * 0.3
        legs.append(tube([(0.0, 0.08, 0.0), (end[0], 0.065, end[2])], [(0.014, 0.02), (0.009, 0.014)], ring=8, exponent=3.0, smooth=1))
        # Each caster rolls along its leg.
        legs += _caster(float(end[0]), float(end[2]), CASTER_SIZE, float(np.arctan2(end[0], end[2])))
        wheels.append(_wheel(float(end[0]), float(end[2]), CASTER_SIZE, float(np.arctan2(end[0], end[2]))))
    m.add("Base", merge(*legs, cylinder(0.04, (0.0, 0.05, 0.0), (0.0, 0.1, 0.0), 16)), "dark_steel")
    m.add("Wheels", merge(*wheels), "rubber")
    column = merge(
        cylinder(0.024, (0.0, 0.1, 0.0), (0.0, 0.56, 0.0), 16),
        cylinder(0.017, (0.0, 0.56, 0.0), (0.0, 0.86, 0.0), 16),
    )
    m.add("Pole", column, "steel")
    collar = merge(
        cylinder(0.03, (0.0, 0.54, 0.0), (0.0, 0.58, 0.0), 16),
        cylinder(0.005, (0.0, 0.56, 0.0), (0.0, 0.56, 0.06), 8),
        superellipsoid((0.022, 0.022, 0.012), 0.6, (0.0, 0.56, 0.065), detail=4),
    )
    m.add("Collar", collar, "black_plastic")
    rails = [_block((0.03, 0.02, 0.7), (x, 0.883, 0.0)) for x in (-0.2, 0.2)]
    bracket = merge(_block((0.44, 0.02, 0.05), (0.0, 0.871, 0.0)), cylinder(0.026, (0.0, 0.85, 0.0), (0.0, 0.862, 0.0), 16))
    sheet = rounded_slab((0.7, 0.012, 0.8), 0.05, 0.003, (0.0, 0.899, 0.0))
    rim = tube(*_rounded_loop(0.69, 0.79, 0.045, 0.909, (0.008, 0.008)), ring=10, smooth=1)
    m.add("Tray", merge(sheet, rim, bracket, *rails), "steel")
    m.add("Drape", rounded_slab((0.66, 0.006, 0.76), 0.03, 0.002, (0.0, 0.906, 0.0)), "gown")
    m.add("SmallTrays", merge(*[_small_tray(*area) for area in SMALL_TRAYS]), "steel")
    return m


def _rounded_loop(
    width: float, depth: float, corner: float, y: float, radii: tuple[float, float], at: tuple[float, float] = (0.0, 0.0)
) -> tuple[list[tuple[float, float, float]], list[tuple[float, float]]]:
    """A closed path round a rounded rectangle in the XZ plane, with `radii` at every point, for `tube`. It starts and
    ends mid side, where the tube's seam is least seen."""
    hx, hz = width / 2 - corner, depth / 2 - corner
    path = [(at[0], y, at[1] + depth / 2)]
    for cx, cz, a0 in ((hx, hz, 90), (hx, -hz, 0), (-hx, -hz, -90), (-hx, hz, -180)):
        for a in np.radians(np.linspace(a0, a0 - 90, 4)):
            path.append((at[0] + cx + corner * np.cos(a), y, at[1] + cz + corner * np.sin(a)))
    path.append(path[0])
    return path, [radii] * len(path)


def _small_tray(x: float, z: float, width: float, depth: float) -> trimesh.Trimesh:
    """A shallow steel tray on the drape, its corner at (x, z). Floor top 4 mm and rim 16 mm above the surface."""
    at = (x + width / 2, z + depth / 2)
    floor = rounded_slab((width, 0.004, depth), 0.008, 0.001, (at[0], 0.917, at[1]))
    wall = tube(*_rounded_loop(width - 0.006, depth - 0.006, 0.006, 0.923, (0.008, 0.003), at), ring=8, exponent=4.0, smooth=1)
    return merge(floor, wall)


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
    """A white wall shelf on steel L brackets with a row of reference books and ring binders, spines out, and the game's
    thick red manual. The wall is at -Z; the ambulance keeps only the manual and its label (Room.Ambulance.cs)."""
    m = Model("props", "manual")
    top = 1.215
    back = -0.15
    m.add("Board", superellipsoid((0.8, 0.03, 0.3), 0.12, (0.0, top - 0.015, 0.0), detail=6), "wood")
    brackets = []
    for x in (-0.3, 0.3):
        brackets += [
            moved(trimesh.creation.box((0.025, 0.2, 0.004)), (x, top - 0.13, back + 0.002)),
            moved(trimesh.creation.box((0.025, 0.004, 0.25)), (x, top - 0.032, back + 0.125)),
            tube([(x, top - 0.21, back + 0.004), (x, top - 0.034, back + 0.2)], [(0.004, 0.006)] * 2, ring=6, exponent=4.0),
        ]
    m.add("Brackets", merge(*brackets), "dark_steel")

    def volume(x: float, width: float, height: float, depth: float, lean: float = 0.0) -> tuple[trimesh.Trimesh, trimesh.Trimesh]:
        """A hardcover's boards and spine, and its page block 3 mm inside them, standing at x."""
        z = back + 0.01 + depth / 2
        cover = merge(
            moved(trimesh.creation.box((0.003, height, depth)), (x - width / 2 + 0.0015, top + height / 2, z)),
            moved(trimesh.creation.box((0.003, height, depth)), (x + width / 2 - 0.0015, top + height / 2, z)),
            moved(trimesh.creation.box((width, height, 0.004)), (x, top + height / 2, z + depth / 2 - 0.002)),
        )
        pages = moved(trimesh.creation.box((width - 0.006, height - 0.006, depth - 0.006)), (x, top + height / 2 - 0.003, z - 0.003))
        if lean:
            cover, pages = (rotated(part, lean, (0.0, 0.0, 1.0), (x + width / 2, top, z)) for part in (cover, pages))
        return cover, pages

    books, binders, pages = [], [], []
    rng = np.random.default_rng(4)
    x = -0.37
    # Reference books of mixed size, then three blue binders with paper labels and finger holes.
    for _ in range(5):
        width, height = rng.uniform(0.028, 0.045), rng.uniform(0.2, 0.25)
        cover, block = volume(x + width / 2, width, height, rng.uniform(0.15, 0.19))
        books.append(cover)
        pages.append(block)
        x += width + 0.002
    labels, holes = [], []
    for _ in range(3):
        width = 0.055
        cover, block = volume(x + width / 2, width, 0.29, 0.22)
        binders.append(cover)
        pages.append(block)
        labels.append(moved(trimesh.creation.box((0.035, 0.09, 0.002)), (x + width / 2, top + 0.2, back + 0.231)))
        holes.append(cylinder(0.009, (x + width / 2, top + 0.07, back + 0.229), (x + width / 2, top + 0.07, back + 0.232), 8))
        x += width + 0.002
    manual_x = 0.2
    # A last book leans on the manual.
    cover, block = volume(manual_x - 0.04 - 0.042, 0.032, 0.22, 0.17, -0.12)
    books.append(cover)
    pages.append(block)
    m.add("Books", merge(*books), "leather")
    m.add("Binders", merge(*binders), "blue_plastic")
    m.add("Pages", merge(*pages, *labels), "paper")
    m.add("Holes", merge(*holes), "black_plastic")
    # The manual is a thick red hardcover with a rounded spine and gold bands, so it stands out from the row.
    # Its pages are a part of their own: the ambulance hides the shelf's other pages but keeps the manual.
    width, height, depth = 0.08, 0.28, 0.21
    z = back + 0.01 + depth / 2
    board = trimesh.creation.box((0.004, height, depth - 0.012))
    boards = [moved(board, (manual_x + side * (width / 2 - 0.002), top + height / 2, z - 0.006)) for side in (-1, 1)]
    spine_z = back + 0.01 + depth - 0.012

    def rounded(y0: float, y1: float, grow: float) -> trimesh.Trimesh:
        """A length of the rounded spine between two heights, grown by `grow` all round."""
        return tube([(manual_x, top + y0, spine_z), (manual_x, top + y1, spine_z)], [(0.012 + grow, width / 2 + grow)] * 2, ring=16, up=(0.0, 0.0, 1.0))

    spine = rounded(0.0, height, 0.0)
    m.add("Manual", merge(*boards, spine), "book_cloth")
    m.add("ManualPages", moved(trimesh.creation.box((width - 0.008, height - 0.008, depth - 0.016)), (manual_x, top + height / 2, z - 0.008)), "paper")
    bands = [rounded(y - 0.004, y + 0.004, 0.0008) for y in (0.025, 0.04, height - 0.04, height - 0.025)]
    bands.append(moved(trimesh.creation.box((width * 0.5, 0.05, 0.002)), (manual_x, top + 0.19, spine_z + 0.0125)))
    m.add("Spine", merge(*bands), "gold")
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


def _monitor_wall_mount() -> Model:
    """Wall plate and swivel stem supporting the monitor arm in the ambulance."""
    m = Model("props", "monitor_wall_mount")
    screen = trimesh.transformations.rotation_matrix(np.radians(MONITOR_YAW), (0.0, 1.0, 0.0))
    screen[:3, 3] = MONITOR_SCREEN
    local_endpoint = np.linalg.inv(screen) @ (MONITOR_POLE[0], 1.2, MONITOR_POLE[1], 1.0)
    x, y, end_z = (float(value) for value in local_endpoint[:3])

    m.add("Plate", _block((0.12, 0.16, 0.02), (x, -0.25, -0.14)), "steel")
    bolts = [cylinder(0.004, (x + dx, -0.25 + dy, -0.131), (x + dx, -0.25 + dy, -0.126), 12) for dx in (-0.042, 0.042) for dy in (-0.06, 0.06)]
    m.add("Bolts", merge(*bolts), "chrome")
    m.add("SwivelStem", cylinder(0.016, (x, -0.25, -0.13), (x, y, end_z), 16), "steel")
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


def _coiled(path: list[tuple[float, float, float]], radius: float, turns: int) -> trimesh.Trimesh:
    """A coiled cord wound round `path`: `turns` loops of `radius` along it, spread by length."""
    points = np.asarray(path, float)
    lengths = np.r_[0.0, np.cumsum(np.linalg.norm(np.diff(points, axis=0), axis=1))]
    along = np.linspace(0.0, lengths[-1], turns * 8 + 1)
    centers = np.column_stack([np.interp(along, lengths, points[:, i]) for i in range(3)])
    coil = []
    for i, center in enumerate(centers):
        tangent = centers[min(i + 1, len(centers) - 1)] - centers[max(i - 1, 0)]
        tangent /= np.linalg.norm(tangent)
        side = np.cross(tangent, (0.0, 1.0, 0.0)) if abs(tangent[1]) < 0.9 else np.cross(tangent, (1.0, 0.0, 0.0))
        side /= np.linalg.norm(side)
        up = np.cross(side, tangent)
        angle = i * np.pi / 4
        coil.append(tuple(center + radius * (np.cos(angle) * side + np.sin(angle) * up)))
    return tube(coil, [(0.004, 0.004)] * len(coil), ring=5, smooth=1)


def _defib_cart() -> Model:
    """Crash cart: a blue moulded cart with a rimmed top tray and white drawers set in its front, two shallow ones over
    a deep one, the top one with an oval grip and a red breakaway seal. A white push frame runs up the front from the
    front casters. The defibrillator stands on top, screen facing +Z, a paddle on each side.
    The ambulance keeps only the defibrillator on its counter (Room.Ambulance.cs), so its bottom stays at 0.9 m."""
    m = Model("props", "defib_cart")
    rim = [((0.6, 0.035, 0.02), (0.0, 0.89, z)) for z in (-0.24, 0.24)] + [((0.02, 0.035, 0.46), (x, 0.89, 0.0)) for x in (-0.29, 0.29)]
    tray = [moved(trimesh.creation.box(size), at) for size, at in [((0.6, 0.03, 0.5), (0.0, 0.86, 0.0)), *rim]]
    grip_path, grip_radii = _rounded_loop(0.1, 0.035, 0.015, 0.0, (0.006, 0.006))
    grip = moved(rotated(tube(grip_path, grip_radii, ring=6, smooth=1), np.pi / 2, (1.0, 0.0, 0.0)), (0.0, 0.76, 0.236))
    m.add("Body", merge(_block((0.54, 0.7, 0.44), (0.0, 0.5, 0.0)), *tray, grip), "matte_blue_plastic")
    # Each drawer front has a lip along its top edge to pull it by.
    drawers, y = [], 0.81
    for height in (0.1, 0.1, 0.43):
        drawers.append(moved(trimesh.creation.box((0.44, height - 0.008, 0.016)), (0.0, y - height / 2, 0.224)))
        drawers.append(moved(trimesh.creation.box((0.44, 0.018, 0.012)), (0.0, y - 0.013, 0.236)))
        y -= height
    m.add("Drawers", merge(*drawers), "matte_plastic")
    m.add("Lock", merge(moved(trimesh.creation.box((0.03, 0.06, 0.006)), (-0.16, 0.74, 0.246))), "red_plastic")
    # Up from one front caster, over the top drawer and down to the other.
    frame = [(-0.25, 0.1, 0.25), (-0.25, 0.8, 0.25), (-0.24, 0.83, 0.25), (-0.2, 0.84, 0.25)]
    frame += [(-x, y, z) for x, y, z in reversed(frame)]
    m.add("Handles", tube(frame, [(0.014, 0.014)] * len(frame), ring=8, up=(0.0, 0.0, 1.0), smooth=3), "matte_plastic")
    wheels = []
    for x in (-0.25, 0.25):
        for z in (-0.19, 0.25):
            wheels += [
                cylinder(0.012, (x, 0.15, z), (x, 0.1, z), 8),
                moved(trimesh.creation.box((0.04, 0.05, 0.05)), (x, 0.085, z - 0.02)),
                cylinder(0.05, (x - 0.016, 0.05, z - 0.035), (x + 0.016, 0.05, z - 0.035), 14),
            ]
    m.add("Wheels", merge(*wheels), "matte_plastic")
    # The defibrillator: an orange back shell with a carry handle, and a grey front wrapping round its sides. The front
    # has the screen in an orange frame with a row of soft keys under it, mode keys left, charge and shock right, the
    # power button over the dial and the cable sockets along the bottom. A paddle sits in a holder on each side, its
    # coiled cord running from a socket round the front and up into the paddle's foot. Their tops stay flat at
    # DEFIB_ROOF, where the game's paddle parks face down (Room.StationTools()).
    height, middle = DEFIB_ROOF - 0.9, (DEFIB_ROOF + 0.9) / 2
    first_unit_part = len(m.parts)
    carry = [(-0.09, middle + 0.1, -0.165), (-0.075, DEFIB_ROOF + 0.035, -0.165), (0.075, DEFIB_ROOF + 0.035, -0.165), (0.09, middle + 0.1, -0.165)]
    m.add("Unit", superellipsoid((0.23, height, 0.12), 0.2, (0.0, middle, -0.12), detail=8), "matte_orange_plastic")
    m.add("Carry", tube(carry, [(0.014, 0.018)] * 4, ring=8, up=(0.0, 0.0, 1.0), smooth=3), "matte_orange_plastic")
    m.add("Front", superellipsoid((0.24, height, 0.11), 0.25, (0.0, middle, -0.035), detail=8), "matte_grey_plastic")
    screen_frame = moved(trimesh.creation.box((0.19, 0.125, 0.006)), (0.0, 1.085, 0.019))
    key_strip = moved(trimesh.creation.box((0.19, 0.026, 0.006)), (0.0, 1.008, 0.019))
    m.add("Bezel", merge(screen_frame, key_strip), "matte_orange_plastic")
    m.add("Screen", moved(trimesh.creation.box((0.165, 0.1, 0.006)), (0.0, 1.088, 0.022)), "screen")
    keys = [moved(trimesh.creation.box((0.026, 0.014, 0.006)), (x, 1.008, 0.024)) for x in (-0.066, -0.033, 0.0, 0.033, 0.066)]
    keys += [moved(trimesh.creation.box((0.04, 0.022, 0.008)), (-0.075, y, 0.019)) for y in (0.975, 0.945)]
    keys += [moved(trimesh.creation.box((0.05, 0.026, 0.008)), (0.072, y, 0.019)) for y in (0.977, 0.945)]
    keys.append(cylinder(0.024, (0.0, 0.947, 0.016), (0.0, 0.947, 0.03), 16))
    m.add("Keys", merge(*keys), "matte_plastic")
    m.add("Power", cylinder(0.011, (0.0, 0.982, 0.016), (0.0, 0.982, 0.025), 12), "green_plastic")
    m.add("Knob", cylinder(0.009, (0.0, 0.947, 0.03), (0.0, 0.947, 0.034), 10), "black_plastic")
    for name, x, material in (("SocketA", -0.045, "green_plastic"), ("SocketB", 0.045, "yellow_plastic")):
        m.add(name, cylinder(0.011, (x, 0.915, 0.012), (x, 0.915, 0.022), 12), material)
    holders, paddles, cords = [], [], []
    tips: dict[str, list[trimesh.Trimesh]] = {"yellow_plastic": [], "red_plastic": []}
    for side in (-1, 1):
        x = side * 0.125
        holders.append(superellipsoid((0.05, 0.03, 0.17), 0.2, (x + side * 0.015, 0.92, -0.075), detail=6))
        # The paddle: a plate against the case, solid at the back of its outer face and a grip round a slot at the
        # front.
        outer = x + side * 0.02
        paddles.append(superellipsoid((0.02, 0.2, 0.15), 0.3, (x, 1.04, -0.075), detail=5))
        paddles.append(superellipsoid((0.022, 0.19, 0.08), 0.3, (outer, 1.04, -0.11), detail=5))
        corners = ((0.085, 0.0), (0.085, 0.02), (0.065, 0.04), (-0.065, 0.04), (-0.085, 0.02), (-0.085, -0.02), (-0.065, -0.04), (0.065, -0.04), (0.085, -0.02))
        loop = [(outer, 1.04 + y, -0.045 + z) for y, z in (*corners, corners[0])]
        paddles.append(tube(loop, [(0.016, 0.022)] * len(loop), ring=8, up=(1.0, 0.0, 0.0), smooth=2))
        for z, color in ((-0.04, "yellow_plastic"), (-0.015, "red_plastic")):
            tips[color].append(cylinder(0.007, (x, 1.138, z), (x, 1.146, z), 8))
        path = [(side * 0.045, 0.915, 0.022), (side * 0.06, 0.91, 0.07), (side * 0.16, 0.912, 0.06), (side * 0.175, 0.92, 0.0)]
        path.append((x + side * 0.02, 0.928, -0.03))
        cords.append(_coiled(path, 0.01, 6))
    m.add("Holders", merge(*holders), "black_plastic")
    m.add("Paddles", merge(*paddles), "matte_orange_plastic")
    for color, buttons in tips.items():
        m.add(f"PaddleButtons{color.split('_')[0].title()}", merge(*buttons), color)
    m.add("Cords", merge(*cords), "matte_plastic")
    # Built square, then narrowed toward the top: the case is a trapezium seen from the front, its paddles leaning in
    # with its sides. The screen and keys on its face stay square.
    for part in m.parts[first_unit_part:]:
        if part.name in ("Bezel", "Screen", "Keys", "Power", "Knob"):
            continue
        rise = (part.mesh.vertices[:, 1] - 0.9) / height
        part.mesh.vertices[:, 0] *= 1.0 - DEFIB_TAPER * rise
    return m


def _sink() -> Model:
    """Hand wash sink: a round steel bowl with a drip-stop rim standing on a cabinet, a swivel gooseneck with an elbow
    lever behind it, and a knee panel on the cabinet door. Rim at 0.92 m, front toward +Z."""
    m = Model("props", "sink")
    # Its top stays under the bowl's floor, or it shows through the bowl as a false floor.
    # A toe recess under the front lets the surgeon stand close.
    cabinet = merge(_block((0.7, 0.68, 0.5), (0.0, 0.44, 0.0)), _block((0.66, 0.1, 0.44), (0.0, 0.05, -0.03)))
    m.add("Cabinet", cabinet, "steel")
    m.add("Door", merge(_block((0.6, 0.58, 0.012), (0.0, 0.44, 0.252)), _block((0.16, 0.08, 0.012), (0.0, 0.6, 0.262))), "dark_steel")
    # Down the inside to the drain and back up the outside, with the rim rolled over between: a bowl with a wall.
    inside = [(0.0, 0.012), (0.1, 0.012), (0.16, 0.025), (0.2, 0.06), (0.218, 0.12), (0.22, 0.135)]
    outside = [(0.24, 0.128), (0.228, 0.115), (0.21, 0.06), (0.17, 0.016), (0.1, 0.0), (0.0, 0.0)]
    rim = [(0.226, 0.143), (0.236, 0.141), (0.241, 0.136)]
    m.add("Basin", moved(lathe(inside + rim + outside, 28), (0.0, 0.78, 0.03)), "dark_steel")
    m.add("Drain", cylinder(0.032, (0.0, 0.791, 0.03), (0.0, 0.795, 0.03), 12), "chrome")
    m.add("Plug", cylinder(0.02, (0.0, 0.795, 0.03), (0.0, 0.797, 0.03), 12), "black_plastic")
    # Up and over in one plane: its rings are laid out from across that plane, or they twist into a pinch where it
    # runs straight up.
    tap = [(0.0, 1.0, -0.215), (0.0, 1.18, -0.215), (0.0, 1.23, -0.13), (0.0, 1.17, -0.03), (0.0, 1.11, -0.025)]
    lever = [(0.02, 0.96, -0.215), (0.1, 0.955, -0.205), (0.17, 0.94, -0.17)]
    body = [
        cylinder(0.032, (0.0, 0.78, -0.215), (0.0, 0.788, -0.215), 16),
        cylinder(0.02, (0.0, 0.788, -0.215), (0.0, 1.0, -0.215), 12),
        cylinder(0.024, (0.0, 0.94, -0.215), (0.0, 0.98, -0.215), 12),
        cylinder(0.014, (0.0, 1.11, -0.025), (0.0, 1.09, -0.025), 10),
    ]
    m.add("Tap", merge(*body, tube(tap, [(0.011, 0.011)] * len(tap), ring=10, up=(1.0, 0.0, 0.0), smooth=3)), "chrome")
    # The elbow lever: a long flat blade out to the side, turned with a forearm.
    m.add("Lever", tube(lever, [(0.004, 0.012), (0.004, 0.01), (0.004, 0.013)], ring=8, exponent=4.0, smooth=3), "chrome")
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


def _ambulance_fittings() -> Model:
    """Fitted ambulance cabin furniture, built around a table at the origin and a clear central ceiling panel."""
    m = Model("props", "ambulance")

    # Low side cabinets leave the middle of the floor open. Their inner faces are open shelves, with dividers,
    # while the worktops give the tools a flat, continuous surface at y=0.915.
    cabinet_parts: dict[str, list[trimesh.Trimesh]] = {"plastic": [], "blue_plastic": [], "chrome": []}
    for side in (-1, 1):
        outer = side * 1.13
        inner = side * 0.89
        mid = side * 1.01
        cabinet_parts["plastic"].append(_block((2.4, 0.9, 0.025), (-0.1, 0.45, outer)))
        cabinet_parts["plastic"].append(_block((2.4, 0.9, 0.025), (-0.1, 0.45, inner)))
        cabinet_parts["plastic"].append(_block((2.4, 0.035, 0.24), (-0.1, 0.0175, mid)))
        cabinet_parts["plastic"].append(_block((2.4, 0.035, 0.24), (-0.1, 0.8825, mid)))
        # Open shelf levels; broad compartments fit bottles and trays.
        for y in (0.25, 0.5, 0.75):
            cabinet_parts["plastic"].append(_block((2.35, 0.018, 0.22), (-0.1, y, mid)))
        for x in (-1.18, -0.4, 0.38, 1.0):
            cabinet_parts["blue_plastic"].append(_block((0.025, 0.82, 0.23), (x, 0.45, mid)))
        # Counter worktop, top face exactly at 0.915.
        cabinet_parts["blue_plastic"].append(_block((2.4, 0.03, 0.26), (-0.1, 0.9, mid)))
        # Low dividers keep the two rows of tool space legible without adding a front lip that would catch a grab.
        divider_x = (-0.7, -0.075, 0.375, 0.7) if side == 1 else (-0.7, -0.275, 0.075, 0.75)
        for x in divider_x:
            cabinet_parts["blue_plastic"].append(_block((0.01, 0.025, 0.24), (x, 0.9275, side * 1.005)))
        # Inward facing latches on the upper shelf fascia.
        for x in (-1.05, -0.3, 0.45, 0.9):
            cabinet_parts["chrome"].append(cylinder(0.008, (x, 0.84, inner), (x, 0.89, inner), 8))
    for material, meshes in cabinet_parts.items():
        m.add(f"Low{material.title().replace('_', '')}", merge(*meshes), material)

    upper_parts: dict[str, list[trimesh.Trimesh]] = {"plastic": [], "blue_plastic": [], "black_plastic": [], "chrome": []}
    for side in (-1, 1):
        inner = side * 0.95
        center_z = side * 1.04
        upper_parts["plastic"].append(_block((2.4, 0.32, 0.18), (-0.1, 1.82, center_z)))
        # Inward facing blue doors, seams, and small chrome pulls.
        for x, width in ((-0.9, 0.78), (-0.1, 0.78), (0.7, 0.78)):
            upper_parts["blue_plastic"].append(_block((width, 0.27, 0.012), (x, 1.82, inner)))
            upper_parts["chrome"].append(
                cylinder(0.006, (x + width / 2 - 0.06, 1.77, inner - side * 0.01), (x + width / 2 - 0.06, 1.84, inner - side * 0.01), 8)
            )
        for x in (-0.5, 0.3):
            upper_parts["black_plastic"].append(_block((0.012, 0.27, 0.004), (x, 1.82, inner - side * 0.008)))
    for material, meshes in upper_parts.items():
        m.add(f"Upper{material.title().replace('_', '')}", merge(*meshes), material)

    # End bulkhead storage, rear loading locker behind the feet, and rear double doors.
    m.add("EndBulkheadCabinet", _block((0.5, 0.9, 2.2), (1.5, 0.45, 0.0)), "plastic")
    m.add("EndBulkheadWorktop", _block((0.52, 0.03, 2.2), (1.5, 0.9, 0.0)), "blue_plastic")
    m.add("LoadingLocker", _block((0.26, 0.9, 2.2), (-1.57, 0.45, 0.0)), "plastic")
    m.add("LoadingWorktop", _block((0.28, 0.03, 2.2), (-1.57, 0.9, 0.0)), "blue_plastic")
    door_leaves = [_block((0.035, 2.02, 1.05), (-1.74, 1.01, side * 0.525)) for side in (-1, 1)]
    m.add("RearDoors", merge(*door_leaves), "plastic")
    windows = [_block((0.008, 0.35, 0.54), (-1.715, 1.5, side * 0.48)) for side in (-1, 1)]
    m.add("RearDoorWindows", merge(*windows), "black_plastic")
    m.add("RearDoorSeam", _block((0.01, 2.0, 0.012), (-1.708, 1.0, 0.0)), "dark_steel")
    rear_handles = [cylinder(0.009, (-1.7, 0.75, side * 0.07), (-1.7, 0.92, side * 0.07), 10) for side in (-1, 1)]
    m.add("RearDoorHandles", merge(*rear_handles), "chrome")

    # Cab partition at the head end; its tinted window remains above the storage cabinet.
    m.add("CabPartitionWindow", _block((0.018, 0.48, 1.6), (1.735, 1.55, 0.0)), "frosted_plastic")
    partition_frame = [
        _block((0.035, 0.035, 1.7), (1.725, 1.82, 0.0)),
        _block((0.035, 0.035, 1.7), (1.725, 1.28, 0.0)),
    ]
    partition_frame.extend(_block((0.035, 0.58, 0.035), (1.725, 1.55, z)) for z in (-0.85, 0.85))
    m.add("CabPartitionFrame", merge(*partition_frame), "blue_plastic")

    # Folded seat is parked flat against the +Z cabinet front, clear of the aisle.
    m.add("FoldedSideSeat", _block((0.6, 0.5, 0.04), (-1.0, 1.22, 0.87)), "blue_plastic")
    hinges = [cylinder(0.018, (-1.0 + dx, 1.19, 0.88), (-1.0 + dx, 1.25, 0.88), 10) for dx in (-0.24, 0.24)]
    m.add("SideSeatHinges", merge(*hinges), "chrome")

    # Side grab rails sit above the upper cabinets, leaving the central ceiling panel clear.
    rails = []
    for side in (-1, 1):
        z = side * 0.65
        rails.append(cylinder(0.014, (-1.3, 1.99, z), (1.3, 1.99, z), 12))
        for x in (-1.2, -0.4, 0.4, 1.2):
            rails.append(cylinder(0.01, (x, 1.99, z), (x, 2.05, z), 8))
    m.add("GrabRails", merge(*rails), "chrome")
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
        _monitor_wall_mount(),
        _xray(),
        _stretcher_straps(),
        _streetlight(),
        _delivery_tray(),
        _defib_cart(),
        _sink(),
        _smoking(),
        _ambulance_fittings(),
    ]
