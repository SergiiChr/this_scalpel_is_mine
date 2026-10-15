"""The patient's skeleton, inside the body (tools/blender/patient.py) and riding the same rig. Only X-ray films show it.

One part per rig bone, named Skeleton<Bone>, pinned rigidly to that bone. Shapes are simplified: what a film shows is
the outline and where bone is thick, not anatomical detail. Same space as the patient: +X toward the head, +Z the
patient's left, the back resting at about y -0.09.
"""

from __future__ import annotations

import bpy

from . import scene
from .scene import Blob, Vec3, capsule, ellipsoid

BONE = (0.93, 0.9, 0.82)
# Triangles per part, about 9.6k in all: the skull and the ribcage are big on a film, the limb bones simple tubes.
BUDGETS = {"Head": 1200, "Jaw": 300, "Neck": 500, "Chest": 3000, "Torso": 1200}
BUDGETS |= {f"{bone}{s}": 250 if bone in ("UpperArm", "Forearm") else 300 for bone in ("UpperArm", "Forearm", "Hand", "Thigh", "Shin", "Foot") for s in "LR"}
# How far below the skin of the back the spine runs, and its line from the pelvis to the skull.
SPINE_Y = -0.05


def _m(p: Vec3, side: float) -> Vec3:
    return (p[0], p[1], p[2] * side)


def _vertebrae(start: float, end: float, count: int, y: float, size: float) -> list[Blob]:
    """Vertebral bodies with their spines pointing back toward the table, evenly spaced from start to end along X."""
    out: list[Blob] = []
    for i in range(count):
        x = start + (end - start) * (i + 0.5) / count
        out.append(ellipsoid((x, y, 0.0), (size * 0.8, size * 0.8, size * 1.1)))
        out.append(capsule((x, y - size * 0.5, 0.0), (x - size * 0.4, y - size * 1.6, 0.0), size * 0.22, 2.5))
        for side in (1.0, -1.0):
            out.append(capsule((x, y - size * 0.3, 0.0), (x, y - size * 0.5, size * 1.4 * side), size * 0.18, 2.5))
    return out


def _skull() -> list[Blob]:
    """A shell: the dome hollowed out by a negative core, so a film shows its rim brighter than its middle."""
    out = [
        ellipsoid((0.705, 0.0, 0.0), (0.085, 0.092, 0.07)),
        ellipsoid((0.735, -0.018, 0.0), (0.052, 0.078, 0.068)),
        ellipsoid((0.705, -0.004, 0.0), (0.068, 0.072, 0.052), -2.0),
        ellipsoid((0.73, -0.02, 0.0), (0.036, 0.058, 0.05), -2.0),
        # Upper jaw and cheekbones, the nasal bones down the bridge.
        ellipsoid((0.625, 0.06, 0.0), (0.03, 0.028, 0.035)),
        capsule((0.684, 0.09, 0.0), (0.66, 0.1, 0.0), 0.005, 3.0),
    ]
    for side in (1.0, -1.0):
        out += [
            capsule(_m((0.665, 0.07, 0.05), side), _m((0.655, 0.02, 0.066), side), 0.008, 2.5),
            # Eye sockets.
            ellipsoid(_m((0.675, 0.085, 0.032), side), (0.017, 0.02, 0.017), -2.5),
        ]
    return out


def _mandible() -> list[Blob]:
    points = [(0.66, 0.0, 0.048), (0.605, 0.018, 0.046), (0.578, 0.058, 0.026), (0.572, 0.072, 0.0)]
    out: list[Blob] = []
    for side in (1.0, -1.0):
        out += [capsule(_m(a, side), _m(b, side), 0.0065, 2.5) for a, b in zip(points, points[1:], strict=False)]
    return out


def _rib(spine_x: float, width: float, drop: float, front: float) -> list[Blob]:
    """One pair of ribs: out from the spine, round the side and forward and down toward the breastbone."""
    out: list[Blob] = []
    for side in (1.0, -1.0):
        path = [
            (spine_x, SPINE_Y + 0.01, 0.02),
            (spine_x - drop * 0.15, SPINE_Y + 0.0, width * 0.75),
            (spine_x - drop * 0.4, SPINE_Y + 0.045, width),
            (spine_x - drop * 0.7, SPINE_Y + 0.1, width * 0.75),
            (spine_x - drop, SPINE_Y + 0.13, front),
        ]
        out += [capsule(_m(a, side), _m(b, side), 0.0045, 3.0) for a, b in zip(path, path[1:], strict=False)]
    return out


def _chest() -> list[Blob]:
    out = _vertebrae(0.06, 0.42, 12, SPINE_Y, 0.016)
    # (spine x, half width, how far the rib drops toward the feet on its way forward, how close to the middle it ends)
    ribs = [(0.4, 0.08, 0.03, 0.03), (0.375, 0.11, 0.06, 0.03), (0.35, 0.13, 0.08, 0.03), (0.325, 0.145, 0.09, 0.035)]
    ribs += [(0.3, 0.155, 0.1, 0.04), (0.275, 0.16, 0.11, 0.045), (0.25, 0.163, 0.12, 0.05), (0.225, 0.163, 0.12, 0.07)]
    ribs += [(0.2, 0.16, 0.11, 0.09), (0.175, 0.155, 0.1, 0.11), (0.15, 0.145, 0.06, 0.13), (0.125, 0.13, 0.04, 0.13)]
    for rib in ribs:
        out += _rib(*rib)
    out += [
        # Breastbone: the handle at the top, the blade down the middle, the small tip.
        ellipsoid((0.355, 0.075, 0.0), (0.025, 0.008, 0.022)),
        capsule((0.335, 0.08, 0.0), (0.2, 0.085, 0.0), 0.011, 3.0),
        ellipsoid((0.19, 0.083, 0.0), (0.012, 0.005, 0.008)),
    ]
    for side in (1.0, -1.0):
        out += [
            capsule(_m((0.365, 0.05, 0.022), side), _m((0.36, 0.03, 0.175), side), 0.007, 3.0),
            # Shoulder blade, flat against the back, and its socket for the arm.
            ellipsoid(_m((0.29, -0.065, 0.12), side), (0.07, 0.008, 0.05)),
            ellipsoid(_m((0.355, -0.03, 0.19), side), (0.015, 0.018, 0.012)),
        ]
    return out


def _neck() -> list[Blob]:
    return _vertebrae(0.43, 0.6, 7, -0.022, 0.012)


def _pelvis() -> list[Blob]:
    out = _vertebrae(-0.24, 0.06, 5, SPINE_Y, 0.024)
    # The sacrum closing the back of the ring, and the pubic bones meeting in front.
    out += [ellipsoid((-0.3, -0.06, 0.0), (0.05, 0.015, 0.04)), ellipsoid((-0.395, 0.025, 0.0), (0.008, 0.012, 0.012))]
    for side in (1.0, -1.0):
        socket = _m((-0.36, -0.015, 0.09), side)
        out += [
            # The wing of the hip bone, flat and broad, with its thick crest.
            ellipsoid(_m((-0.26, -0.035, 0.105), side), (0.065, 0.012, 0.06)),
            capsule(_m((-0.2, -0.03, 0.065), side), _m((-0.22, -0.02, 0.155), side), 0.008, 3.0),
            capsule(_m((-0.3, -0.04, 0.06), side), socket, 0.014, 2.5),
            # The socket, then the two rami round the hole below it.
            ellipsoid(socket, (0.022, 0.022, 0.012)),
            capsule(socket, (-0.395, 0.025, 0.006 * side), 0.007, 3.0),
            capsule(socket, _m((-0.42, -0.02, 0.06), side), 0.009, 3.0),
            capsule(_m((-0.42, -0.02, 0.06), side), (-0.4, 0.015, 0.006 * side), 0.006, 3.0),
        ]
    return out


def _humerus(side: float) -> list[Blob]:
    return [
        ellipsoid(_m((0.365, -0.008, 0.21), side), (0.02, 0.02, 0.02)),
        capsule(_m((0.35, -0.01, 0.22), side), _m((0.145, -0.024, 0.25), side), 0.0105, 3.0),
        ellipsoid(_m((0.138, -0.026, 0.25), side), (0.013, 0.012, 0.024)),
    ]


def _forearm(side: float) -> list[Blob]:
    return [
        capsule(_m((0.128, -0.03, 0.258), side), _m((-0.13, -0.037, 0.266), side), 0.006, 3.0),
        capsule(_m((0.12, -0.026, 0.242), side), _m((-0.13, -0.036, 0.247), side), 0.0065, 3.0),
        ellipsoid(_m((0.135, -0.035, 0.258), side), (0.012, 0.01, 0.01)),
    ]


def _hand(side: float) -> list[Blob]:
    out = [ellipsoid(_m((-0.148, -0.042, 0.258), side), (0.014, 0.007, 0.022))]
    for dz, length in ((-0.027, 0.075), (-0.009, 0.082), (0.009, 0.077), (0.026, 0.062)):
        z = 0.26 + dz
        base, knuckle = (-0.16, -0.046, z * 0.6 + 0.258 * 0.4), (-0.218, -0.047, z)
        tip = (-0.225 - length * 0.92, -0.06, z)
        out += [capsule(_m(base, side), _m(knuckle, side), 0.0038, 3.0), capsule(_m(knuckle, side), _m(tip, side), 0.0032, 3.0)]
    thumb = [(-0.15, -0.046, 0.24), (-0.168, -0.048, 0.226), (-0.19, -0.05, 0.212), (-0.214, -0.053, 0.204)]
    out += [capsule(_m(a, side), _m(b, side), 0.0035, 3.0) for a, b in zip(thumb, thumb[1:], strict=False)]
    return out


def _femur(side: float) -> list[Blob]:
    return [
        ellipsoid(_m((-0.36, -0.015, 0.088), side), (0.021, 0.021, 0.021)),
        capsule(_m((-0.36, -0.015, 0.09), side), _m((-0.385, -0.018, 0.125), side), 0.011, 3.0),
        ellipsoid(_m((-0.38, -0.02, 0.135), side), (0.018, 0.016, 0.012)),
        capsule(_m((-0.39, -0.018, 0.125), side), _m((-0.78, -0.03, 0.105), side), 0.013, 3.0),
        ellipsoid(_m((-0.79, -0.033, 0.105), side), (0.02, 0.019, 0.03)),
    ]


def _shin(side: float) -> list[Blob]:
    return [
        ellipsoid(_m((-0.8, 0.008, 0.105), side), (0.014, 0.006, 0.013)),
        ellipsoid(_m((-0.82, -0.034, 0.104), side), (0.016, 0.017, 0.027)),
        capsule(_m((-0.83, -0.033, 0.1), side), _m((-1.18, -0.048, 0.104), side), 0.012, 3.0),
        capsule(_m((-0.83, -0.045, 0.127), side), _m((-1.19, -0.055, 0.13), side), 0.005, 3.0),
    ]


def _foot(side: float) -> list[Blob]:
    out = [
        ellipsoid(_m((-1.225, -0.065, 0.11), side), (0.024, 0.018, 0.017)),
        ellipsoid(_m((-1.205, -0.035, 0.11), side), (0.016, 0.014, 0.017)),
    ]
    for dz in (-0.024, -0.012, 0.0, 0.012, 0.024):
        out += [
            capsule(_m((-1.225, -0.01, 0.11 + dz * 0.5), side), _m((-1.24, 0.085, 0.114 + dz), side), 0.0038, 3.0),
            capsule(_m((-1.24, 0.09, 0.114 + dz), side), _m((-1.245, 0.125, 0.116 + dz), side), 0.003, 3.0),
        ]
    return out


def _parts() -> dict[str, list[Blob]]:
    parts = {"Head": _skull(), "Jaw": _mandible(), "Neck": _neck(), "Chest": _chest(), "Torso": _pelvis()}
    for side, s in ((1.0, "L"), (-1.0, "R")):
        parts |= {
            f"UpperArm{s}": _humerus(side),
            f"Forearm{s}": _forearm(side),
            f"Hand{s}": _hand(side),
            f"Thigh{s}": _femur(side),
            f"Shin{s}": _shin(side),
            f"Foot{s}": _foot(side),
        }
    return parts


def build() -> list[tuple[bpy.types.Object, str]]:
    """The skeleton's parts, each with the rig bone it rides. Call before the patient's scene.clean()."""
    material = scene.material("bone", BONE, roughness=0.55)
    built = []
    for bone, elements in _parts().items():
        obj = scene.blobs(f"Skeleton{bone}", elements, resolution=0.003)
        scene.fit(obj, BUDGETS[bone])
        scene.finish(obj, material)
        built.append((obj, bone))
    return built
