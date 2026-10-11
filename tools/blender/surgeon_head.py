"""The surgeon's head, rigged: face and neck with a bouffant cap, pleated surgical mask and safety glasses.
Built for review into build/blender_review/surgeon_head.glb; the game still loads the tools/assetgen head.

Head frame (game space): origin between the eyes at eye level, Y up, the face looks toward -Z, +X is the surgeon's
right. The game turns the model about Surgeon.HeadPivot, the top of the neck; the neck below it ends inside the
body's collar (tools/assetgen/surgeon.py). The face is the patient's (tools/blender/face.py), built in the
patient's frame and turned upright here, with the mouth left closed under the mask.
Bones: Neck (collar to the top of the neck), Head; Face is skinned to both.
Rigid parts on Head: Cap, Mask, Lens, Frame, Brows, EyeL, EyeR.
"""

from __future__ import annotations

from collections.abc import Callable

import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from numpy.typing import NDArray

from . import face, scene
from .scene import Vec3, capsule, ellipsoid

# Material colors (display sRGB). The game recolors tint (cap), skin and mask (src/Surgeons/Surgeon.Body.cs).
SKIN = (0.8, 0.6, 0.5)
CAP = (0.42, 0.52, 0.66)
MASK = (0.55, 0.72, 0.78)
FRAME = (0.95, 0.72, 0.12)

# Where the neck meets the collar and where the head turns (Surgeon.HeadPivot), both below eye level.
NECK_BASE: Vec3 = (0.0, -0.145, 0.02)
PIVOT: Vec3 = (0.0, -0.08, 0.02)
CROWN: Vec3 = (0.0, 0.12, 0.02)
BONES: list[tuple[str, Vec3, Vec3, str | None]] = [("Neck", NECK_BASE, PIVOT, None), ("Head", PIVOT, CROWN, "Neck")]

# Triangles per part: about 6k in all, most of it in the face the players look at from up close.
BUDGETS = {
    "Face": 2600,
    "Cap": 900,
    "Mask": 1300,
    "Lens": 200,
    "Frame": 400,
    "Brows": 120,
    "EyeL": 140,
    "EyeR": 140,
}


def _upright(obj: bpy.types.Object) -> bpy.types.Object:
    """Turns a part built in the patient's face frame (x up the face, y out of it, z the patient's left) into the
    surgeon's: y up, the face toward -Z, and the origin at eye level between the eyes."""
    # In Blender coordinates: (x, y, z) -> (y, z, x - eye height).
    turn = Matrix(((0.0, 1.0, 0.0, 0.0), (0.0, 0.0, 1.0, 0.0), (1.0, 0.0, 0.0, -face.EYE_CENTER[0]), (0.0, 0.0, 0.0, 1.0)))
    if obj.location.length == 0.0:
        obj.data.transform(turn)
    else:
        # A part with its origin at its pivot (the eyes) keeps its mesh around the origin: only the pivot moves.
        obj.data.transform(turn.to_3x3().to_4x4())
        obj.location = turn @ obj.location
    return obj


def _neck_blobs() -> list[scene.Blob]:
    """The neck in the patient's frame, under the jaw and a little behind the face, with the two long neck muscles."""
    elements = [capsule((0.5, -0.02, 0.0), (0.605, -0.012, 0.0), 0.046, 3.0), ellipsoid((0.59, -0.02, 0.0), (0.03, 0.045, 0.05), 2.5)]
    for side in (1.0, -1.0):
        elements.append(capsule((0.585, 0.0, 0.04 * side), (0.5, 0.022, 0.018 * side), 0.012, 2.5))
    return elements


def _head_mesh() -> bpy.types.Object:
    """Face and neck as one fused shell, sculpted, upright, open at the collar."""
    shells = [scene.blobs("Face", face.base() + _neck_blobs(), resolution=0.0015)]
    shells += face.ear_shells() + face.eyelid_shells()
    # Named Face, not Head: a mesh node named like the Head bone loses the name to it in Godot's import.
    head = scene.remesh(scene.join("Face", *shells), voxel=0.0012, smooth=12)
    head = scene.sculpt(head, face.strokes())
    for canal in face.ear_canals():
        head = scene.subtract(head, canal, fast=True)
    return head


def _surface(head: bpy.types.Object) -> BVHTree:
    return BVHTree.FromObject(head, bpy.context.evaluated_depsgraph_get())


def _depth(tree: BVHTree, x: float, y: float) -> float:
    """Game z of the face surface at (x, y) seen from the front. Past the face's silhouette (beside the jaw) it is the
    depth of the silhouette itself, so cloth there wraps around the edge instead of falling through."""
    for step in range(20):
        hit = tree.ray_cast(Vector((x * (1.0 - 0.05 * step), 0.5, y)), Vector((0.0, -1.0, 0.0)))
        if hit[0] is not None:
            return float(-hit[0].y)
    raise ValueError(f"no face at height {y}")


def _grid(name: str, columns: int, rows: int, point: Callable[[float, float], Vec3]) -> bpy.types.Object:
    """A quad grid surface from point(u, v) with u, v in 0..1 (game space), facing the viewer in front of the face."""
    verts = [point(u, v) for v in np.linspace(0.0, 1.0, rows + 1) for u in np.linspace(0.0, 1.0, columns + 1)]
    faces = []
    for r in range(rows):
        for c in range(columns):
            i = r * (columns + 1) + c
            faces.append((i, i + 1, i + columns + 2, i + columns + 1))
    return scene.mesh_object(name, verts, faces)


def _solidify(obj: bpy.types.Object, thickness: float) -> bpy.types.Object:
    mod = obj.modifiers.new("Thickness", "SOLIDIFY")
    mod.thickness = thickness
    mod.offset = 1.0
    return scene.bake(obj)


def _cap(head: bpy.types.Object) -> bpy.types.Object:
    """Bouffant cap: the skull above a tilted band line, puffed out and gathered into pleats toward the band."""
    cap = bpy.data.objects.new("Cap", head.data.copy())
    scene.link(cap)
    # The band runs low on the forehead, above the ears and down to the nape.
    scene.bisect(cap, (0.0, 0.03, 0.0), (0.0, 1.0, 0.2))

    def puff(p: NDArray[np.float64]) -> NDArray[np.float64]:
        # Pushed out from a point low in the skull, so the lower cap leans up and out instead of overhanging the band.
        out = p - np.array([0.0, -0.03, 0.02])
        direction = out / np.linalg.norm(out, axis=1)[:, None]
        # Height above the band line, 0..1 over the crown: the cap stands further off the skull the higher it goes.
        height = np.clip((p[:, 1] + 0.2 * p[:, 2] - 0.03) / 0.09, 0.0, 1.0)
        amount = 0.002 + 0.016 * height**0.7
        # Gathers: vertical pleats around the band, fading out over the crown.
        angle = np.arctan2(out[:, 2], out[:, 0])
        amount += 0.002 * np.sin(angle * 22.0) * (1.0 - height) ** 2
        return np.asarray(p + direction * amount[:, None], dtype=np.float64)

    cap = scene.deform(cap, puff)
    band = scene.finish(scene.rim("Band", cap, 0.0045), scene.material("tint", CAP, roughness=0.9))
    cap = _solidify(cap, 0.003)
    cap = scene.displace(cap, 0.0015, 0.015, detail=2, seed=23)
    return scene.join("Cap", scene.finish(cap, scene.material("tint", CAP, roughness=0.9)), band)


def _mask(head: bpy.types.Object) -> bpy.types.Object:
    """Pleated mask from the bridge of the nose to under the chin: it tents over nose and mouth and lies on the cheeks."""
    tree = _surface(head)
    top, bottom, half = -0.012, -0.128, 0.066
    # The center line, top to bottom: over the nose bridge, the nose tip and the lips, then tucked under the chin.
    profile_y = [bottom, -0.118, -0.105, -0.09, -0.07, -0.055, -0.04, -0.025, top]
    profile_z = [-0.078, -0.098, -0.108, -0.115, -0.125, -0.133, -0.134, -0.125, -0.118]

    def point(u: float, v: float) -> Vec3:
        x = (u - 0.5) * 2.0 * half
        y = top + (bottom - top) * v
        # The tent falls off to the sides like a cylinder until it lands on the cheeks, where it hugs the skin.
        tent = float(np.interp(y, profile_y, profile_z)) + 9.0 * x * x
        skin = _depth(tree, x, y) - 0.003
        hug = float(np.clip((abs(x) - 0.04) / 0.02, 0.0, 1.0))
        z = min(tent, skin) * (1.0 - hug) + skin * hug
        # Three horizontal pleats, each a fold standing a little off the surface.
        pleat = float(np.interp(y, [-0.095, -0.088, -0.081, -0.074, -0.067, -0.06, -0.053, -0.046], [0, 1, 0, 1, 0, 1, 0, 0]))
        z -= 0.003 * pleat * (1.0 - hug)
        return (float(x), float(y), float(z))

    mask = _grid("Mask", 22, 13, point)
    mask = _solidify(mask, 0.0015)
    return scene.finish(mask, scene.material("mask", MASK, roughness=0.9))


def _ear_loops(head: bpy.types.Object) -> bpy.types.Object:
    """Elastic loops from the mask's corners up over the root of each ear, behind it and back under the lobe."""
    tree = _surface(head)
    loops = []
    for side in (1.0, -1.0):
        x = 0.064 * side
        path: list[Vec3] = [
            (x, -0.018, _depth(tree, x, -0.018) - 0.002),
            (0.08 * side, 0.012, -0.03),
            (0.084 * side, 0.018, 0.0),
            (0.086 * side, 0.0, 0.024),
            (0.083 * side, -0.03, 0.012),
            (0.078 * side, -0.05, -0.03),
            (x, -0.112, _depth(tree, x, -0.112) - 0.002),
        ]
        loops.append(scene.curve_tube(f"EarLoop{side}", path, 0.0015, resolution=6))
    straps = scene.join("Straps", *loops)
    return scene.finish(straps, scene.material("mask", MASK, roughness=0.9))


def _glasses() -> tuple[bpy.types.Object, bpy.types.Object]:
    """Wrap-around safety glasses: one clear shield lens and a frame with a brow bar, nose piece and temple arms."""

    def lens_point(u: float, v: float) -> Vec3:
        x = (u - 0.5) * 0.138
        # Straight along the brow bar; the lower edge comes down under the eyes and rises toward the temples.
        height = 0.02 + 0.017 * float(np.sin(np.pi * u)) ** 0.5
        y = 0.021 - height * v
        # Wrapped around the face, clear of the nose bridge in the middle and back to the temples at the sides.
        z = -0.118 + 9.0 * x * x - 0.004 * v
        return (float(x), float(y), float(z))

    lens = _grid("Lens", 24, 6, lens_point)
    scene.finish(lens, scene.material("glass", (0.85, 0.9, 0.92), roughness=0.05, transmission=0.7))
    brow_bar = [lens_point(u, 0.0) for u in np.linspace(0.0, 1.0, 13)]
    brow_bar = [(x, y + 0.002, z) for x, y, z in brow_bar]
    parts = [scene.curve_tube("BrowBar", brow_bar, 0.003, resolution=8)]
    for side in (1.0, -1.0):
        x0, y0, z0 = brow_bar[0] if side < 0 else brow_bar[-1]
        temple: list[Vec3] = [(x0, y0, z0), (0.079 * side, 0.02, -0.035), (0.082 * side, 0.018, 0.0), (0.08 * side, 0.004, 0.022)]
        parts.append(scene.curve_tube(f"Temple{side}", temple, 0.0022, [1.0, 0.9, 0.8, 0.6], resolution=8))
        parts.append(scene.curve_tube(f"NosePad{side}", [(0.005 * side, 0.004, -0.112), (0.01 * side, -0.012, -0.108)], 0.0018, resolution=6))
    frame = scene.join("Frame", *parts)
    return lens, scene.finish(frame, scene.material("frame", FRAME, roughness=0.4))


def build() -> bpy.types.Object:
    head = _head_mesh()
    scene.finish(head, scene.material("skin", SKIN, roughness=0.55, subsurface=0.2))
    brows = face.eyebrows(head)
    eye_l, eye_r, lids = face.eyes()
    bpy.data.objects.remove(lids)
    for part in (head, brows, eye_l, eye_r):
        _upright(part)
    head = scene.bisect(head, NECK_BASE, (0.0, 1.0, 0.0))
    # The cap and mask are shaped on the dense face so their folds stay smooth after decimating.
    cap = _cap(head)
    mask = scene.join("Mask", _mask(head), _ear_loops(head))
    lens, frame = _glasses()
    head = scene.remove(head, covered)
    for part in (head, cap, mask, lens, frame, brows, eye_l, eye_r):
        scene.fit(part, BUDGETS[part.name])
    scene.clean()

    rig = scene.armature("SurgeonHeadRig", BONES)
    scene.bind(head, rig)
    scene.reweight(head, "Neck", neck_weight, "Head")
    for part in (cap, mask, lens, frame, brows, eye_l, eye_r):
        scene.attach(part, rig, "Head")
    return rig


def covered(p: NDArray[np.float64]) -> NDArray[np.bool_]:
    """Skin the cap or the mask hides: the skull above the band, and the face from under the eyes to the chin."""
    under_cap = p[:, 1] + 0.2 * p[:, 2] > 0.036
    under_mask = (np.abs(p[:, 0]) < 0.056) & (p[:, 1] < -0.018) & (p[:, 1] > -0.112) & (p[:, 2] < -0.055)
    return np.asarray(under_cap | under_mask, dtype=np.bool_)


def neck_weight(p: NDArray[np.float64]) -> NDArray[np.float64]:
    """How much each point follows the Neck bone (0..1): all of the neck at the collar, none of the head above the
    pivot, blended between so the throat stretches when the head turns. The chin and jaw stay with the head."""
    t = np.clip((PIVOT[1] - 0.01 - p[:, 1]) / (PIVOT[1] - 0.01 - NECK_BASE[1] - 0.01), 0.0, 1.0)
    # Below the jawline only: in front of the throat the jaw hangs lower than the pivot and belongs to the head.
    throat = np.clip((p[:, 2] + 0.07) / 0.03, 0.0, 1.0)
    w = t * t * (3.0 - 2.0 * t) * throat
    return np.asarray(w, dtype=np.float64)


def look_pose(rig: bpy.types.Object) -> None:
    """Review pose: head turned and tilted down as when looking at the patient, to check the neck weights."""
    scene.pose(rig, {"Head": (-25.0, 20.0, 0.0)})
