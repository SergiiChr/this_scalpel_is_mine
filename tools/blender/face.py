"""The patient's face parts, fitted to MakeHuman's head (tools/blender/makehuman.py): eyes, closed lids, brows, lips,
the mouth's lining, teeth and tongue.

Head landmarks in game space (patient lying face up): chin 0.58, nose base 0.642, lips meeting at 0.627, eye centers
0.698, crown 0.806 along x; y is out of the face, z is the patient's left.
"""

from __future__ import annotations

import bpy
import numpy as np
from mathutils.bvhtree import BVHTree
from numpy.typing import NDArray

from . import scene
from .makehuman import Man
from .scene import Vec3, dab, ellipsoid

HAIR = (0.12, 0.09, 0.07)


def _m(p: Vec3, side: float) -> Vec3:
    return (p[0], p[1], p[2] * side)


def _eye(man: Man) -> tuple[Vec3, float]:
    """The left eye's center and radius, from MakeHuman's eyeball helper."""
    helper = man.helpers["l-eye"]
    center = helper.mean(axis=0)
    return (float(center[0]), float(center[1]), float(center[2])), float(np.linalg.norm(helper - center, axis=1).mean())


def eyes(man: Man) -> tuple[bpy.types.Object, bpy.types.Object, bpy.types.Object]:
    """EyeL and EyeR: separate objects (eyeball plus iris) with their origin at the eye's center so they can look around.
    Lids: the closed eyelids, shown while unconscious."""
    center, radius = _eye(man)
    out = []
    for side, suffix in ((1.0, "L"), (-1.0, "R")):
        c = _m(center, side)
        ball = scene.blobs(f"Eye{suffix}", [ellipsoid(c, (radius,) * 3, 3.0)], resolution=0.0008)
        scene.finish(ball, scene.material("eye", (0.9, 0.88, 0.84), roughness=0.08))
        iris = scene.blobs(f"Iris{suffix}", [ellipsoid((c[0], c[1] + radius * 0.92, c[2]), (0.0062, 0.0022, 0.0062), 3.0)], resolution=0.0004)
        scene.finish(iris, scene.material("iris", (0.2, 0.24, 0.16), roughness=0.08))
        eye = scene.join(f"Eye{suffix}", ball, iris)
        scene.set_origin(eye, c)
        out.append(eye)
    # The closed lids lie just in front of the eyeball, a little inside the lid margins, and tuck under the skin round
    # the eye's corners.
    lids = [ellipsoid(c, (radius * 1.02, radius * 1.1, radius * 1.15), 3.0) for c in (_m(center, 1.0), _m(center, -1.0))]
    lid = scene.cut_below(scene.blobs("Lids", lids, resolution=0.0008), 1, center[1] + 0.002)
    scene.finish(lid, scene.material("skin", (0.8, 0.6, 0.5), roughness=0.55, subsurface=0.2))
    return out[0], out[1], lid


def eyebrows(man: Man, head: bpy.types.Object) -> bpy.types.Object:
    """Short, thick brows lying on the brow ridge: fuller at the inner end, thinning toward the temple."""
    (x, y, _z), _radius = _eye(man)
    parts = []
    for side in (1.0, -1.0):
        arc = [_m((x + dx, y + dy, z), side) for dx, dy, z in ((0.017, 0.03, 0.01), (0.021, 0.03, 0.022), (0.022, 0.024, 0.035), (0.018, 0.014, 0.047))]
        path = scene.on_surface(head, arc, -0.0003)
        parts.append(scene.curve_tube(f"Brow{side}", path, 0.0032, list(np.linspace(1.0, 0.5, len(path))), resolution=8))
    brows = scene.join("Brows", *parts)
    return scene.finish(scene.displace(brows, 0.0004, 0.0012, detail=3, seed=51), scene.material("hair", HAIR, roughness=0.8))


# The red of the lips, by distance from the middle of the mouth (|z|): where the upper lip's Cupid's bow and the lower
# lip's lower edge cross the face (x), out to the mouth's corners.
LIP_Z = (0.0, 0.006, 0.012, 0.018, 0.022, 0.025)
UPPER_LIP_X = (0.6345, 0.6352, 0.634, 0.632, 0.63, 0.627)
LOWER_LIP_X = (0.6158, 0.6165, 0.6178, 0.6198, 0.622, 0.626)
# The front of the face: the lips stand out of it, the inside of the mouth lies behind.
LIP_FRONT_Y = 0.13


def _vermilion(p: NDArray[np.float64]) -> NDArray[np.float64]:
    """How far inside the red of the lips each point lies, in metres. Negative outside."""
    z = np.abs(p[:, 2])
    upper = np.interp(z, LIP_Z, UPPER_LIP_X)
    lower = np.interp(z, LIP_Z, LOWER_LIP_X)
    return np.asarray(np.minimum.reduce([upper - p[:, 0], p[:, 0] - lower, LIP_Z[-1] - z, p[:, 1] - LIP_FRONT_Y]), dtype=np.float64)


def lips(head: bpy.types.Object) -> bpy.types.Object:
    """The red of the lips as a thin skin over the face, cut cleanly along the lip line. A separate part, so the game
    can tint it from the patient's skin tone and its border stays crisp. It keeps the face's skin weights."""
    red = bpy.data.objects.new("Lips", head.data.copy())
    scene.link(red)
    scene.cut_below(red, 0, 0.6)
    scene.cut_below(red, 0, 0.645, above=True)
    scene.keep(red, _vermilion)
    scene.inflate(red, lambda p: np.full(len(p), 0.00025))
    # The color is only for review renders: the game tints it from the skin tone.
    return scene.finish(red, scene.material("lips", (0.7, 0.45, 0.42), roughness=0.4, subsurface=0.2))


# Where to look for the inside of the mouth: (center, half size), in front of the nostrils.
MOUTH_BOX: tuple[Vec3, Vec3] = ((0.628, 0.11, 0.0), (0.045, 0.037, 0.035))


def line_mouth(man: Man, head: bpy.types.Object) -> bpy.types.Object:
    """Gives the inside of the mouth the dark mouth material: the faces round the tongue that look into more of the
    head within a few centimetres rather than out into the air, and every such face joined to them."""
    tree = BVHTree.FromObject(head, bpy.context.evaluated_depsgraph_get())
    center, size = np.array(MOUTH_BOX[0]), np.array(MOUTH_BOX[1])
    polygons = head.data.polygons
    facing_in = np.zeros(len(polygons), dtype=np.bool_)
    for face in polygons:
        c = face.center
        if np.all(np.abs(np.array((c.x, c.z, -c.y)) - center) < size):
            facing_in[face.index] = tree.ray_cast(c + face.normal * 0.0002, face.normal, 0.04)[0] is not None
    # Flood out from the face nearest the tongue, so a crease outside the mouth that faces in doesn't count.
    tongue = scene.to_blender(man.helpers["tongue"].mean(axis=0).tolist())
    by_edge: dict[tuple[int, int], list[int]] = {}
    for face in polygons:
        for key in face.edge_keys:
            by_edge.setdefault(key, []).append(face.index)
    seed = min(np.nonzero(facing_in)[0], key=lambda i: (polygons[i].center - tongue).length)
    inside = np.zeros(len(polygons), dtype=np.bool_)
    inside[seed] = True
    todo = [int(seed)]
    while todo:
        for key in polygons[todo.pop()].edge_keys:
            for other in by_edge[key]:
                if facing_in[other] and not inside[other]:
                    inside[other] = True
                    todo.append(other)
    return scene.assign(head, scene.material("mouth", (0.35, 0.08, 0.09), roughness=0.3, subsurface=0.1), lambda _centers: inside)


def _arch(name: str, helper: NDArray[np.float64], top: float, height: float, front: float) -> bpy.types.Object:
    """A row of teeth round the dental arch MakeHuman's helper marks: a parabola from the front teeth back to the
    molars, its edge at x = top, with a shallow groove between each of the 16 teeth."""
    radius = height * 0.5
    reach = float(np.abs(helper[:, 2]).max()) - radius
    depth = front - float(helper[:, 1].min()) - radius
    bend = depth / reach**2

    def at(z: float, lift: float = 0.0) -> Vec3:
        return (top, front - bend * z * z + lift, z)

    row = scene.curve_tube(name, [at(float(z)) for z in np.linspace(-reach, reach, 25)], radius, [0.6, *([1.0] * 23), 0.6], resolution=8)
    grooves = [dab(at(float(z), radius * 0.66), (0.006, 0.003, 0.0008), -0.0006) for z in np.linspace(-reach * 0.93, reach * 0.93, 15)]
    return scene.finish(scene.sculpt(row, grooves), scene.material("teeth", (0.86, 0.83, 0.72), roughness=0.3))


def teeth_and_tongue(man: Man) -> tuple[bpy.types.Object, bpy.types.Object, bpy.types.Object]:
    """Upper teeth (on the head), lower teeth and tongue (on the jaw), in MakeHuman's mouth.
    The upper incisors' edges sit just above where the lips meet, the lower ones close up behind them."""
    upper, lower = man.helpers["upper-teeth"], man.helpers["lower-teeth"]
    upper_front = float(upper[:, 1].max()) - 0.0045
    upper_x = float(upper[:, 0].min()) + 0.0045
    upper_teeth = _arch("UpperTeeth", upper, upper_x, 0.009, upper_front)
    lower_teeth = _arch("LowerTeeth", lower, upper_x - 0.0075, 0.008, upper_front - 0.003)
    tongue_helper = man.helpers["tongue"]
    c = tongue_helper.mean(axis=0)
    tongue = scene.blobs("Tongue", [ellipsoid((float(c[0]) - 0.002, float(c[1]) - 0.004, 0.0), (0.008, 0.026, 0.018), 2.5)], resolution=0.001)
    scene.finish(tongue, scene.material("tongue", (0.62, 0.26, 0.28), roughness=0.35, subsurface=0.2))
    return upper_teeth, lower_teeth, tongue
