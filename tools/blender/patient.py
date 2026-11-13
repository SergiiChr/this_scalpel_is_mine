"""The patient: MakeHuman's average young man (tools/blender/makehuman.py), lying face up along +X (head at +X), +Z his
left, skinned to our rig with MakeHuman's own weights.

Bones: Torso, Chest, Neck, Head, Jaw, UpperArm/Forearm/Hand L/R, Thigh/Shin/Foot L/R, each from one of MakeHuman's
joints to the next.
Rigid parts on the Head bone: Eyes, Irises, Lids (closed eyelids, shown while unconscious), Hair, Brows, UpperTeeth;
on the Jaw: LowerTeeth, Tongue. Body, Gown and Lips are skinned.
The skeleton (tools/blender/skeleton.py) is one rigid part per bone, named Skeleton<Bone>.
"""

from __future__ import annotations

import bpy
import numpy as np
from numpy.typing import NDArray

from . import face, makehuman, scene, skeleton
from .makehuman import Man
from .scene import Vec3

SKIN = (0.8, 0.6, 0.5)
GOWN = (0.5, 0.58, 0.55)


def _bones(man: Man) -> list[tuple[str, Vec3, Vec3, str | None]]:
    """(bone, head, tail, parent) from MakeHuman's joints. The jaw hinges where MakeHuman's own jaw bone does."""

    def j(name: str) -> Vec3:
        p = man.joints[name]
        return (float(p[0]), float(p[1]), float(p[2]))

    bones: list[tuple[str, Vec3, Vec3, str | None]] = [
        ("Torso", j("pelvis"), j("spine-2"), None),
        ("Chest", j("spine-2"), j("neck"), "Torso"),
        ("Neck", j("neck"), j("head"), "Chest"),
        ("Head", j("head"), j("head-2"), "Neck"),
        ("Jaw", j("mouth"), j("jaw"), "Head"),
    ]
    for s, m in (("L", "l"), ("R", "r")):
        bones += [
            (f"UpperArm{s}", j(f"{m}-shoulder"), j(f"{m}-elbow"), "Chest"),
            (f"Forearm{s}", j(f"{m}-elbow"), j(f"{m}-hand"), f"UpperArm{s}"),
            (f"Hand{s}", j(f"{m}-hand"), j(f"{m}-finger-3-4"), f"Forearm{s}"),
            (f"Thigh{s}", j(f"{m}-upper-leg"), j(f"{m}-knee"), "Torso"),
            (f"Shin{s}", j(f"{m}-knee"), j(f"{m}-ankle"), f"Thigh{s}"),
            (f"Foot{s}", j(f"{m}-ankle"), j(f"{m}-foot-2"), f"Shin{s}"),
        ]
    return bones


# The navel and the groove above it down the middle of the belly: (center, half length along x, half width), and the
# height of the belly round it at its ends along x.
NAVEL: tuple[tuple[float, float], float, float] = ((0.135, 0.0), 0.045, 0.02)
BELLY_Y = (0.1255, 0.1235)


def _fill_navel(verts: NDArray[np.float64]) -> NDArray[np.float64]:
    """Lifts the navel and the groove above it level with the belly round them. The belly's surgical site is centered
    there and its simulated skin starts from the body's: a dent under it would sink the cuts and stitches across it."""
    (x, z), length, width = NAVEL
    p = verts.copy()
    d = np.hypot((p[:, 0] - x) / length, (p[:, 2] - z) / width)
    level = np.interp(p[:, 0], (x - length, x + length), BELLY_Y)
    # Full height inside, easing into the belly over the outer third.
    lift = np.clip((1.0 - d) / 0.3, 0.0, 1.0) * np.maximum(level - p[:, 1], 0.0)
    p[:, 1] += np.where((d < 1.0) & (p[:, 1] > 0.1), lift, 0.0)
    return p


def _body(man: Man) -> bpy.types.Object:
    body = scene.mesh_object("Body", _fill_navel(man.verts).tolist(), man.quads.tolist())
    scene.weigh(body, man.weights)
    return scene.finish(body, scene.material("skin", SKIN, roughness=0.55, subsurface=0.2))


def _gown(body: bpy.types.Object, man: Man) -> bpy.types.Object:
    """Hospital gown shorts: the hips and upper thighs of the body, puffed out a few millimetres. Skinned like the body."""
    gown = bpy.data.objects.new("Gown", body.data.copy())
    scene.link(gown)
    hip = man.joints["l-upper-leg"]
    # Clean plane cuts make straight hems at the waist and across the thighs. The hands lying beside the hips aren't
    # part of it.
    scene.bisect(gown, (float(hip[0]) - GOWN_HEM, 0.0, 0.0), (1.0, 0.0, 0.0))
    scene.bisect(gown, (float(hip[0]) + GOWN_WAIST, 0.0, 0.0), (-1.0, 0.0, 0.0))
    scene.drop(gown, [f"{bone}{s}" for bone in ("UpperArm", "Forearm", "Hand") for s in "LR"])
    mod = gown.modifiers.new("Puff", "DISPLACE")
    mod.strength = 0.006
    mod.mid_level = 0.0
    return scene.finish(scene.bake(gown), scene.material("gown", GOWN, roughness=0.9))


# The gown's waistband and hems from the hip joints, in metres along the body. The waistband sits low, under the
# belly's surgical site.
GOWN_WAIST, GOWN_HEM = 0.025, 0.15

# The hairline as the lowest head-axis x the hair reaches, by angle around the head from the face (0) to the back
# of the head (180 degrees). The front edge sits 6.5 cm above the brow (the male average), recedes a little at the
# temples, drops at the temporal points into sideburns that end level with the tragus, and the nape ends level with
# the earlobes.
HAIRLINE_ANGLE = (0.0, 22.0, 36.0, 48.0, 58.0, 67.0, 71.0, 74.0, 80.0, 90.0, 130.0, 180.0)
HAIRLINE_X = (0.768, 0.769, 0.776, 0.763, 0.747, 0.735, 0.719, 0.689, 0.685, 0.663, 0.655, 0.649)
# The head's long axis, halfway between the forehead and the back of the head.
HEAD_AXIS_Y = 0.043
# Hair stands about 9 mm on top, a crew cut, and 4 mm on the clipped sides and back, thinning to nothing over the
# last centimetre before the hairline so its edge lies flat on the skin. The top starts 6 cm below the crown.
HAIR_TOP, HAIR_SIDES, HAIR_FADE, HAIR_TOP_X = 0.009, 0.004, 0.01, 0.743
# The hair leaves a gap around each ear (ear center, half sizes along x and y).
EAR_GAP: tuple[tuple[float, float], tuple[float, float]] = ((0.693, 0.03), (0.047, 0.026))


def _above_hairline(p: NDArray[np.float64]) -> NDArray[np.float64]:
    """How far each point lies inside the hair, in metres: negative on bare skin."""
    angle = np.degrees(np.arctan2(np.abs(p[:, 2]), p[:, 1] - HEAD_AXIS_Y))
    edge = p[:, 0] - np.interp(angle, HAIRLINE_ANGLE, HAIRLINE_X)
    (ex, ey), (sx, sy) = EAR_GAP
    ear = (np.hypot((p[:, 0] - ex) / sx, (p[:, 1] - ey) / sy) - 1.0) * sy
    # A few millimetres of waver, so the hairline isn't drawn with a ruler.
    edge += 0.0008 * np.sin(p[:, 2] * 150.0 + p[:, 1] * 60.0) + 0.0004 * np.sin(p[:, 1] * 230.0 - p[:, 2] * 310.0)
    return np.where(np.abs(p[:, 2]) > 0.045, np.minimum(edge, ear), edge)


def _hair_depth(p: NDArray[np.float64]) -> NDArray[np.float64]:
    top = np.clip((p[:, 0] - HAIR_TOP_X) / 0.05, 0.0, 1.0)
    fade = np.clip(_above_hairline(p) / HAIR_FADE, 0.0, 1.0)
    return (HAIR_SIDES + (HAIR_TOP - HAIR_SIDES) * top * top) * (0.04 + 0.96 * fade * fade * (3.0 - 2.0 * fade))


def _hair(body: bpy.types.Object) -> bpy.types.Object:
    """Short hair grown out of the scalp: one surface lifted off the skin, thick on top and fading out at the hairline."""
    hair = bpy.data.objects.new("Hair", body.data.copy())
    scene.link(hair)
    hair.vertex_groups.clear()
    scene.cut_below(hair, 0, 0.62)
    # The scalp's quads are a centimetre across: split them, so the hairline is cut smoothly.
    hair = scene.subdivide(hair, 2)
    scene.keep(hair, _above_hairline)
    hair = scene.displace(scene.inflate(hair, _hair_depth), 0.0009, 0.003, detail=3, seed=17)
    return scene.finish(hair, scene.material("hair", face.HAIR, roughness=0.8))


# Triangles per part, about 42k in all. MakeHuman's body is used as it comes: about 27k triangles.
BUDGETS = {
    "Body": 27000,
    "Gown": 5000,
    "Hair": 3000,
    "Lips": 600,
    "Brows": 800,
    "EyeL": 1200,
    "EyeR": 1200,
    "Lids": 1200,
    "UpperTeeth": 800,
    "LowerTeeth": 800,
    "Tongue": 500,
}


def build() -> bpy.types.Object:
    man = makehuman.load()
    body = _body(man)
    hair = _hair(body)
    lips = face.lips(body)
    gown = _gown(body, man)
    brows = face.eyebrows(man, body)
    # After the copies, so only the body has the mouth's material.
    face.line_mouth(man, body)
    eye_l, eye_r, lids = face.eyes(man)
    upper_teeth, lower_teeth, tongue = face.teeth_and_tongue(man)
    for part in (body, gown, hair, lips, brows, eye_l, eye_r, lids, upper_teeth, lower_teeth, tongue):
        scene.fit(part, BUDGETS[part.name])
    bones = skeleton.build(man)
    scene.clean()

    rig = scene.armature("PatientRig", _bones(man))
    for part in (body, gown, lips):
        scene.skin(part, rig)
    for part in (hair, brows, eye_l, eye_r, lids, upper_teeth):
        scene.attach(part, rig, "Head")
    for part in (lower_teeth, tongue):
        scene.attach(part, rig, "Jaw")
    for part, bone in bones:
        scene.attach(part, rig, bone)
    show_lids(False)
    return rig


def show_lids(visible: bool) -> None:
    bpy.data.objects["Lids"].hide_render = not visible
    for name in ("EyeL", "EyeR"):
        bpy.data.objects[name].hide_render = visible


def show_skeleton_only(only: bool) -> None:
    """Hides everything but the skeleton from renders, to review it as an X-ray would show it."""
    for obj in scene.meshes():
        obj.hide_render = only and not obj.name.startswith("Skeleton")
    if not only:
        show_lids(False)


def test_pose(rig: bpy.types.Object) -> None:
    """Review pose to check the skin weights: head turned, jaw open, one arm raised, one knee bent."""
    scene.pose(
        rig,
        {
            "Head": (0.0, 20.0, 0.0),
            "Jaw": (-12.0, 0.0, 0.0),
            "UpperArmL": (0.0, 0.0, 55.0),
            "ForearmL": (0.0, 0.0, 40.0),
            "ThighR": (0.0, 0.0, 35.0),
            "ShinR": (0.0, 0.0, -60.0),
        },
    )
