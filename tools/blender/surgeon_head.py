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
from pathlib import Path

import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from numpy.typing import NDArray
from scipy.ndimage import gaussian_filter1d

from . import face, scene
from .scene import Vec3, capsule, ellipsoid

# Material colors (display sRGB). The game recolors tint (cap), skin and mask (src/Surgeons/Surgeon.Body.cs).
SKIN = (0.8, 0.6, 0.5)
# The cap's royal blue and its weave come from the product photo in sources/.
CAP = (0.23, 0.405, 0.71)
CAP_PHOTO = Path(__file__).with_name("sources") / "cap_photo.jpg"
# The mask's blue and white ear loops are sampled from the product photo in sources/, as is its fiber grain.
MASK = (0.44, 0.785, 0.967)
LOOPS = (0.97, 0.97, 0.97)
PHOTO = Path(__file__).with_name("sources") / "mask_photo.png"
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
    "Mask": 1500,
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
    """The neck in the patient's frame: one soft column under the jaw and a little behind the face, the nape filled
    so the back of the skull flows into it, and the two long neck muscles."""
    elements = [
        capsule((0.49, -0.025, 0.0), (0.615, -0.03, 0.0), 0.045, 2.0),
        ellipsoid((0.615, -0.058, 0.0), (0.04, 0.032, 0.046), 1.5),
    ]
    for side in (1.0, -1.0):
        elements.append(capsule((0.59, 0.0, 0.04 * side), (0.5, 0.02, 0.018 * side), 0.01, 2.5))
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


# The cap map's u runs around the whole head while v covers only its height: anything isotropic on the map is
# stretched this much across the cloth, so its noise and weave are squeezed along u by the same factor.
CAP_STRETCH = 5.0


def _grain(shape: tuple[int, int], scale: float, amount: float, seed: int, stretch: float = 1.0) -> NDArray[np.float64]:
    """Fiber noise for cloth maps: smoothed random speckle, zero on average, `stretch` times finer along u."""
    from scipy.ndimage import gaussian_filter

    noise = gaussian_filter(np.random.default_rng(seed).standard_normal(shape), (scale, scale / stretch), mode="wrap")
    return np.asarray(noise / noise.std() * amount, dtype=np.float64)


def _cap_maps() -> tuple[NDArray[np.float64], NDArray[np.float64]]:
    """Cap texture, u around the head from the back, v from the band up to the crown: smooth woven cloth with a wide
    band topstitched along both edges, panel seams up the crown, and the photo's weave."""
    size = 512
    v, u = np.meshgrid(np.linspace(0.0, 1.0, size), np.linspace(0.0, 1.0, size), indexing="ij")
    # Soft folds above the band, flat over the crown where every column of the map converges on one point.
    folds = _grain((size, size), 24.0, 0.25, 4, CAP_STRETCH)
    height = folds * np.clip((1.0 - v) / 0.2, 0.0, 1.0) * np.clip((v - 0.2) / 0.1, 0.0, 1.0)
    seams = (np.abs(v - 0.22) < 0.004) | (np.abs(v - 0.03) < 0.004)
    for center in (0.0, 0.5, 1.0):
        seams |= (np.abs(u - center) < 0.004) & (v > 0.25)
    stitch = seams & (np.sin((u + v) * 2.0 * np.pi * 120.0) > 0.0)
    height = height - seams * 0.15 - stitch * 0.25
    color = scene.shade(CAP, height, 8.0, _photo_grain(size, CAP_PHOTO, slice(300, 360), slice(450, 750), 3.0, CAP_STRETCH) - 1.0)
    color[stitch] *= 0.8
    return color, scene.normal_map(height, 6.0)


def _photo_grain(size: int, photo_path: Path, rows: slice, columns: slice, detail: float, stretch: float = 1.0) -> NDArray[np.float64]:
    """The weave of the real cloth: a flat patch of a photo, mirrored into a seamless tile and repeated,
    as a brightness factor around 1, `stretch` times finer along u."""
    from PIL import Image
    from scipy.ndimage import gaussian_filter, zoom

    photo = np.asarray(Image.open(photo_path).convert("L"), dtype=np.float64) / 255.0
    patch = photo[rows, columns]
    # Only the weave finer than `detail` pixels: the photo's own lighting gradient would tile as blotches.
    patch = patch / gaussian_filter(patch, detail, mode="reflect") * patch.mean()
    patch = zoom(patch, (1.0, 1.0 / stretch))
    tile = np.block([[patch, patch[:, ::-1]], [patch[::-1], patch[::-1, ::-1]]])
    reps = (size // tile.shape[0] + 1, size // tile.shape[1] + 1)
    return np.asarray(np.tile(tile, reps)[:size, :size] / patch.mean(), dtype=np.float64)


def _mask_maps() -> tuple[NDArray[np.float64], NDArray[np.float64]]:
    """Mask texture, u across the face, v from the top edge down: three pleats as folds, the bound edge with two
    dashed stitch rows all around, the nose wire channel along the top, and the photo's nonwoven grain."""
    size = 512
    v, u = np.meshgrid(np.linspace(0.0, 1.0, size), np.linspace(0.0, 1.0, size), indexing="ij")
    # A sawtooth over the pleated band: each pleat's lower face lies flat, its upper face folds under.
    band = np.clip((v - 0.26) / 0.5, 0.0, 1.0)
    pleats = np.where((v > 0.26) & (v < 0.76), (band * 3.0) % 1.0, 0.0)
    height = pleats + _grain((size, size), 4.0, 0.02, 6)
    edge = np.minimum(np.minimum(u, 1.0 - u), np.minimum(v, 1.0 - v))
    hem = edge < 0.04
    dashes = np.sin((u + v) * 2.0 * np.pi * 110.0) > 0.0
    stitch = ((np.abs(edge - 0.014) < 0.003) | (np.abs(edge - 0.028) < 0.003)) & dashes
    wire = (v < 0.05) & (np.abs(u - 0.5) < 0.2)
    height = np.where(hem, 0.1, height) + wire * 0.3 - stitch * 0.3
    color = scene.shade(MASK, height, 12.0, _photo_grain(size, PHOTO, slice(240, 300), slice(160, 330), 12.0) - 1.0)
    # The bound edge is a paler, denser weave; its stitches a darker blue.
    color[hem] = color[hem] * 0.4 + np.array([0.75, 0.865, 0.93]) * 0.6
    color[stitch] = np.array([0.25, 0.5, 0.75])
    return color, scene.normal_map(height, 10.0)


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
        amount += 0.0012 * np.sin(angle * 26.0) * (1.0 - height) ** 2
        return np.asarray(p + direction * amount[:, None], dtype=np.float64)

    cap = scene.deform(cap, puff)
    band = scene.finish(scene.rim("Band", cap, 0.0045), scene.material("tint", CAP, roughness=0.9))
    cap = _solidify(cap, 0.003)
    cap = scene.displace(cap, 0.0015, 0.015, detail=2, seed=23)
    return scene.join("Cap", scene.finish(cap, scene.material("tint", CAP, roughness=0.9)), band)


def cap_uv(p: NDArray[np.float64]) -> NDArray[np.float64]:
    """u around the head starting at the back, v from the band line up over the crown."""
    u = (np.arctan2(p[:, 0], 0.02 - p[:, 2]) + np.pi) / (2.0 * np.pi)
    v = np.clip((p[:, 1] + 0.2 * p[:, 2] - 0.03) / 0.11, 0.0, 1.0)
    return np.column_stack([u, v])


def mask_uv(p: NDArray[np.float64]) -> NDArray[np.float64]:
    """u across the mask from the surgeon's left, v from its top edge down."""
    u = np.clip((p[:, 0] + 0.066) / 0.132, 0.0, 1.0)
    v = np.clip((-0.012 - p[:, 1]) / 0.116, 0.0, 1.0)
    return np.column_stack([u, v])


def _mask(head: bpy.types.Object) -> bpy.types.Object:
    """Pleated mask from the bridge of the nose to under the chin: it tents over nose and mouth and lies on the cheeks."""
    tree = _surface(head)
    top, bottom, half = -0.012, -0.128, 0.066
    # The center line, top to bottom: over the nose bridge, the nose tip and the lips, then tucked under the chin.
    profile_y = [bottom, -0.118, -0.105, -0.09, -0.07, -0.055, -0.04, -0.025, top]
    profile_z = [-0.078, -0.098, -0.108, -0.115, -0.125, -0.133, -0.134, -0.125, -0.118]
    columns, rows = 22, 16
    xs = (np.linspace(0.0, 1.0, columns + 1) - 0.5) * 2.0 * half
    ys = top + (bottom - top) * np.linspace(0.0, 1.0, rows + 1)
    # The skin depth under every grid point, smoothed down each column: beside the jaw the samples jump from row to
    # row, and cloth stretched over them runs in a straight line, not a zigzag.
    skin = gaussian_filter1d(np.array([[_depth(tree, x, y) for x in xs] for y in ys]), 1.2, axis=0, mode="nearest") - 0.003

    def point(u: float, v: float) -> Vec3:
        x = (u - 0.5) * 2.0 * half
        y = top + (bottom - top) * v
        # The tent falls off to the sides like a cylinder until it lands on the cheeks, where it hugs the skin.
        tent = float(np.interp(y, profile_y, profile_z)) + 9.0 * x * x
        on_skin = float(skin[round(v * rows), round(u * columns)])
        hug = float(np.clip((abs(x) - 0.04) / 0.02, 0.0, 1.0))
        z = min(tent, on_skin) * (1.0 - hug) + on_skin * hug
        # Three horizontal pleats, each a fold standing a little off the surface.
        pleat = float(np.interp(y, [-0.095, -0.088, -0.081, -0.074, -0.067, -0.06, -0.053, -0.046], [0, 1, 0, 1, 0, 1, 0, 0]))
        z -= 0.003 * pleat * (1.0 - hug)
        return (float(x), float(y), float(z))

    mask = _grid("Mask", columns, rows, point)
    mask = _solidify(mask, 0.0015)
    return scene.finish(mask, scene.material("mask", MASK, roughness=0.9))


def _ear_loops(head: bpy.types.Object) -> bpy.types.Object:
    """Elastic loops from the mask's corners up over the root of each ear, behind it and back under the lobe."""
    tree = _surface(head)
    loops = []
    for side in (1.0, -1.0):
        # The loops end just inside the mask's corners, between the cloth and the skin.
        x = 0.06 * side
        path: list[Vec3] = [
            (x, -0.02, _depth(tree, x, -0.02) - 0.001),
            (0.08 * side, 0.012, -0.03),
            (0.084 * side, 0.018, 0.0),
            (0.086 * side, 0.0, 0.024),
            (0.083 * side, -0.03, 0.012),
            (0.078 * side, -0.05, -0.03),
            (x, -0.11, _depth(tree, x, -0.11) - 0.001),
        ]
        loops.append(scene.curve_tube(f"EarLoop{side}", path, 0.0015, resolution=6))
    straps = scene.join("Straps", *loops)
    return scene.finish(straps, scene.material("elastic", LOOPS, roughness=0.7))


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


def build(out_dir: Path) -> bpy.types.Object:
    """Builds the head; the generated texture maps are written next to the review renders in out_dir."""
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
    scene.uv_from(cap, cap_uv)
    scene.texture(bpy.data.materials["tint"], *_cap_maps(), out_dir)
    scene.uv_from(mask, mask_uv)
    scene.texture(bpy.data.materials["mask"], *_mask_maps(), out_dir)

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
