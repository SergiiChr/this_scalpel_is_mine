"""Surgeon body, head, arm segments and the gloved hand.

Body: origin at the feet, facing -Z. Head: origin at eye level. Glove: origin at the grip, tool handle along -Z,
right hand (the game mirrors it for the left). Finger joints rotate around Z to curl around the handle.
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
    eyes = merge(ellipsoid((0.013, 0.011, 0.008), (0.032, 0.0, -0.067)), ellipsoid((0.013, 0.011, 0.008), (-0.032, 0.0, -0.067)))
    irises = merge(ellipsoid((0.006, 0.006, 0.004), (0.032, 0.0, -0.074)), ellipsoid((0.006, 0.006, 0.004), (-0.032, 0.0, -0.074)))
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
    fore = Model("surgeon", "forearm")
    fore.add(
        "Glove",
        tube([(0, -0.5, 0), (0, -0.1, 0), (0, 0.3, 0), (0, 0.5, 0)], [(0.045, 0.042), (0.043, 0.041), (0.034, 0.03), (0.028, 0.026)], up=(0, 0, 1)),
        "glove",
    )
    fore.add("Cuff", tube([(0, -0.5, 0), (0, -0.42, 0)], [(0.05, 0.047), (0.05, 0.047)], smooth=1, up=(0, 0, 1)), "tint")
    return [upper, fore]


def _finger(model: Model, name: str, knuckle: tuple[float, float, float], lengths: tuple[float, float, float], radius: float, parent: str) -> None:
    """Three segments pointing -X from the knuckle (open hand). Each joint's node origin is at its base."""
    x, y, z = knuckle
    base = parent
    for i, length in enumerate(lengths):
        r = radius * (1.0 - 0.12 * i)
        segment = tube([(x, y, z), (x - length * 0.5, y, z), (x - length, y, z)], [(r, r * 1.05), (r * 0.97, r), (r * 0.9, r * 0.95)], ring=12)
        tip = ellipsoid((r * 0.9, r * 0.9, r * 0.95), (x - length, y, z)) if i == 2 else None
        mesh = merge(segment, tip) if tip is not None else segment
        joint = f"{name}{i + 1}"
        model.add(joint, mesh, "glove", (x, y, z), base)
        base = joint
        x -= length


def _glove() -> Model:
    """Right hand, palm on the +X side of the handle, fingers ready to wrap over it."""
    model = Model("surgeon", "glove")
    palm = superellipsoid((0.03, 0.085, 0.09), 0.45, (0.028, 0.0, 0.005))
    wrist = tube([(0.03, 0.0, 0.05), (0.032, 0.0, 0.1)], [(0.03, 0.022), (0.031, 0.024)], smooth=1, up=(1, 0, 0))
    model.add("Palm", merge(palm, wrist), "glove")
    fingers = (
        ("Index", -0.03, 0.0095, (0.045, 0.027, 0.022)),
        ("Middle", -0.01, 0.01, (0.048, 0.03, 0.023)),
        ("Ring", 0.01, 0.0095, (0.045, 0.027, 0.021)),
        ("Pinky", 0.028, 0.008, (0.035, 0.021, 0.018)),
    )
    for name, z, radius, lengths in fingers:
        _finger(model, name, (0.018, 0.038, z), lengths, radius, "Palm")
    thumb_base = (0.022, -0.03, -0.035)
    thumb = tube([thumb_base, (0.0, -0.035, -0.055), (-0.02, -0.035, -0.07)], [(0.012, 0.012), (0.011, 0.011), (0.01, 0.01)], ring=12)
    model.add("Thumb", merge(thumb, ellipsoid((0.01, 0.01, 0.01), (-0.02, -0.035, -0.07))), "glove", thumb_base, "Palm")
    return model


def build() -> list[Model]:
    return [_body(), _head(), _glove(), *_arm_segments()]
