"""The patient: one continuous skinned body lying face up along +X (head at +X), +Z is the patient's left.

Joint positions match the old part pivots (tools/assetgen/patient.py), so surgical sites and the animator line up.
Bones: Torso, Chest, Neck, Head, Jaw, UpperArm/Forearm/Hand L/R, Thigh/Shin/Foot L/R.
Rigid parts on the Head bone: Eyes, Irises, Lids (closed eyelids, shown while unconscious), Hair. Gown is skinned.
"""

from __future__ import annotations

import bpy

from . import scene
from .scene import Blob, Vec3, capsule, ellipsoid

SKIN = (0.8, 0.6, 0.5)
GOWN = (0.5, 0.58, 0.55)
HAIR = (0.12, 0.09, 0.07)
EYE = (0.92, 0.9, 0.86)
IRIS = (0.18, 0.22, 0.14)

EYE_CENTER = (0.672, 0.078, 0.031)
EYE_RADIUS = 0.0118


def _mirror(p: Vec3, side: float) -> Vec3:
    return (p[0], p[1], p[2] * side)


def _torso() -> list[Blob]:
    elements = [
        # Pelvis and buttocks resting on the table.
        ellipsoid((-0.33, -0.005, 0.0), (0.1, 0.092, 0.165)),
        ellipsoid((-0.2, 0.005, 0.0), (0.11, 0.09, 0.15)),
        # Belly, a little soft and rounded on top.
        ellipsoid((-0.12, 0.028, 0.0), (0.13, 0.07, 0.125)),
        # Ribcage and chest.
        ellipsoid((0.1, 0.0, 0.0), (0.17, 0.1, 0.165)),
        ellipsoid((0.28, -0.008, 0.0), (0.1, 0.085, 0.19)),
        # Trapezius into the neck.
        ellipsoid((0.4, -0.025, 0.0), (0.06, 0.055, 0.11)),
        capsule((0.4, -0.01, 0.0), (0.57, 0.012, 0.0), 0.049, 3.0),
        # Clavicles.
        capsule((0.36, 0.045, 0.03), (0.35, 0.03, 0.17), 0.01, 2.0),
        capsule((0.36, 0.045, -0.03), (0.35, 0.03, -0.17), 0.01, 2.0),
    ]
    for side in (1.0, -1.0):
        elements += [
            ellipsoid(_mirror((0.22, 0.058, 0.075), side), (0.075, 0.035, 0.07)),
            ellipsoid(_mirror((-0.33, -0.05, 0.07), side), (0.09, 0.065, 0.08)),
            ellipsoid(_mirror((-0.24, 0.02, 0.13), side), (0.06, 0.05, 0.04)),
        ]
    return elements


def _head() -> list[Blob]:
    elements = [
        ellipsoid((0.705, -0.005, 0.0), (0.092, 0.1, 0.077)),
        ellipsoid((0.635, 0.042, 0.0), (0.07, 0.062, 0.06)),
        # Chin and jaw line.
        ellipsoid((0.568, 0.072, 0.0), (0.022, 0.024, 0.03), 3.0),
        ellipsoid((0.6, 0.02, 0.0), (0.035, 0.05, 0.058)),
        # Brow ridge and nose.
        ellipsoid((0.7, 0.087, 0.0), (0.014, 0.014, 0.055), 3.0),
        capsule((0.688, 0.098, 0.0), (0.646, 0.12, 0.0), 0.008, 3.0),
        ellipsoid((0.642, 0.121, 0.0), (0.011, 0.011, 0.012), 3.0),
        # Lips.
        ellipsoid((0.614, 0.104, 0.0), (0.0055, 0.009, 0.02), 4.0),
        ellipsoid((0.6, 0.101, 0.0), (0.0065, 0.009, 0.018), 4.0),
        # Mouth line and the groove under the nose.
        ellipsoid((0.607, 0.111, 0.0), (0.0018, 0.008, 0.021), -5.0),
        ellipsoid((0.626, 0.112, 0.0), (0.007, 0.004, 0.004), -3.0),
    ]
    for side in (1.0, -1.0):
        elements += [
            ellipsoid(_mirror((0.64, 0.074, 0.04), side), (0.026, 0.024, 0.02)),
            ellipsoid(_mirror((0.64, 0.108, 0.012), side), (0.008, 0.008, 0.008), 3.0),
            # Eye sockets.
            ellipsoid(_mirror((0.673, 0.095, 0.031), side), (0.013, 0.011, 0.017), -2.0),
            # Eyelids: a heavier upper lid and a thin lower one make an almond-shaped opening.
            capsule(_mirror((0.681, 0.09, 0.019), side), _mirror((0.681, 0.088, 0.045), side), 0.0055, 4.0),
            capsule(_mirror((0.664, 0.088, 0.021), side), _mirror((0.664, 0.087, 0.043), side), 0.0035, 4.0),
            # Ears.
            ellipsoid(_mirror((0.665, 0.005, 0.077), side), (0.03, 0.018, 0.009), 3.0),
            ellipsoid(_mirror((0.665, 0.008, 0.085), side), (0.014, 0.008, 0.005), -2.0),
        ]
    return elements


def _arm(side: float) -> tuple[list[Blob], list[Blob], list[list[Blob]]]:
    """(arm, palm, fingers) elements. Arms rest at the sides, palms down, thumbs toward the body.
    Each finger is its own shell so they don't melt together."""
    arm = [
        ellipsoid(_mirror((0.36, 0.0, 0.2), side), (0.06, 0.055, 0.052)),
        capsule(_mirror((0.36, -0.005, 0.22), side), _mirror((0.13, -0.025, 0.25), side), 0.042, 3.0),
        ellipsoid(_mirror((0.25, 0.012, 0.245), side), (0.075, 0.035, 0.036), 2.5),
        capsule(_mirror((0.13, -0.025, 0.25), side), _mirror((-0.02, -0.03, 0.254), side), 0.038, 3.0),
        capsule(_mirror((-0.02, -0.03, 0.254), side), _mirror((-0.14, -0.04, 0.258), side), 0.026, 3.0),
        ellipsoid(_mirror((0.06, -0.022, 0.252), side), (0.07, 0.034, 0.042), 2.5),
    ]
    hand = [
        ellipsoid(_mirror((-0.14, -0.042, 0.258), side), (0.022, 0.018, 0.03), 2.5),
        ellipsoid(_mirror((-0.19, -0.048, 0.26), side), (0.048, 0.014, 0.041)),
    ]
    fingers = []
    for i, (dz, length, spread) in enumerate(((-0.027, 0.075, -0.004), (-0.009, 0.082, -0.001), (0.009, 0.077, 0.002), (0.026, 0.062, 0.006))):
        base = (-0.225, -0.046, 0.26 + dz)
        mid = (base[0] - length * 0.55, base[1] - 0.006, base[2] + spread)
        tip = (base[0] - length, base[1] - 0.016, base[2] + spread * 2.0)
        r = 0.0084 - i * 0.0004 if i < 3 else 0.0072
        fingers.append([capsule(_mirror(base, side), _mirror(mid, side), r, 3.0), capsule(_mirror(mid, side), _mirror(tip, side), r * 0.86, 3.0)])
    thumb = [capsule(_mirror((-0.16, -0.046, 0.228), side), _mirror((-0.19, -0.05, 0.212), side), 0.011, 3.0)]
    thumb.append(capsule(_mirror((-0.19, -0.05, 0.212), side), _mirror((-0.218, -0.053, 0.203), side), 0.0095, 3.0))
    fingers.append(thumb)
    return arm, hand, fingers


def _leg(side: float) -> list[Blob]:
    return [
        capsule(_mirror((-0.33, -0.015, 0.095), side), _mirror((-0.8, -0.03, 0.105), side), 0.075, 3.0),
        ellipsoid(_mirror((-0.5, 0.0, 0.1), side), (0.16, 0.07, 0.078), 2.5),
        ellipsoid(_mirror((-0.8, 0.02, 0.105), side), (0.03, 0.022, 0.03), 3.0),
        capsule(_mirror((-0.8, -0.03, 0.105), side), _mirror((-1.19, -0.05, 0.11), side), 0.042, 3.0),
        ellipsoid(_mirror((-0.93, -0.058, 0.107), side), (0.11, 0.045, 0.047), 2.5),
        ellipsoid(_mirror((-1.19, -0.05, 0.11), side), (0.025, 0.025, 0.03), 3.0),
        # Foot pointing up: heel on the table, toes to the ceiling.
        ellipsoid(_mirror((-1.225, -0.06, 0.11), side), (0.03, 0.028, 0.028), 3.0),
        capsule(_mirror((-1.225, -0.04, 0.11), side), _mirror((-1.245, 0.1, 0.114), side), 0.028, 3.0),
        ellipsoid(_mirror((-1.24, 0.06, 0.114), side), (0.02, 0.06, 0.042), 2.5),
        ellipsoid(_mirror((-1.248, 0.125, 0.116), side), (0.017, 0.02, 0.043), 3.0),
    ]


BONES: list[tuple[str, Vec3, Vec3, str | None]] = [
    ("Torso", (-0.33, 0.0, 0.0), (0.05, 0.0, 0.0), None),
    ("Chest", (0.05, 0.0, 0.0), (0.4, 0.0, 0.0), "Torso"),
    ("Neck", (0.42, 0.0, 0.0), (0.58, 0.01, 0.0), "Chest"),
    ("Head", (0.58, 0.01, 0.0), (0.79, 0.01, 0.0), "Neck"),
    ("Jaw", (0.64, 0.035, 0.0), (0.575, 0.075, 0.0), "Head"),
]
for _side, _s in ((1.0, "L"), (-1.0, "R")):
    BONES += [
        (f"UpperArm{_s}", _mirror((0.38, 0.0, 0.215), _side), _mirror((0.13, -0.025, 0.25), _side), "Chest"),
        (f"Forearm{_s}", _mirror((0.13, -0.025, 0.25), _side), _mirror((-0.14, -0.04, 0.258), _side), f"UpperArm{_s}"),
        (f"Hand{_s}", _mirror((-0.14, -0.04, 0.258), _side), _mirror((-0.28, -0.055, 0.26), _side), f"Forearm{_s}"),
        (f"Thigh{_s}", _mirror((-0.36, -0.015, 0.1), _side), _mirror((-0.8, -0.03, 0.105), _side), "Torso"),
        (f"Shin{_s}", _mirror((-0.8, -0.03, 0.105), _side), _mirror((-1.2, -0.05, 0.11), _side), f"Thigh{_s}"),
        (f"Foot{_s}", _mirror((-1.2, -0.05, 0.11), _side), _mirror((-1.245, 0.11, 0.114), _side), f"Shin{_s}"),
    ]


def _gown(body: bpy.types.Object) -> bpy.types.Object:
    """Hospital gown shorts: the hips and upper thighs of the body, puffed out a few millimetres."""
    gown = bpy.data.objects.new("Gown", body.data.copy())
    scene.link(gown)
    scene.cut_below(gown, 0, -0.58)
    scene.cut_below(gown, 0, -0.12, above=True)
    # Arms lie beside the hips; keep only what's within the hip width.
    scene.cut_below(gown, 2, 0.185, above=True)
    scene.cut_below(gown, 2, -0.185)
    mod = gown.modifiers.new("Puff", "DISPLACE")
    mod.strength = 0.006
    mod.mid_level = 0.0
    return scene.finish(scene.bake(gown), scene.material("gown", GOWN, roughness=0.9))


def _hair(body: bpy.types.Object) -> bpy.types.Object:
    """Short hair grown out of the scalp: the crown and back of the head, stopping at the hairline and around the ears."""
    hair = bpy.data.objects.new("Hair", body.data.copy())
    scene.link(hair)
    # One tilted plane makes a clean hairline: high on the forehead, down to the nape at the back, above the ears.
    scene.bisect(hair, (0.745, 0.03, 0.0), (1.0, -0.6, 0.0))
    mod = hair.modifiers.new("Thickness", "SOLIDIFY")
    mod.thickness = 0.004
    mod.offset = 1.0
    hair = scene.displace(scene.bake(hair), 0.0012, 0.003, seed=17)
    return scene.finish(hair, scene.material("hair", HAIR, roughness=0.8))


def _eyes() -> tuple[bpy.types.Object, bpy.types.Object, bpy.types.Object]:
    balls, irises, lids = [], [], []
    for side in (1.0, -1.0):
        c = _mirror(EYE_CENTER, side)
        balls.append(ellipsoid(c, (EYE_RADIUS,) * 3, 3.0))
        irises.append(ellipsoid((c[0], c[1] + EYE_RADIUS * 0.82, c[2]), (0.0052, 0.0028, 0.0052), 3.0))
        lids.append(ellipsoid((c[0], c[1] + 0.0015, c[2]), (EYE_RADIUS * 1.18, EYE_RADIUS * 1.08, EYE_RADIUS * 1.3), 3.0))
    eyes = scene.finish(scene.blobs("Eyes", balls, resolution=0.0008), scene.material("eye", EYE, roughness=0.1))
    iris = scene.finish(scene.blobs("Irises", irises, resolution=0.0005), scene.material("iris", IRIS, roughness=0.1))
    lid = scene.blobs("Lids", lids, resolution=0.0008)
    lid = scene.cut_below(lid, 1, EYE_CENTER[1] - 0.002)
    return eyes, iris, scene.finish(lid, scene.material("skin", SKIN, roughness=0.55, subsurface=0.2))


def build() -> bpy.types.Object:
    shells = [scene.blobs("Torso", _torso(), resolution=0.006), scene.blobs("Head", _head(), resolution=0.002)]
    for side in (1.0, -1.0):
        arm, hand, fingers = _arm(side)
        shells += [
            scene.blobs(f"Arm{side}", arm, resolution=0.004),
            scene.blobs(f"Hand{side}", hand, resolution=0.002),
            scene.blobs(f"Leg{side}", _leg(side), resolution=0.005),
        ]
        shells += [scene.blobs(f"Finger{side}{i}", finger, resolution=0.0015) for i, finger in enumerate(fingers)]
    body = scene.remesh(scene.join("Body", *shells), voxel=0.0024, smooth=20)
    body = scene.decimate(body, 0.028)
    scene.finish(body, scene.material("skin", SKIN, roughness=0.55, subsurface=0.2))
    gown = _gown(body)
    hair = _hair(body)
    eyes, iris, lids = _eyes()

    rig = scene.armature("PatientRig", BONES)
    scene.bind(body, rig)
    scene.bind(gown, rig)
    for part in (hair, eyes, iris, lids):
        scene.attach(part, rig, "Head")
    show_lids(False)
    return rig


def show_lids(visible: bool) -> None:
    bpy.data.objects["Lids"].hide_render = not visible
    for name in ("Eyes", "Irises"):
        bpy.data.objects[name].hide_render = visible


def test_pose(rig: bpy.types.Object) -> None:
    """Review pose to check the skin weights: head turned, jaw open, one arm raised, one knee bent."""
    scene.pose(
        rig,
        {
            "Head": (0.0, 0.0, 25.0),
            "Jaw": (0.0, 0.0, -15.0),
            "UpperArmL": (0.0, 0.0, 55.0),
            "ForearmL": (0.0, 0.0, 40.0),
            "ThighR": (0.0, 0.0, 35.0),
            "ShinR": (0.0, 0.0, -60.0),
        },
    )
