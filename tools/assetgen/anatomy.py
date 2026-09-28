"""Non-anatomical targets scenarios make you take out: bullets, a knife, a figurine, fluid and trapped air.
Centered on their origin. Organs and anatomical targets come from tools/blender.
"""

from __future__ import annotations

import numpy as np

from .geometry import Model, bumpy, cylinder, ellipsoid, extrude, lathe, merge, rotated, scaled, superellipsoid, tube


def _targets() -> list[Model]:
    out = []
    bullet = Model("targets", "bullet")
    slug = lathe([(0.0, -0.009), (0.0045, -0.009), (0.0048, 0.0), (0.004, 0.005), (0.0022, 0.008), (0.0, 0.0095)], 20)
    bullet.add("Slug", rotated(slug, np.pi / 2, (0, 0, 1)), "brass")
    out.append(bullet)

    knife = Model("targets", "knife")
    blade = extrude([(-0.004, 0.0), (0.012, 0.0), (0.014, -0.06), (0.004, -0.1), (-0.004, -0.09)], 0.002)
    knife.add("Blade", rotated(blade, np.pi / 2, (0, 1, 0)), "steel")
    handle = superellipsoid((0.012, 0.11, 0.026), 0.5, (0.0, 0.055, 0.004))
    rivets = merge(*[cylinder(0.0025, (-0.007, y, 0.004), (0.007, y, 0.004), 8) for y in (0.03, 0.08)])
    knife.add("Handle", handle, "wood")
    knife.add("Rivets", rivets, "brass")
    out.append(knife)

    figurine = Model("targets", "figurine")
    body = lathe(
        [(0.0, -0.045), (0.012, -0.045), (0.012, -0.036), (0.006, -0.034), (0.007, -0.01), (0.009, 0.01), (0.006, 0.026), (0.0065, 0.036), (0.0, 0.046)], 24
    )
    arms = rotated(tube([(-0.008, 0.012, 0.004), (0.0, 0.004, 0.008), (0.008, 0.012, 0.004)], [(0.0025, 0.0025)] * 3, ring=8), 0.0, (0, 1, 0))
    figurine.add("Statuette", rotated(merge(body, arms), np.pi / 2, (0, 0, 1)), "gold")
    out.append(figurine)

    fluid = Model("targets", "fluid")
    fluid.add("Fluid", scaled(bumpy(ellipsoid((0.03, 0.012, 0.025), subdivisions=4), 0.002, 9, 200.0), (1, 1, 1)), "iodine")
    out.append(fluid)

    air = Model("targets", "air")
    air.add(
        "Pocket",
        merge(*[ellipsoid((r, r, r), c) for r, c in ((0.012, (0, 0, 0)), (0.008, (0.014, 0.004, 0.006)), (0.006, (-0.012, -0.002, 0.008)))]),
        "clear_plastic",
    )
    out.append(air)
    return out


def build() -> list[Model]:
    return _targets()
