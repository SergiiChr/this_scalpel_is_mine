"""Surgeon's gloved right hand, rigged.

Rest pose: wrist at the origin, fingers along +X, palm facing down (-Y), thumb on the -Z side, fingers slightly relaxed.
Bones: Hand, Index1-3, Middle1-3, Ring1-3, Pinky1-3, Thumb1-3 (local X bends toward the palm).
Each finger is its own metaball shell so fingers don't melt together; a voxel remesh fuses them into the palm.
"""

from __future__ import annotations

import bpy
import numpy as np

from . import scene
from .scene import Vec3, capsule, ellipsoid

GLOVE = (0.56, 0.7, 0.82)
OPENING = -0.1

# name, knuckle (MCP) position, phalanx lengths, proximal radius, spread (radians around Y, + toward the pinky side)
FINGERS: tuple[tuple[str, Vec3, tuple[float, float, float], float, float], ...] = (
    ("Index", (0.093, 0.002, -0.027), (0.04, 0.024, 0.02), 0.0093, -0.09),
    ("Middle", (0.097, 0.003, -0.007), (0.044, 0.027, 0.021), 0.0096, -0.02),
    ("Ring", (0.093, 0.002, 0.013), (0.041, 0.025, 0.02), 0.009, 0.06),
    ("Pinky", (0.084, 0.0, 0.03), (0.031, 0.019, 0.017), 0.0078, 0.15),
)
# Carpometacarpal joint to the tip; the thumb sits lower than the fingers and angles away from the palm.
THUMB: tuple[Vec3, ...] = ((0.018, -0.01, -0.02), (0.046, -0.016, -0.045), (0.068, -0.019, -0.058), (0.086, -0.02, -0.064))
THUMB_RADII = (0.0125, 0.0108, 0.0096)


def joints(knuckle: Vec3, lengths: tuple[float, float, float], spread: float) -> list[Vec3]:
    """Joint positions from the knuckle to the tip. Each joint droops a little more, like a relaxed hand."""
    direction = np.array([np.cos(spread), 0.0, np.sin(spread)])
    points = [np.array(knuckle)]
    droop = 0.0
    for length in lengths:
        droop += 0.12
        step = direction * np.cos(droop) + np.array([0.0, -np.sin(droop), 0.0])
        points.append(points[-1] + step * length)
    return [(float(p[0]), float(p[1]), float(p[2])) for p in points]


def _finger(name: str, chain: list[Vec3], radius: float) -> bpy.types.Object:
    radii = (radius, radius * 0.9, radius * 0.82)
    base = np.array(chain[0]) - (np.array(chain[1]) - np.array(chain[0])) * 0.35
    elements = [capsule((float(base[0]), float(base[1]), float(base[2])), chain[0], radius * 1.08, 3.0)]
    for i in range(3):
        elements.append(capsule(chain[i], chain[i + 1], radii[i], 3.0))
    tip = np.array(chain[3])
    pad = tip - (tip - np.array(chain[2])) * 0.35 + np.array([0.0, -radii[2] * 0.25, 0.0])
    elements.append(ellipsoid((float(pad[0]), float(pad[1]), float(pad[2])), (radii[2] * 1.05, radii[2] * 0.85, radii[2] * 1.02), 3.0))
    return scene.blobs(name, elements, resolution=0.0012)


def build() -> bpy.types.Object:
    palm = scene.blobs(
        "Palm",
        [
            ellipsoid((0.05, 0.0, -0.001), (0.05, 0.014, 0.04)),
            ellipsoid((0.084, -0.004, 0.0), (0.017, 0.013, 0.043)),
            ellipsoid((0.028, -0.011, -0.022), (0.034, 0.016, 0.019)),
            ellipsoid((0.042, -0.009, 0.026), (0.038, 0.012, 0.014)),
            ellipsoid((-0.02, 0.0, 0.0), (0.06, 0.019, 0.027)),
            # The cuff hangs loose past the wrist.
            ellipsoid((-0.09, 0.0, 0.0), (0.05, 0.022, 0.03)),
            # Hollow of the palm.
            ellipsoid((0.058, -0.022, 0.002), (0.028, 0.007, 0.018), -1.5),
        ],
        resolution=0.0015,
    )
    parts = [palm]
    for name, knuckle, lengths, radius, spread in FINGERS:
        parts.append(_finger(name, joints(knuckle, lengths, spread), radius))
    thumb = [capsule(THUMB[i], THUMB[i + 1], THUMB_RADII[i], 3.0) for i in range(3)]
    thumb += [ellipsoid(THUMB[i], (THUMB_RADII[i],) * 3, 3.0) for i in range(1, 3)]
    parts.append(scene.blobs("Thumb", thumb, resolution=0.0012))
    hand = scene.remesh(scene.join("Glove", *parts), voxel=0.0008, smooth=10)
    hand = scene.decimate(scene.cut_below(hand, 0, OPENING), 0.06)
    scene.finish(hand, scene.material("glove", GLOVE, roughness=0.38, subsurface=0.05))
    rim = scene.rim("GloveRim", hand, 0.0022)
    scene.finish(rim, scene.material("glove", GLOVE))

    bones: list[tuple[str, Vec3, Vec3, str | None]] = [("Hand", (0.0, 0.0, 0.0), (0.09, 0.0, 0.0), None)]
    for name, knuckle, lengths, _radius, spread in FINGERS:
        chain = joints(knuckle, lengths, spread)
        for i in range(3):
            bones.append((f"{name}{i + 1}", chain[i], chain[i + 1], "Hand" if i == 0 else f"{name}{i}"))
    for i in range(3):
        bones.append((f"Thumb{i + 1}", THUMB[i], THUMB[i + 1], "Hand" if i == 0 else f"Thumb{i}"))
    rig = scene.armature("GloveRig", bones)
    scene.bind(hand, rig)
    scene.attach(rim, rig, "Hand")
    return rig


def curl(rig: bpy.types.Object, amount: float) -> None:
    """Review pose: fingers close toward the palm (amount 0..1)."""
    rotations: dict[str, Vec3] = {}
    for name, *_ in FINGERS:
        for i, weight in enumerate((0.9, 1.2, 0.8)):
            rotations[f"{name}{i + 1}"] = (-80.0 * weight * amount, 0.0, 0.0)
    for i, weight in enumerate((0.5, 0.7, 0.8)):
        rotations[f"Thumb{i + 1}"] = (-50.0 * weight * amount, 0.0, -20.0 * weight * amount)
    scene.pose(rig, rotations)
