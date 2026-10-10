"""Static props with organic parts. Origin on the floor (table top) under the prop's center, long side along X.

Material names match the game's (src/Visuals/ModelSlot.cs) where one exists: glove.
"""

from __future__ import annotations

import math

import bpy

# isort: split
# bmesh only exists once bpy has been imported.
import bmesh
import numpy as np
from numpy.typing import NDArray

from . import scene
from .hand import GLOVE
from .scene import Blob, Vec3, capsule, ellipsoid

# A box of 100 exam gloves.
BOX = (0.24, 0.09, 0.125)
# Width of the blue band along every edge.
TRIM = 0.007
# Half axes of the oval dispenser opening on top, and how deep it is cut.
OPENING = (0.06, 0.024)
OPENING_DEPTH = 0.012
# Radius of an empty glove finger lying flat.
FINGER = 0.006
# Triangles left for the gloves after the box.
GLOVE_BUDGET = 1700


def _box() -> None:
    """White cardboard box with a blue band along every edge and an oval opening on top."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    width, height, depth = BOX
    bmesh.ops.scale(bm, vec=(width, depth, height), verts=bm.verts)
    bmesh.ops.translate(bm, vec=(0.0, 0.0, height / 2), verts=bm.verts)
    # Each side keeps its middle white; the ring the inset leaves around it is the band.
    sides = list(bm.faces)
    bands = bmesh.ops.inset_individual(bm, faces=sides, thickness=TRIM, depth=0.0)["faces"]
    for face in bands:
        face.material_index = 1
    mesh = bpy.data.meshes.new("Box")
    bm.to_mesh(mesh)
    bm.free()
    box = scene.link(bpy.data.objects.new("Box", mesh))
    box.data.materials.append(scene.material("cardboard", (0.94, 0.94, 0.92), roughness=0.8))
    box.data.materials.append(scene.material("trim", (0.12, 0.55, 0.85), roughness=0.6))
    # Blender space is Z up; the box is symmetric, so its X and Y line up with the game's X and Z.
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=24, radius1=1.0, radius2=1.0, depth=1.0)
    bmesh.ops.scale(bm, vec=(OPENING[0], OPENING[1], 2 * OPENING_DEPTH), verts=bm.verts)
    bmesh.ops.translate(bm, vec=(0.0, 0.0, height), verts=bm.verts)
    mesh = bpy.data.meshes.new("Opening")
    bm.to_mesh(mesh)
    bm.free()
    cutter = scene.link(bpy.data.objects.new("Opening", mesh))
    box = scene.subtract(box, cutter)
    for poly in box.data.polygons:
        poly.use_smooth = False


def _vec(p: NDArray[np.float64]) -> Vec3:
    return float(p[0]), float(p[1]), float(p[2])


def _finger(root: Vec3, angle: float, length: float, lift: float) -> list[Blob]:
    """An empty glove finger flopping out of the bunch: it arches up `lift` and droops back down to lie on the lid.
    angle is the direction it falls in, in degrees around Y from +X."""
    a = math.radians(angle)
    out = np.array([math.cos(a), 0.0, -math.sin(a)])
    base = np.array(root)
    arch = base + out * length * 0.45 + np.array([0.0, lift, 0.0])
    tip = base + out * length
    tip[1] = BOX[1] + FINGER
    return [capsule(_vec(base), _vec(arch), FINGER * 1.1, 2.5), capsule(_vec(arch), _vec(tip), FINGER, 2.5)]


def _gloves() -> None:
    """Two crumpled gloves pulled halfway out of the opening, their limp fingers draped over the lid."""
    top = BOX[1]
    elements = [
        # Bunched rubber filling the opening, sunk below the rim so no gap shows.
        ellipsoid((0.0, top - 0.004, 0.0), (OPENING[0] * 1.05, 0.012, OPENING[1] * 1.1)),
        # The two hands, flat and wide like empty gloves.
        ellipsoid((-0.022, top + 0.016, 0.004), (0.036, 0.014, 0.024)),
        ellipsoid((0.026, top + 0.022, -0.004), (0.032, 0.02, 0.022)),
        ellipsoid((0.006, top + 0.03, 0.0), (0.018, 0.014, 0.016)),
        # A cuff pulled up out of the bunch, as the next glove waits to be taken.
        capsule((0.004, top + 0.02, 0.002), (-0.004, top + 0.055, -0.004), 0.011, 2.5),
        capsule((-0.004, top + 0.055, -0.004), (-0.016, top + 0.06, -0.006), 0.008, 2.5),
    ]
    # The left glove spills toward the front, the right one toward the back.
    for i, (angle, length, lift) in enumerate(((150, 0.07, 0.016), (165, 0.08, 0.02), (180, 0.075, 0.018), (195, 0.06, 0.012), (120, 0.05, 0.01))):
        elements += _finger((-0.03 + i * 0.003, top + 0.018, 0.004 - i * 0.003), angle, length, lift)
    for i, (angle, length, lift) in enumerate(((-20, 0.065, 0.02), (0, 0.075, 0.024), (15, 0.07, 0.02), (30, 0.055, 0.014), (60, 0.045, 0.012))):
        elements += _finger((0.034 - i * 0.002, top + 0.024, -0.004 + i * 0.003), angle, length, lift)
    gloves = scene.blobs("Gloves", elements, resolution=0.0015)
    # Thin rubber folds: creases and ridges pressed across the bunch in random directions.
    rng = np.random.default_rng(6)
    folds = []
    for i in range(14):
        c = np.array([rng.uniform(-0.05, 0.05), top + rng.uniform(0.01, 0.04), rng.uniform(-0.02, 0.02)])
        d = rng.normal(size=3) * np.array([1.0, 0.4, 1.0])
        d *= 0.02 / np.linalg.norm(d)
        folds.append(scene.line(_vec(c - d), _vec(c + d), 0.0025, -0.003 if i % 2 else 0.002))
    gloves = scene.sculpt(gloves, folds)
    gloves = scene.displace(gloves, 0.002, 0.01, seed=4)
    gloves = scene.cut_below(gloves, 1, top - OPENING_DEPTH + 0.002)
    scene.fit(gloves, GLOVE_BUDGET)
    scene.finish(gloves, scene.material("glove", GLOVE, roughness=0.55))


def glove_box() -> None:
    _box()
    _gloves()
