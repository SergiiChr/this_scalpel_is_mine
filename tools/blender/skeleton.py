"""The patient's skeleton, inside the body (tools/blender/patient.py) and riding the same rig. Only X-ray films show it.

One part per rig bone, named Skeleton<Bone>, pinned rigidly to that bone. Shapes are simplified: what a film shows is
the outline and where bone is thick, not anatomical detail. Every bone is placed from MakeHuman's joints, or from the
body's cross section where the joints don't say (how deep the spine lies, how wide the ribs reach).
Same space as the patient: +X toward the head, +Y up out of the face, +Z the patient's left.
"""

from __future__ import annotations

import bpy
import numpy as np
from numpy.typing import NDArray

from . import scene
from .makehuman import Man
from .scene import Blob, Vec3, capsule, ellipsoid

BONE = (0.93, 0.9, 0.82)
# Triangles per part, about 9.6k in all: the skull and the ribcage are big on a film, the limb bones simple tubes.
BUDGETS = {"Head": 1200, "Jaw": 300, "Neck": 500, "Chest": 3000, "Torso": 1200}
BUDGETS |= {f"{bone}{s}": 250 if bone in ("UpperArm", "Forearm") else 300 for bone in ("UpperArm", "Forearm", "Hand", "Thigh", "Shin", "Foot") for s in "LR"}
# Where each run of vertebrae starts and ends along the body (x): lumbar from the sacrum up, thoracic, cervical up to
# the skull. The thoracic ones carry the twelve pairs of ribs.
LUMBAR, THORACIC, CERVICAL = (0.07, 0.25), (0.25, 0.51), (0.51, 0.645)
# How far the vertebral bodies lie in front of the skin of the back, along each run.
SPINE_DEPTH = {"lumbar": 0.05, "thoracic": 0.042, "cervical": 0.033}
# How wide each pair of ribs reaches, top to bottom, as a share of the widest, and how far forward toward the
# breastbone it ends (half the gap, metres). The lowest ribs float.
RIB_WIDTH = (0.5, 0.67, 0.8, 0.89, 0.95, 0.98, 1.0, 1.0, 0.98, 0.95, 0.89, 0.8)
RIB_END = (0.025, 0.025, 0.025, 0.03, 0.035, 0.04, 0.045, 0.06, 0.08, 0.1, 0.12, 0.12)
# How far each rib drops toward the feet on its way round to the front.
RIB_DROP = (0.025, 0.05, 0.068, 0.076, 0.085, 0.093, 0.1, 0.1, 0.093, 0.085, 0.05, 0.034)
# The breastbone runs from the notch at the top of the chest down to its tip (x).
STERNUM = (0.475, 0.29)


def _m(p: Vec3, side: float) -> Vec3:
    return (p[0], p[1], p[2] * side)


def _v(p: NDArray[np.float64]) -> Vec3:
    return (float(p[0]), float(p[1]), float(p[2]))


class _Body:
    """The trunk's cross sections along x: where its skin lies, without the arms beside it."""

    def __init__(self, man: Man) -> None:
        q = man.quads
        edges = np.unique(np.sort(np.concatenate([q[:, [0, 1]], q[:, [1, 2]], q[:, [2, 3]], q[:, [3, 0]]]), axis=1), axis=0)
        trunk = man.weights["Torso"] + man.weights["Chest"] + man.weights["Neck"] + man.weights["Head"] + man.weights["Jaw"]
        self.a, self.b = man.verts[edges[:, 0]], man.verts[edges[:, 1]]
        self.trunk = (trunk[edges[:, 0]] + trunk[edges[:, 1]]) * 0.5 > 0.5

    def _skin(self, x: float, z: float | None = None) -> NDArray[np.float64]:
        """Where the skin crosses the plane at x, near z when given."""
        t = (x - self.a[:, 0]) / (self.b[:, 0] - self.a[:, 0] + 1e-12)
        crossing = (t >= 0.0) & (t <= 1.0) & self.trunk
        p = self.a[crossing] + (self.b[crossing] - self.a[crossing]) * t[crossing, None]
        return np.asarray(p if z is None else p[np.abs(p[:, 2] - z) < 0.01], dtype=np.float64)

    def half_width(self, x: float) -> float:
        return float(np.abs(self._skin(x)[:, 2]).max())

    def front(self, x: float, z: float = 0.0) -> float:
        return float(self._skin(x, z)[:, 1].max())

    def back(self, x: float, z: float = 0.0) -> float:
        return float(self._skin(x, z)[:, 1].min())


def _vertebrae(body: _Body, run: tuple[float, float], count: int, depth: float, size: float) -> list[Blob]:
    """Vertebral bodies with their spines pointing back toward the table, evenly spaced along the run."""
    out: list[Blob] = []
    for i in range(count):
        x = run[0] + (run[1] - run[0]) * (i + 0.5) / count
        y = body.back(x) + depth
        out.append(ellipsoid((x, y, 0.0), (size * 0.8, size * 0.8, size * 1.1)))
        out.append(capsule((x, y - size * 0.5, 0.0), (x - size * 0.4, y - size * 1.6, 0.0), size * 0.22, 2.5))
        for side in (1.0, -1.0):
            out.append(capsule((x, y - size * 0.3, 0.0), (x, y - size * 0.5, size * 1.4 * side), size * 0.18, 2.5))
    return out


def _skull(man: Man) -> list[Blob]:
    """A shell: the dome hollowed out by a negative core, so a film shows its rim brighter than its middle."""
    eye = man.joints["l-eye"]
    crown = float(man.verts[:, 0].max())
    # The cranium follows the head's own ellipsoid, 6 mm in under the scalp, centered between forehead and nape.
    center = (crown - 0.078, 0.043, 0.0)
    out = [
        ellipsoid(center, (0.071, 0.093, 0.068)),
        ellipsoid(center, (0.054, 0.072, 0.05), -2.0),
        # Upper jaw above the teeth and the nasal bones down the bridge.
        ellipsoid((0.66, 0.12, 0.0), (0.02, 0.018, 0.03)),
        capsule((eye[0] + 0.006, 0.128, 0.0), (eye[0] - 0.018, 0.138, 0.0), 0.0035, 3.0),
    ]
    for side in (1.0, -1.0):
        out += [
            capsule(_m((eye[0] - 0.012, 0.11, 0.046), side), _m((eye[0] - 0.016, 0.065, 0.056), side), 0.006, 2.5),
            # Eye sockets.
            ellipsoid(_m(_v(eye), side), (0.019, 0.021, 0.019), -2.5),
        ]
    return out


def _mandible(man: Man) -> list[Blob]:
    """From the joint in front of each ear down to the angle of the jaw and round to the chin."""
    hinge, chin = man.joints["mouth"], man.joints["jaw"]
    points = [
        (hinge[0] + 0.015, hinge[1], 0.05),
        (hinge[0] - 0.05, hinge[1], 0.042),
        (chin[0] - 0.004, chin[1] - 0.03, 0.028),
        (chin[0] - 0.012, chin[1] - 0.012, 0.0),
    ]
    out: list[Blob] = []
    for side in (1.0, -1.0):
        out += [capsule(_m(_v(np.array(a)), side), _m(_v(np.array(b)), side), 0.0058, 2.5) for a, b in zip(points, points[1:], strict=False)]
    return out


def _rib(body: _Body, x: float, width: float, end: float, drop: float) -> list[Blob]:
    """One pair of ribs: out from the spine, round the side and forward and down toward the breastbone."""
    out: list[Blob] = []
    back = body.back(x) + SPINE_DEPTH["thoracic"]
    front = body.front(x - drop, end) - 0.016
    deep = front - back
    for side in (1.0, -1.0):
        path = [
            (x, back - 0.006, 0.018),
            (x - drop * 0.15, back - 0.012, width * 0.75),
            (x - drop * 0.4, back + deep * 0.32, width),
            (x - drop * 0.7, back + deep * 0.72, width * 0.78),
            (x - drop, front, end),
        ]
        out += [capsule(_m(a, side), _m(b, side), 0.0042, 3.0) for a, b in zip(path, path[1:], strict=False)]
    return out


def _chest(man: Man, body: _Body) -> list[Blob]:
    out = _vertebrae(body, THORACIC, 12, SPINE_DEPTH["thoracic"], 0.014)
    # The widest ribs reach to within 1.5 cm of the skin at the sides.
    widest = min(body.half_width(x) for x in np.linspace(0.27, 0.37, 6)) - 0.015
    step = (THORACIC[1] - THORACIC[0]) / 12
    for i, (width, end, drop) in enumerate(zip(RIB_WIDTH, RIB_END, RIB_DROP, strict=True)):
        out += _rib(body, THORACIC[1] - step * (i + 0.5), widest * width, end, drop)
    top, tip = STERNUM
    out += [
        # Breastbone: the handle at the top, the blade down the middle, the small tip.
        ellipsoid((top - 0.02, body.front(top - 0.02) - 0.012, 0.0), (0.022, 0.007, 0.02)),
        capsule((top - 0.04, body.front(top - 0.04) - 0.012, 0.0), (tip + 0.01, body.front(tip + 0.01) - 0.014, 0.0), 0.009, 3.0),
        ellipsoid((tip, body.front(tip) - 0.016, 0.0), (0.01, 0.004, 0.007)),
    ]
    shoulder = man.joints["l-shoulder"]
    for side in (1.0, -1.0):
        out += [
            # Collarbone from the top of the breastbone out to the shoulder.
            capsule(
                _m((top - 0.005, body.front(top, 0.02) - 0.01, 0.02), side),
                _m((shoulder[0] + 0.012, shoulder[1] + 0.02, shoulder[2] - 0.012), side),
                0.0062,
                3.0,
            ),
            # Shoulder blade, flat against the back, and its socket for the arm.
            ellipsoid(_m((shoulder[0] - 0.065, body.back(shoulder[0] - 0.065, shoulder[2] * 0.5) + 0.014, shoulder[2] * 0.5), side), (0.06, 0.007, 0.038)),
            ellipsoid(_m((shoulder[0], shoulder[1] - 0.01, shoulder[2] - 0.02), side), (0.013, 0.016, 0.01)),
        ]
    return out


def _neck(body: _Body) -> list[Blob]:
    return _vertebrae(body, CERVICAL, 7, SPINE_DEPTH["cervical"], 0.011)


def _pelvis(man: Man, body: _Body) -> list[Blob]:
    hip = man.joints["l-upper-leg"]
    x, y, z = (float(c) for c in hip)
    out = _vertebrae(body, LUMBAR, 5, SPINE_DEPTH["lumbar"], 0.02)
    sacrum = x + 0.045
    # The sacrum closing the back of the ring, and the pubic bones meeting in front.
    out += [ellipsoid((sacrum, body.back(sacrum) + 0.028, 0.0), (0.045, 0.013, 0.036)), ellipsoid((x - 0.03, y + 0.06, 0.0), (0.007, 0.011, 0.011))]
    # The hip bone's crest reaches to within 2 cm of the skin of the flank.
    crest = body.half_width(x + 0.12) - 0.022
    for side in (1.0, -1.0):
        socket = _m((x, y, z - 0.005), side)
        ischium = _m((x - 0.045, y - 0.012, z * 0.55), side)
        out += [
            # The wing of the hip bone, flat and broad, with its thick crest.
            ellipsoid(_m((x + 0.08, y - 0.02, z * 0.78), side), (0.055, 0.011, 0.035)),
            capsule(_m((x + 0.135, y - 0.025, z * 0.5), side), _m((x + 0.115, y - 0.005, crest), side), 0.007, 3.0),
            capsule(_m((x + 0.05, y - 0.03, z * 0.6), side), socket, 0.012, 2.5),
            # The socket, then the two rami round the hole below it.
            ellipsoid(socket, (0.019, 0.019, 0.011)),
            capsule(socket, (x - 0.03, y + 0.058, 0.006 * side), 0.006, 3.0),
            capsule(socket, ischium, 0.008, 3.0),
            capsule(ischium, (x - 0.035, y + 0.05, 0.006 * side), 0.005, 3.0),
        ]
    return out


def _humerus(man: Man, s: str) -> list[Blob]:
    shoulder, elbow = man.joints[f"{s}-shoulder"], man.joints[f"{s}-elbow"]
    return [
        ellipsoid(_v(shoulder), (0.018, 0.018, 0.018)),
        capsule(_v(shoulder + (elbow - shoulder) * 0.06), _v(elbow + (shoulder - elbow) * 0.06), 0.009, 3.0),
        ellipsoid(_v(elbow), (0.011, 0.01, 0.021)),
    ]


def _forearm(man: Man, s: str, side: float) -> list[Blob]:
    elbow, wrist = man.joints[f"{s}-elbow"], man.joints[f"{s}-hand"]
    # The palm lies down, so the radius crosses over the ulna from the outside of the elbow to the thumb side of the wrist.
    out_elbow, in_wrist = np.array((0.0, -0.003, 0.009 * side)), np.array((0.0, -0.002, -0.009 * side))
    return [
        capsule(_v(elbow - out_elbow), _v(wrist - in_wrist), 0.0052, 3.0),
        capsule(_v(elbow + out_elbow), _v(wrist + in_wrist), 0.0056, 3.0),
        ellipsoid(_v(elbow + (0.006, -0.01, 0.0)), (0.01, 0.009, 0.009)),
    ]


def _hand(man: Man, s: str) -> list[Blob]:
    """Carpals, then each digit's metacarpal and phalanges along MakeHuman's finger joints."""
    j = man.joints
    wrist = j[f"{s}-hand"]
    knuckles = np.array([j[f"{s}-finger-{f}-1"] for f in range(2, 6)])
    carpals = wrist + (knuckles.mean(axis=0) - wrist) * 0.22
    out = [ellipsoid(_v(carpals), (0.014, 0.007, 0.02))]
    for f in range(1, 6):
        chain = [j[f"{s}-finger-{f}-{k}"] for k in range(1, 5)]
        if f > 1:
            chain.insert(0, wrist + (chain[0] - wrist) * 0.35)
        # The last joint is the fingertip's skin: the bone stops short of it. MakeHuman's finger joints lie toward the
        # back of the hand, so the bones drop a little toward the palm.
        chain[-1] = chain[-2] + (chain[-1] - chain[-2]) * 0.7
        chain = [p + (0.0, -0.003, 0.0) for p in chain]
        radii = np.linspace(0.0042, 0.0028, len(chain) - 1)
        out += [capsule(_v(a), _v(b), float(r), 3.0) for a, b, r in zip(chain, chain[1:], radii, strict=False)]
    return out


def _femur(man: Man, s: str, side: float) -> list[Blob]:
    hip, knee = man.joints[f"{s}-upper-leg"], man.joints[f"{s}-knee"]
    trochanter = hip + (-0.025, -0.004, 0.032 * side)
    return [
        ellipsoid(_v(hip), (0.02, 0.02, 0.02)),
        capsule(_v(hip), _v(trochanter), 0.01, 3.0),
        ellipsoid(_v(trochanter), (0.016, 0.014, 0.011)),
        capsule(_v(trochanter + (-0.02, 0.0, -0.01 * side)), _v(knee + (0.03, -0.003, 0.0)), 0.0115, 3.0),
        ellipsoid(_v(knee + (0.012, -0.004, 0.0)), (0.018, 0.017, 0.026)),
    ]


def _shin(man: Man, s: str, side: float) -> list[Blob]:
    knee, ankle = man.joints[f"{s}-knee"], man.joints[f"{s}-ankle"]
    return [
        # Kneecap, the top of the shinbone, its shaft and the thin fibula on the outside.
        ellipsoid(_v(knee + (0.012, 0.03, 0.0)), (0.017, 0.006, 0.016)),
        ellipsoid(_v(knee + (-0.012, -0.004, 0.0)), (0.014, 0.015, 0.025)),
        capsule(_v(knee + (-0.02, 0.0, -0.003 * side)), _v(ankle + (0.012, 0.0, -0.004 * side)), 0.0105, 3.0),
        capsule(_v(knee + (-0.025, -0.01, 0.022 * side)), _v(ankle + (0.005, -0.004, 0.02 * side)), 0.0045, 3.0),
    ]


def _foot(man: Man, s: str) -> list[Blob]:
    """Heel bone and ankle bone, then each toe's metatarsal and phalanges along MakeHuman's toe joints. The foot points up."""
    j = man.joints
    ankle = j[f"{s}-ankle"]
    out = [ellipsoid(_v(ankle + (-0.042, -0.018, 0.0)), (0.02, 0.026, 0.015)), ellipsoid(_v(ankle + (-0.008, 0.008, 0.0)), (0.014, 0.014, 0.015))]
    midfoot = ankle + (-0.035, 0.045, 0.0)
    for t in range(1, 6):
        chain = [midfoot + (j[f"{s}-toe-{t}-1"] - midfoot) * 0.25] + [j[f"{s}-toe-{t}-{k}"] for k in range(1, 5) if f"{s}-toe-{t}-{k}" in j]
        chain[-1] = chain[-2] + (chain[-1] - chain[-2]) * 0.7
        radii = np.linspace(0.0045 if t == 1 else 0.0035, 0.0028, len(chain) - 1)
        out += [capsule(_v(a), _v(b), float(r), 3.0) for a, b, r in zip(chain, chain[1:], radii, strict=False)]
    return out


def _parts(man: Man) -> dict[str, list[Blob]]:
    body = _Body(man)
    parts = {"Head": _skull(man), "Jaw": _mandible(man), "Neck": _neck(body), "Chest": _chest(man, body), "Torso": _pelvis(man, body)}
    for side, s, m in ((1.0, "L", "l"), (-1.0, "R", "r")):
        parts |= {
            f"UpperArm{s}": _humerus(man, m),
            f"Forearm{s}": _forearm(man, m, side),
            f"Hand{s}": _hand(man, m),
            f"Thigh{s}": _femur(man, m, side),
            f"Shin{s}": _shin(man, m, side),
            f"Foot{s}": _foot(man, m),
        }
    return parts


def build(man: Man) -> list[tuple[bpy.types.Object, str]]:
    """The skeleton's parts, each with the rig bone it rides. Call before the patient's scene.clean()."""
    material = scene.material("bone", BONE, roughness=0.55)
    built = []
    for bone, elements in _parts(man).items():
        obj = scene.blobs(f"Skeleton{bone}", elements, resolution=0.003)
        scene.fit(obj, BUDGETS[bone])
        scene.finish(obj, material)
        built.append((obj, bone))
    return built
