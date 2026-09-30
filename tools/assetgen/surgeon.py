"""Surgeon body, head and arm segments (the gloved hand comes from tools/blender).

Body: origin at the feet, facing -Z. Head: origin at eye level. Arm segments: unit length, stretched by the game.
"""

from __future__ import annotations

from .geometry import Model, cylinder, ellipsoid, merge, rotated, superellipsoid, tube


def _body() -> Model:
    model = Model("surgeon", "body")
    torso = tube(
        [(0.0, 0.92, 0.0), (0.0, 1.05, 0.0), (0.0, 1.2, 0.0), (0.0, 1.35, 0.0), (0.0, 1.44, 0.0), (0.0, 1.5, 0.0)],
        [(0.12, 0.19), (0.115, 0.17), (0.125, 0.18), (0.13, 0.2), (0.11, 0.2), (0.06, 0.08)],
        ring=32,
        exponent=2.4,
        up=(0.0, 0.0, -1.0),
    )
    collar = rotated(ellipsoid((0.07, 0.02, 0.05), (0.0, 1.47, -0.07)), 0.3, (1, 0, 0), (0.0, 1.47, -0.07))
    model.add("Torso", merge(torso, collar), "tint", (0.0, 0.92, 0.0))
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        x = 0.1 * side
        leg = tube([(x, 0.98, 0.0), (x, 0.7, 0.01), (x * 1.05, 0.45, 0.0), (x * 1.08, 0.08, 0.0)], [(0.085, 0.08), (0.075, 0.07), (0.06, 0.058), (0.055, 0.05)])
        model.add(f"Leg{suffix}", leg, "tint", (x, 0.95, 0.0), "Torso")
        shoe = superellipsoid((0.11, 0.09, 0.28), 0.4, (x * 1.08, 0.045, -0.05))
        model.add(f"Shoe{suffix}", shoe, "rubber", (x * 1.08, 0.08, 0.0), f"Leg{suffix}")
    return model


def _head() -> Model:
    model = Model("surgeon", "head")
    neck = cylinder(0.055, (0.0, -0.25, 0.03), (0.0, -0.08, 0.02))
    skull = ellipsoid((0.085, 0.11, 0.1), (0.0, 0.0, 0.03), 4)
    ears = merge(ellipsoid((0.012, 0.03, 0.02), (0.085, -0.01, 0.04)), ellipsoid((0.012, 0.03, 0.02), (-0.085, -0.01, 0.04)))
    model.add("Face", merge(neck, skull, ears), "skin")
    eyes = merge(ellipsoid((0.013, 0.011, 0.008), (0.032, 0.0, -0.067), 2), ellipsoid((0.013, 0.011, 0.008), (-0.032, 0.0, -0.067), 2))
    irises = merge(ellipsoid((0.006, 0.006, 0.004), (0.032, 0.0, -0.074), 2), ellipsoid((0.006, 0.006, 0.004), (-0.032, 0.0, -0.074), 2))
    model.add("Eyes", eyes, "eye", (0, 0, 0), "Face")
    model.add("Irises", irises, "iris", (0, 0, 0), "Face")
    cap = superellipsoid((0.19, 0.12, 0.22), 0.7, (0.0, 0.075, 0.035))
    model.add("Cap", cap, "tint", (0, 0, 0), "Face")
    mask = merge(
        superellipsoid((0.15, 0.1, 0.06), 0.6, (0.0, -0.065, -0.06)),
        cylinder(0.003, (0.07, -0.04, -0.05), (0.086, -0.01, 0.03)),
        cylinder(0.003, (-0.07, -0.04, -0.05), (-0.086, -0.01, 0.03)),
    )
    model.add("Mask", mask, "mask", (0, 0, 0), "Face")
    return model


def _arm_segments() -> list[Model]:
    """Unit-length arm segments along +Y; the game stretches them between shoulder, elbow and wrist."""
    upper = Model("surgeon", "upper_arm")
    upper.add(
        "Sleeve",
        tube([(0, -0.5, 0), (0, -0.2, 0), (0, 0.2, 0), (0, 0.5, 0)], [(0.06, 0.055), (0.056, 0.052), (0.05, 0.048), (0.045, 0.043)], up=(0, 0, 1)),
        "tint",
    )
    # The gown's sleeve down to a knit cuff at the wrist; the wrist end sits inside the glove's cuff (+Y).
    fore = Model("surgeon", "forearm")
    fore.add(
        "Sleeve",
        tube([(0, -0.5, 0), (0, -0.1, 0), (0, 0.12, 0), (0, 0.24, 0)], [(0.05, 0.047), (0.047, 0.044), (0.038, 0.035), (0.029, 0.028)], up=(0, 0, 1)),
        "tint",
    )
    # Snug under the glove's cuff (tools/blender/hand.py), which the game keeps on this end of the sleeve.
    fore.add("WristCuff", tube([(0, 0.2, 0), (0, 0.35, 0), (0, 0.5, 0)], [(0.026, 0.026), (0.024, 0.024), (0.023, 0.023)], up=(0, 0, 1)), "knit")
    fore.add("Cuff", tube([(0, -0.5, 0), (0, -0.42, 0)], [(0.05, 0.047), (0.05, 0.047)], smooth=1, up=(0, 0, 1)), "tint")
    return [upper, fore]


def build() -> list[Model]:
    return [_body(), _head(), *_arm_segments()]
