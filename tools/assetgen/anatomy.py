"""Organs you push aside and the things scenarios make you take out.

Organs are sized to a unit radius; the game scales them. Targets are centered on their origin.
"""

from __future__ import annotations

import numpy as np

from .geometry import Model, bumpy, cylinder, ellipsoid, extrude, lathe, merge, rotated, scaled, superellipsoid, tube


def _organs() -> list[Model]:
    bowel = Model("organs", "bowel")
    loops = []
    for i in range(4):
        angle = i * np.pi / 2
        center = (0.35 * np.cos(angle), 0.0, 0.35 * np.sin(angle))
        pts = [(center[0] + 0.45 * np.cos(t), 0.1 * np.sin(3 * t), center[2] + 0.45 * np.sin(t)) for t in np.linspace(0, 1.6 * np.pi, 7)]
        loops.append(tube(pts, [(0.22, 0.22)] * len(pts), ring=14))
    bowel.add("Bowel", scaled(merge(*loops), (0.9, 0.9, 0.9)), "organ")
    lobe = Model("organs", "lobe")
    lobe.add("Lobe", bumpy(superellipsoid((2.0, 0.9, 1.5), 0.8), 0.04, 3, 4.0), "organ")
    sac = Model("organs", "sac")
    sac.add("Sac", bumpy(ellipsoid((1.0, 0.7, 0.85), subdivisions=4), 0.05, 7, 5.0), "organ")
    return [bowel, lobe, sac]


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

    appendix = Model("targets", "appendix")
    appendix.add(
        "Appendix",
        tube(
            [(-0.035, 0, 0), (-0.01, 0.004, 0.006), (0.015, 0.0, 0.002), (0.035, -0.003, -0.006)],
            [(0.009, 0.009), (0.008, 0.008), (0.007, 0.007), (0.0065, 0.0065)],
            ring=14,
        ),
        "organ",
    )
    out.append(appendix)

    tumor = Model("targets", "tumor")
    tumor.add("Tumor", bumpy(ellipsoid((0.02, 0.016, 0.018), subdivisions=4), 0.004, 11, 260.0), "flesh")
    out.append(tumor)

    clot = Model("targets", "clot")
    clot.add("Clot", bumpy(ellipsoid((0.012, 0.007, 0.008), subdivisions=3), 0.002, 5, 400.0), "blood_bag")
    out.append(clot)

    figurine = Model("targets", "figurine")
    body = lathe(
        [(0.0, -0.045), (0.012, -0.045), (0.012, -0.036), (0.006, -0.034), (0.007, -0.01), (0.009, 0.01), (0.006, 0.026), (0.0065, 0.036), (0.0, 0.046)], 24
    )
    arms = rotated(tube([(-0.008, 0.012, 0.004), (0.0, 0.004, 0.008), (0.008, 0.012, 0.004)], [(0.0025, 0.0025)] * 3, ring=8), 0.0, (0, 1, 0))
    figurine.add("Statuette", rotated(merge(body, arms), np.pi / 2, (0, 0, 1)), "gold")
    out.append(figurine)

    bone = Model("targets", "bone")
    shaft = tube([(-0.09, 0, 0), (-0.03, 0.001, 0), (0.03, -0.001, 0), (0.09, 0, 0)], [(0.012, 0.013), (0.011, 0.012), (0.011, 0.012), (0.012, 0.013)], ring=16)
    ends = merge(ellipsoid((0.02, 0.018, 0.022), (-0.1, 0, 0)), ellipsoid((0.02, 0.018, 0.022), (0.1, 0, 0)))
    bone.add("Bone", merge(shaft, ends), "bone")
    out.append(bone)

    fragment = Model("targets", "fragment")
    jagged = tube([(-0.035, 0, 0), (0.0, 0, 0), (0.03, 0, 0)], [(0.012, 0.013), (0.011, 0.012), (0.012, 0.012)], ring=14)
    fragment.add("Fragment", merge(jagged, bumpy(ellipsoid((0.014, 0.013, 0.014), (-0.038, 0, 0)), 0.002, 2, 300.0)), "bone")
    out.append(fragment)

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
    return _organs() + _targets()
