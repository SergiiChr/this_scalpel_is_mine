"""Surgeon body and arm segments (the gloved hand comes from tools/blender, the head is a delivered model).

Body: origin at the feet, facing -Z. Arm segments: unit length, stretched by the game.
"""

from __future__ import annotations

from .geometry import Model, ellipsoid, merge, superellipsoid, tube


# Rig pivots/lengths match src/Surgeons/Surgeon.Body.cs's HipHeight, ThighLength, ShinLength, AnkleHeight and HipWidth.
# Neck top/collar samples and eye-relative HeadPivot are checked in tests/Models/SurgeonPoseTest.cs; update both
# consumers when changing these dimensions. The rig contract test reads the generated rest transforms.
def _body() -> Model:
    model = Model("surgeon", "body")
    torso = tube(
        [(0.0, 0.92, 0.0), (0.0, 1.05, 0.0), (0.0, 1.2, 0.0), (0.0, 1.35, 0.0), (0.0, 1.44, 0.0), (0.0, 1.5, 0.0)],
        [(0.12, 0.19), (0.115, 0.17), (0.125, 0.18), (0.13, 0.2), (0.11, 0.2), (0.06, 0.08)],
        ring=32,
        exponent=2.4,
        up=(0.0, 0.0, -1.0),
    )
    model.add("Torso", torso, "tint", (0.0, 0.95, 0.0))
    # The neck belongs to the torso, not the eye-level look pivot. Its base stays inside the collar.
    model.add(
        "Neck",
        tube([(0, 1.46, 0.015), (0, 1.51, 0.015), (0, 1.555, 0.02)], [(0.047, 0.048), (0.045, 0.046), (0.05, 0.05)], up=(0, 0, 1)),
        "skin",
        (0, 1.5, 0.015),
        "Torso",
    )
    # A rolled V collar and pocket give the scrubs readable tailoring without coplanar decoration.
    collar = tube(
        [(-0.075, 1.47, -0.065), (-0.045, 1.405, -0.12), (0, 1.365, -0.13), (0.045, 1.405, -0.12), (0.075, 1.47, -0.065)], [(0.007, 0.007)] * 5, ring=12
    )
    model.add("Collar", collar, "tint", (0, 1.4, -0.12), "Torso")
    pocket = superellipsoid((0.072, 0.078, 0.014), 0.35, (0.105, 1.27, -0.12), detail=12)
    model.add("Pocket", pocket, "tint", (0.105, 1.27, -0.12), "Torso")
    pelvis = superellipsoid((0.35, 0.19, 0.23), 0.75, (0, 0.955, 0), detail=16)
    model.add("Pelvis", pelvis, "tint", (0, 0.95, 0))
    for side, suffix in ((-1.0, "L"), (1.0, "R")):
        x = 0.115 * side
        # Separate thighs, knees and ankles: fixed-length segments can fold into a heels-down deep squat.
        thigh = merge(
            tube([(x, 0.95, 0), (x, 0.8, 0), (x, 0.63, 0), (x, 0.49, 0)], [(0.092, 0.084), (0.087, 0.08), (0.076, 0.07), (0.064, 0.063)], up=(0, 0, 1)),
            ellipsoid((0.065, 0.065, 0.065), (x, 0.49, 0), 2),
        )
        model.add(f"Leg{suffix}", thigh, "tint", (x, 0.95, 0))
        shin = tube([(x, 0.49, 0), (x, 0.38, 0), (x, 0.22, 0), (x, 0.08, 0)], [(0.064, 0.063), (0.067, 0.065), (0.054, 0.052), (0.045, 0.046)], up=(0, 0, 1))
        model.add(f"Shin{suffix}", shin, "tint", (x, 0.49, 0), f"Leg{suffix}")
        shoe = superellipsoid((0.13, 0.09, 0.27), 0.55, (x, 0.058, -0.055), detail=14)
        model.add(f"Shoe{suffix}", shoe, "rubber", (x, 0.08, 0), f"Shin{suffix}")
        sole = superellipsoid((0.135, 0.024, 0.275), 0.35, (x, 0.015, -0.055), detail=12)
        model.add(f"Sole{suffix}", sole, "black_plastic", (x, 0.08, 0), f"Shoe{suffix}")
    return model


def _arm_segments() -> list[Model]:
    """Unit-length arm segments along +Y; the game stretches them between shoulder, elbow and wrist."""
    upper = Model("surgeon", "upper_arm")
    upper.add(
        "Sleeve",
        # Round the shoulder into the torso instead of exposing a flat cylindrical end cap.
        tube(
            [(0, -0.64, 0), (0, -0.59, 0), (0, -0.5, 0), (0, -0.2, 0), (0, 0.2, 0), (0, 0.5, 0)],
            [(0.014, 0.013), (0.05, 0.047), (0.064, 0.059), (0.056, 0.052), (0.05, 0.048), (0.045, 0.043)],
            up=(0, 0, 1),
        ),
        "tint",
    )
    # A rounded elbow covers both sleeve end caps when the forearm folds sharply.
    # Its own part: the player's own arms show only the elbow, which ends the forearm.
    upper.add("Elbow", ellipsoid((0.051, 0.16, 0.049), (0, 0.5, 0), 2), "tint")
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
    return [_body(), *_arm_segments()]
