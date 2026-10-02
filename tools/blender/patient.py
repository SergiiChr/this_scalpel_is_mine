"""The patient: one continuous skinned body lying face up along +X (head at +X), +Z is the patient's left.

Joint positions match the old part pivots (tools/assetgen/patient.py), so surgical sites and the animator line up.
Bones: Torso, Chest, Neck, Head, Jaw, UpperArm/Forearm/Hand L/R, Thigh/Shin/Foot L/R.
Rigid parts on the Head bone: Eyes, Irises, Lids (closed eyelids, shown while unconscious), Hair. Gown is skinned.
"""

from __future__ import annotations

import bpy

from . import face, scene
from .hand import smooth_digit
from .scene import Blob, Vec3, capsule, ellipsoid

SKIN = (0.8, 0.6, 0.5)
GOWN = (0.5, 0.58, 0.55)
HAIR = (0.12, 0.09, 0.07)


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
        ellipsoid((0.28, -0.008, 0.0), (0.1, 0.085, 0.2)),
        # Shoulder girdle: one solid mass from shoulder to shoulder that the arms grow out of.
        ellipsoid((0.345, -0.012, 0.0), (0.07, 0.078, 0.228)),
        # Trapezius into the neck.
        ellipsoid((0.4, -0.025, 0.0), (0.06, 0.055, 0.11)),
        capsule((0.4, -0.01, 0.0), (0.57, 0.012, 0.0), 0.049, 3.0),
        # Clavicles.
        capsule((0.36, 0.045, 0.03), (0.35, 0.03, 0.17), 0.01, 2.0),
        capsule((0.36, 0.045, -0.03), (0.35, 0.03, -0.17), 0.01, 2.0),
    ]
    for side in (1.0, -1.0):
        elements += [
            # Neck muscles from behind the ear down to the top of the breastbone, so the neck isn't a plain tube.
            capsule(_mirror((0.575, 0.0, 0.042), side), (0.43, 0.035, 0.016 * side), 0.013, 2.5),
            # Pectoral fold: the chest's lower edge rolls into the armpit instead of melting into the arm.
            capsule(_mirror((0.2, 0.05, 0.1), side), _mirror((0.31, 0.02, 0.19), side), 0.022, 2.5),
            ellipsoid(_mirror((0.22, 0.058, 0.075), side), (0.075, 0.035, 0.07)),
            # Deltoid caps and the lats that run from the armpit down the back.
            ellipsoid(_mirror((0.345, -0.006, 0.205), side), (0.058, 0.052, 0.042), 2.0),
            ellipsoid(_mirror((0.2, -0.045, 0.15), side), (0.15, 0.055, 0.05), 2.0),
            ellipsoid(_mirror((-0.33, -0.05, 0.07), side), (0.09, 0.065, 0.08)),
            ellipsoid(_mirror((-0.24, 0.02, 0.13), side), (0.06, 0.05, 0.04)),
        ]
    return elements


def _arm(side: float) -> tuple[list[Blob], list[Blob], list[tuple[list[Vec3], list[float]]]]:
    """(arm, palm, fingers) elements. Arms rest at the sides, palms down, thumbs toward the body.
    Each finger is its own shell so they don't melt together."""
    arm = [
        capsule(_mirror((0.34, -0.006, 0.222), side), _mirror((0.13, -0.025, 0.25), side), 0.042, 3.0),
        ellipsoid(_mirror((0.25, 0.012, 0.245), side), (0.075, 0.035, 0.036), 2.5),
        capsule(_mirror((0.13, -0.025, 0.25), side), _mirror((-0.02, -0.03, 0.254), side), 0.038, 3.0),
        capsule(_mirror((-0.02, -0.03, 0.254), side), _mirror((-0.14, -0.04, 0.258), side), 0.026, 3.0),
        ellipsoid(_mirror((0.06, -0.022, 0.252), side), (0.07, 0.034, 0.042), 2.5),
        # Point of the elbow, and the wrist bones standing out on either side of a slimmer wrist.
        ellipsoid(_mirror((0.13, -0.05, 0.25), side), (0.018, 0.016, 0.018), 3.0),
        ellipsoid(_mirror((-0.13, -0.036, 0.237), side), (0.009, 0.008, 0.008), 3.0),
        ellipsoid(_mirror((-0.13, -0.036, 0.279), side), (0.009, 0.008, 0.008), 3.0),
    ]
    hand = [
        ellipsoid(_mirror((-0.14, -0.042, 0.258), side), (0.022, 0.018, 0.03), 2.5),
        ellipsoid(_mirror((-0.19, -0.048, 0.26), side), (0.048, 0.014, 0.035)),
    ]
    fingers = []
    for i, (dz, length, spread) in enumerate(((-0.027, 0.075, -0.004), (-0.009, 0.082, -0.001), (0.009, 0.077, 0.002), (0.026, 0.062, 0.006))):
        base = (-0.225, -0.046, 0.26 + dz)
        mid = (base[0] - length * 0.55, base[1] - 0.006, base[2] + spread)
        tip = (base[0] - length, base[1] - 0.016, base[2] + spread * 2.0)
        r = 0.0084 - i * 0.0004 if i < 3 else 0.0072
        root = (base[0] + 0.015, base[1], base[2])
        fingers.append(([_mirror(p, side) for p in (root, base, mid, tip)], [r * 1.05, r, r * 0.9, r * 0.78]))
    thumb = [_mirror(p, side) for p in ((-0.16, -0.046, 0.228), (-0.19, -0.05, 0.212), (-0.218, -0.053, 0.203))]
    fingers.append((thumb, [0.011, 0.0098, 0.0085]))
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
    # Clean plane cuts make straight hems: at the waist, across the thighs, and inside the arms lying beside the hips.
    scene.bisect(gown, (-0.58, 0.0, 0.0), (1.0, 0.0, 0.0))
    scene.bisect(gown, (-0.12, 0.0, 0.0), (-1.0, 0.0, 0.0))
    scene.bisect(gown, (0.0, 0.0, 0.185), (0.0, 0.0, -1.0))
    scene.bisect(gown, (0.0, 0.0, -0.185), (0.0, 0.0, 1.0))
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


# Triangles per part, about 50k in all. The face and hands get most of the body's share because the decimation
# keeps curvature, and the player looks at them from up close.
BUDGETS = {
    "Body": 34000,
    "Gown": 5000,
    "Hair": 4000,
    "Brows": 800,
    "EyeL": 1200,
    "EyeR": 1200,
    "Lids": 1200,
    "UpperTeeth": 800,
    "LowerTeeth": 800,
    "Tongue": 500,
}


def build() -> bpy.types.Object:
    shells = [scene.blobs("Torso", _torso(), resolution=0.006), scene.blobs("Head", face.base(), resolution=0.0015)]
    shells += face.ear_shells() + face.eyelid_shells() + face.mouth_shells()
    for side in (1.0, -1.0):
        arm, hand, fingers = _arm(side)
        shells += [
            scene.blobs(f"Arm{side}", arm, resolution=0.004),
            scene.blobs(f"Hand{side}", hand, resolution=0.002),
            scene.blobs(f"Leg{side}", _leg(side), resolution=0.005),
        ]
        shells += [smooth_digit(f"Finger{side}{i}", points, radii) for i, (points, radii) in enumerate(fingers)]
    # Fine enough for the face; the sculpt strokes and the decimation keep detail where it matters.
    body = scene.remesh(scene.join("Body", *shells), voxel=0.0012, smooth=12)
    body = scene.sculpt(body, face.strokes())
    scene.finish(body, scene.material("skin", SKIN, roughness=0.55, subsurface=0.2))
    # Cut the canals and the mouth while the mesh is still dense, so their edges stay clean after decimating.
    for canal in face.ear_canals():
        body = scene.subtract(body, canal, fast=True)
    body = face.carve_mouth(body)
    body = scene.fit(body, BUDGETS["Body"])
    gown = _gown(body)
    hair = _hair(body)
    brows = face.eyebrows(body)
    eye_l, eye_r, lids = face.eyes()
    upper_teeth, lower_teeth, tongue = face.teeth_and_tongue()
    for part in (gown, hair, brows, eye_l, eye_r, lids, upper_teeth, lower_teeth, tongue):
        scene.fit(part, BUDGETS[part.name])
    scene.clean()

    rig = scene.armature("PatientRig", BONES)
    scene.bind(body, rig)
    scene.bind(gown, rig)
    scene.reweight(body, "Jaw", face.jaw_weight, "Head")
    for part in (hair, brows, eye_l, eye_r, lids, upper_teeth):
        scene.attach(part, rig, "Head")
    for part in (lower_teeth, tongue):
        scene.attach(part, rig, "Jaw")
    show_lids(False)
    return rig


def show_lids(visible: bool) -> None:
    bpy.data.objects["Lids"].hide_render = not visible
    for name in ("EyeL", "EyeR"):
        bpy.data.objects[name].hide_render = visible


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
