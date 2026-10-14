"""Alternative doctor body from a photogrammetry scan of a painted figurine, rebuilt in a T-pose and rigged.

The scan (sources/scan_doctor.glb) is one fused, textured shell. The figurine stands with crossed legs, a sway in the
waist and both hands gripping the ends of a cape that spans her back, so it is rebuilt in parts:
- Torso, a short capelet and the left shoe stay scanned. The head is cut off at the neck (the game's head goes on
  top), the rest of the cape, the arms and the legs are cut away, and the holes closed with smooth caps.
- Legs are measured on the scan as tubes: rays cast out of a centerline give each ring's outline, and a finer grid of
  rays its colors. Where the thighs press together the outline and colors are filled in from the clean rings around
  them. Laid out straight, a measured ring keeps its shape, so the legs come out straight without stretching.
- The right shoe is the left one mirrored, red and blue swapped: on the scan its toe is buried in the left heel.
- Arms are tubes in the scan's skin and glove colors: the scanned arms are tangled in the cape. Hands are the glove
  generator's hand (hand.py) in skin color, as the scanned fists are fused around the cape.
- The upper body is shifted upright over the hips and the figure turned to face -Z.
The parts are fused with a voxel remesh, settled back onto their surfaces, decimated, unwrapped and baked into one
texture from the scan's own.

Model space as the surgeon's body: feet on the floor at the origin, facing -Z, Y up, her right toward +X. Scaled to
the surgeon's hip height, which puts the shoulders at the surgeon's too.
Bones: Torso, Chest, Neck, Head (where the head sits, no weights), UpperArm/Forearm/Hand L/R with
Index/Middle/Ring/Pinky/Thumb 1-3 L/R, Thigh/Shin/Foot L/R. Material: one textured `figure`.
"""

from __future__ import annotations

import math
from pathlib import Path

import bpy

# isort: split
import bmesh
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from numpy.typing import NDArray

from . import hand, scene
from .scene import Vec3

SOURCE = Path(__file__).resolve().parent / "sources" / "scan_doctor.glb"
# The figure is scaled to put the hips where the surgeon's are (Surgeon.HipHeight): the shoulders then come out at the
# surgeon's height too. The head stays the game's: the model ends at the neck.
HIP_HEIGHT = 0.95
# Triangles: the fused body, and each hand on its own.
BUDGET = 40000
HAND_BUDGET = 3000
# Voxel size the parts are fused at, and the baked texture's size.
VOXEL = 0.0011
TEXTURE = 4096
# Rays (and mesh columns) around each ring of a limb, and the finer grid its colors are cast on.
ANGLES = 128
COLOR_ANGLES = 512
COLOR_STEP = 0.00025

# Everything below is measured on the scan in its own units: the figurine is 0.807 tall, feet at 0, the face toward
# -Z, her right toward +X. Joints are where the limb's centerline meets them, in the scan's pose.

# The sway: the chest sits this far toward her left of the hips, shifted back over them across the waist (y range).
SWAY = (-0.043, 0.33, 0.4)
# The whole figure is turned this far (degrees around Y) toward her left, the hips further than the chest; turned back
# around the pelvis, the twist undone across the waist (y range).
TURN = 20.0
HIP_TURN = (22.0, 0.33, 0.38)
PELVIS = (0.008, 0.32, -0.038)
# Rest hip joints sit this far either side of the pelvis center.
HIP_X = 0.035
# Leg centerlines from the hip to inside the shoe; the thighs cross, the left one in front.
LEGS = {
    "L": [
        (0.0, 0.33, -0.05),
        (0.012, 0.275, -0.062),
        (0.014, 0.25, -0.066),
        (0.012, 0.195, -0.066),
        (0.019, 0.15, -0.067),
        (0.016, 0.1, -0.069),
        (0.018, 0.06, -0.069),
    ],
    "R": [
        (0.04, 0.33, -0.025),
        (0.033, 0.25, -0.02),
        (0.046, 0.19, -0.014),
        (0.06, 0.16, -0.01),
        (0.07, 0.13, -0.011),
        (0.085, 0.09, -0.016),
        (0.092, 0.06, -0.025),
    ],
}
# Thigh top across and front to back, where the thighs are fused and the scan has no clean ring.
HIP_RADII = (0.038, 0.042)
# Shoes stay scanned below this height; the stocking tubes reach a little into them.
ANKLE = 0.075
# Surface points the arm colors are picked up from: skin on the upper arm, the long gloves on the forearms.
SKIN_SPOT = (0.05, 0.452, -0.03)
GLOVE_SPOTS = {"R": (0.078, 0.49, -0.035), "L": (-0.1, 0.42, 0.065)}
# Rest shoulder joints (after straightening), arm lengths and radii (shoulder, elbow, wrist), from the clean
# stretches of the scanned arms. The gloves start a little above the elbow.
SHOULDERS = {"R": (0.037, 0.458, 0.0), "L": (-0.037, 0.458, 0.0)}
UPPER_ARM = 0.075
FOREARM = 0.058
ARM_RADII = (0.0135, 0.0115, 0.009)
GLOVE_TOP = 0.068
# Rounded shoulder over the arm's root: offset out from the joint (x away from the body, y, z) and radii (x, z, y in
# Blender's axes, as the sphere is scaled there).
DELTOID = ((0.0, 0.002, 0.0), (0.017, 0.016, 0.016))
# Glove generator hand scaled to the figurine's wrist, and how far it reaches back into the forearm.
HAND_SCALE = 0.21
HAND_SOCKET = 0.02
# Rest joints along the body's middle and the legs (heights), and where the foot bone points.
SPINE_JOINTS = (0.31, 0.38, 0.47, 0.5, 0.56)
KNEE = 0.185
TOE = (0.0, 0.012, -0.05)

# The upper body is kept within reach of the spine and the shoulder line: that keeps the chest, the shoulders and
# the cape where it lies on them (the capelet), and drops the arms and the cape flaring out to the hands. The neck is
# cut flat at the top, under the chin, for the game's head.
UPPER = 0.375
NECK_TOP = 0.5
# Above this height only what lies this close to the spine line is neck.
NECK = (0.49, 0.034)
SPINE = ((0.0, 0.35, -0.044), (-0.035, 0.5, -0.03), 0.055)
SHOULDER_LINE = ((-0.01, 0.458, -0.03), (-0.06, 0.458, 0.0), 0.027)
# Legs and shoes go below this height.
HIPS = 0.3
# Boxes: center, size.
Box = tuple[Vec3, Vec3]
# Cut even within reach of the spine: the cape bunched up under the right fist and the pigtail hanging in front of
# the left shoulder.
CUTS: list[Box] = [
    ((0.075, 0.48, -0.01), (0.09, 0.08, 0.22)),
    ((-0.2, 0.48, -0.09), (0.2, 0.1, 0.12)),
    # The cape flaring up over the right shoulder toward the hand.
    ((0.04, 0.5, -0.02), (0.05, 0.05, 0.12)),
]
# The bust stands out further from the spine than the back: kept whole.
BUST: Box = ((-0.035, 0.41, -0.065), (0.11, 0.07, 0.07))
# The soles are fused and the right toe tucks against the left heel: seen from above, the left shoe is what lies
# nearer its own heel-to-toe line (x, z) than the right one's.
SHOE_LINES = {"L": ((0.034, -0.056), (-0.012, -0.108)), "R": ((0.094, -0.022), (0.056, -0.06))}
# Where they touch, a plane between the left heel and the right toe gives both a clean cut (point, normal toward the
# right shoe).
SHOE_SPLIT = ((0.045, 0.0, -0.058), (0.98, 0.0, -0.18))
# Hue of the figurine's blue (red is 0): the mirrored right shoe swaps the two.
FIGURINE_BLUE_HUE = 0.57


def _game(v: Vector) -> NDArray[np.float64]:
    return np.array([v.x, v.z, -v.y])


def _yaw(degrees: float) -> NDArray[np.float64]:
    """Game-space rotation matrix around Y."""
    a = math.radians(degrees)
    return np.array([[math.cos(a), 0.0, math.sin(a)], [0.0, 1.0, 0.0], [-math.sin(a), 0.0, math.cos(a)]])


def _in_box(points: NDArray[np.float64], box: Box) -> NDArray[np.bool_]:
    center, size = box
    inside: NDArray[np.bool_] = np.all(np.abs(points - np.array(center)) <= np.array(size) / 2, axis=1)
    return inside


class Scan:
    """The scanned figurine as imported: mesh object, texture pixels and a ray tree, all in game space."""

    def __init__(self) -> None:
        bpy.ops.import_scene.gltf(filepath=str(SOURCE))
        obj = next(o for o in bpy.context.scene.objects if o.type == "MESH")
        # The scan comes in lying down with its face toward +X: stand it up facing -Z (game), feet on the floor.
        obj.rotation_mode = "XYZ"
        obj.rotation_euler = (math.radians(-90.0), 0.0, 0.0)
        bpy.context.view_layer.update()
        obj.data.transform(Matrix.Rotation(math.radians(-90.0), 4, "Z") @ obj.matrix_world)
        obj.matrix_world = Matrix.Identity(4)
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        # Vertices are split along every texture seam; welded, the shell is one closed surface.
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
        low = min(v.co.z for v in bm.verts)
        bmesh.ops.translate(bm, verts=bm.verts, vec=(0.0, 0.0, -low))
        bm.to_mesh(obj.data)
        bm.free()
        obj.name = obj.data.name = "Scan"
        self.obj = obj
        self.image = obj.data.materials[0].node_tree.nodes["Image Texture"].image
        width, height = self.image.size
        self.pixels = np.array(self.image.pixels[:], dtype=np.float64).reshape(height, width, 4)[:, :, :3]
        self.bm = bmesh.new()
        self.bm.from_mesh(obj.data)
        self.bm.faces.ensure_lookup_table()
        self.uv = self.bm.loops.layers.uv.active
        self.tree = BVHTree.FromBMesh(self.bm)
        # Every face's corners and texture coordinates, for sampling the texture at many points at once.
        self.corners = np.array([[loop.vert.co[:] for loop in face.loops] for face in self.bm.faces])
        self.corner_uvs = np.array([[loop[self.uv].uv[:] for loop in face.loops] for face in self.bm.faces])

    def texel(self, uv: Vector) -> NDArray[np.float64]:
        """Texture color (display values) at a texture coordinate."""
        h, w = self.pixels.shape[:2]
        color: NDArray[np.float64] = self.pixels[int(np.clip(uv.y % 1.0 * h, 0, h - 1)), int(np.clip(uv.x % 1.0 * w, 0, w - 1))]
        return color

    def colors(self, faces: NDArray[np.int64], points: NDArray[np.float64]) -> NDArray[np.float64]:
        """Texture colors (display values, blended between texels) at Blender-space points on the given faces."""
        a, b, c = (self.corners[faces, k] for k in range(3))
        ab, ac, ap = b - a, c - a, points - a
        d00, d01, d11 = (ab * ab).sum(1), (ab * ac).sum(1), (ac * ac).sum(1)
        d20, d21 = (ap * ab).sum(1), (ap * ac).sum(1)
        denominator = np.where(np.abs(d00 * d11 - d01 * d01) > 1e-30, d00 * d11 - d01 * d01, 1e-30)
        v = (d11 * d20 - d01 * d21) / denominator
        w = (d00 * d21 - d01 * d20) / denominator
        uv = self.corner_uvs[faces, 0] * (1.0 - v - w)[:, None] + self.corner_uvs[faces, 1] * v[:, None] + self.corner_uvs[faces, 2] * w[:, None]
        h, wide = self.pixels.shape[:2]
        x = np.clip(uv[:, 0] % 1.0 * wide - 0.5, 0.0, wide - 1.001)
        y = np.clip(uv[:, 1] % 1.0 * h - 0.5, 0.0, h - 1.001)
        x0, y0 = x.astype(int), y.astype(int)
        fx, fy = (x - x0)[:, None], (y - y0)[:, None]
        top = self.pixels[y0, x0] * (1.0 - fx) + self.pixels[y0, x0 + 1] * fx
        bottom = self.pixels[y0 + 1, x0] * (1.0 - fx) + self.pixels[y0 + 1, x0 + 1] * fx
        blended: NDArray[np.float64] = top * (1.0 - fy) + bottom * fy
        return blended

    def cast(self, curve: NDArray[np.float64], angles: int) -> tuple[NDArray[np.float64], NDArray[np.float64]]:
        """Rays out of each point of a centerline (game space), `angles` around it: distance to the surface (inf for
        a miss) and the surface color there, per (point, angle)."""
        _tangent, u, w = _frames(curve)
        a = np.linspace(0.0, 2.0 * math.pi, angles, endpoint=False)
        directions = np.cos(a)[None, :, None] * u[:, None, :] + np.sin(a)[None, :, None] * w[:, None, :]
        distance = np.full((len(curve), angles), np.inf)
        faces = np.zeros((len(curve), angles), dtype=np.int64)
        points = np.zeros((len(curve), angles, 3))
        for i, center in enumerate(curve):
            origin = scene.to_blender(center)
            for j in range(angles):
                hit, _normal, index, dist = self.tree.ray_cast(origin, scene.to_blender(directions[i, j]), 0.08)
                if hit is not None:
                    distance[i, j], faces[i, j], points[i, j] = dist, index, hit[:]
        colors = self.colors(faces.ravel(), points.reshape(-1, 3)).reshape(len(curve), angles, 3)
        return distance, colors

    def color_around(self, point: Vec3, radius: float = 0.012) -> NDArray[np.float64]:
        """Median texture color of the surface within `radius`: steadier than one texel against painted shading."""
        hits = self.tree.find_nearest_range(scene.to_blender(point), radius)
        if not hits:
            hit, _normal, index, _dist = self.tree.find_nearest(scene.to_blender(point))
            hits = [(hit, None, index, 0.0)]
        faces = np.array([index for _co, _normal, index, _dist in hits])
        median: NDArray[np.float64] = np.median(self.colors(faces, np.array([co[:] for co, *_ in hits])), axis=0)
        return median

    def piece(self, name: str, keep: NDArray[np.bool_], planes: list[tuple[Vec3, Vec3]] | None = None, erode: int = 0) -> bpy.types.Object:
        """A copy of the scan with only the faces in `keep`, trimmed to the side of each (point, normal) plane its normal
        points to, its holes closed so it is solid again. `erode` rings of faces come off every cut edge first, which
        takes away slivers too thin to read as anything (the ragged edge of a cut cloth)."""
        obj = self.obj.copy()
        obj.data = self.obj.data.copy()
        obj.name = obj.data.name = name
        scene.link(obj)
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        bm.faces.ensure_lookup_table()
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if not keep[f.index]], context="FACES")
        for point, normal in planes or []:
            geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
            bmesh.ops.bisect_plane(bm, geom=geom, plane_co=scene.to_blender(point), plane_no=scene.to_blender(normal), clear_inner=True)
        for _ in range(erode):
            bmesh.ops.delete(bm, geom=[f for f in bm.faces if any(e.is_boundary for e in f.edges)], context="FACES")
        # Bits left floating by the cuts (a fingertip, a cape scrap, the other shoe's heel) go.
        _drop_crumbs(bm, 0.5 if planes else 0.02)
        _smooth_rims(bm)
        filled = bmesh.ops.holes_fill(bm, edges=bm.edges, sides=0)["faces"]
        if any(e.is_boundary for e in bm.edges):
            raise RuntimeError(f"{name} has holes that would not close")
        patches = bmesh.ops.triangulate(bm, faces=filled, quad_method="BEAUTY", ngon_method="BEAUTY")["faces"]
        for face in patches:
            face.material_index = 1
        _fair(bm)
        # A cap has no texture of its own: it takes the colors around its rim, blended across, from a second material.
        uv = bm.loops.layers.uv.active
        rim = {}
        for vert in bm.verts:
            textured = [self.texel(corner[uv].uv) for corner in vert.link_loops if corner.face.material_index == 0]
            if textured and any(face.material_index == 1 for face in vert.link_faces):
                rim[vert] = np.mean(textured, axis=0)
        colors = bm.loops.layers.float_color.new("patch")
        for vert, color in _blend_inward(bm, rim).items():
            # The attribute holds linear color.
            for corner in vert.link_loops:
                corner[colors] = (*(scene.to_linear(float(c)) for c in color), 1.0)
        bm.to_mesh(obj.data)
        bm.free()
        obj.data.materials.append(_patch_material())
        return obj

    def face_centers(self) -> NDArray[np.float64]:
        return np.array([_game(f.calc_center_median()) for f in self.bm.faces])


def _patch_material() -> bpy.types.Material:
    mat = bpy.data.materials.get("ScanPatch")
    if mat is None:
        mat = bpy.data.materials.new("ScanPatch")
        mat.use_nodes = True
        nodes = mat.node_tree.nodes
        attribute = nodes.new("ShaderNodeAttribute")
        attribute.attribute_name = "patch"
        mat.node_tree.links.new(attribute.outputs["Color"], nodes["Principled BSDF"].inputs["Base Color"])
    return mat


def _fair(bm: bmesh.types.BMesh) -> None:
    """Turns each cap (material 1) from a flat fan into a smooth membrane across its rim: its inner edges are split
    a few times and the new points relaxed, the rim kept where it is."""
    for _ in range(3):
        inner = [e for e in bm.edges if all(face.material_index == 1 for face in e.link_faces) and len(e.link_faces) == 2]
        bmesh.ops.subdivide_edges(bm, edges=inner, cuts=1)
        bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if f.material_index == 1 and len(f.verts) > 3])
    free = [v for v in bm.verts if v.link_faces and all(face.material_index == 1 for face in v.link_faces)]
    for _ in range(60):
        bmesh.ops.smooth_vert(bm, verts=free, factor=0.8, use_axis_x=True, use_axis_y=True, use_axis_z=True)


def _blend_inward(bm: bmesh.types.BMesh, rim: dict[bmesh.types.BMVert, NDArray[np.float64]]) -> dict[bmesh.types.BMVert, NDArray[np.float64]]:
    """Colors for every cap vertex: the rim's own, spread smoothly to the inside (repeated neighbor averaging)."""
    inside = [v for v in bm.verts if v not in rim and v.link_faces and all(face.material_index == 1 for face in v.link_faces)]
    colors = dict(rim)
    fallback = np.mean(list(rim.values()), axis=0) if rim else np.full(3, 0.5)
    for vert in inside:
        colors[vert] = fallback
    for _ in range(80):
        for vert in inside:
            colors[vert] = np.mean([colors[e.other_vert(vert)] for e in vert.link_edges if e.other_vert(vert) in colors], axis=0)
    return colors


def _smooth_rims(bm: bmesh.types.BMesh) -> None:
    """Trims a cut's ragged rim until each hole is one simple loop that can be filled: faces hanging on by one edge
    and vertices where two holes touch go."""
    for _ in range(20):
        spikes = {f for f in bm.faces if sum(e.is_boundary for e in f.edges) >= 2}
        for vert in bm.verts:
            if sum(e.is_boundary for e in vert.link_edges) > 2:
                spikes.update(vert.link_faces)
        if not spikes:
            break
        bmesh.ops.delete(bm, geom=list(spikes), context="FACES")
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")


def _drop_crumbs(bm: bmesh.types.BMesh, share: float = 0.02) -> None:
    """Deletes connected shells with less than `share` of the faces."""
    seen: set[int] = set()
    shells: list[list[bmesh.types.BMFace]] = []
    for start in bm.faces:
        if start.index in seen:
            continue
        shell, stack = [], [start]
        seen.add(start.index)
        while stack:
            face = stack.pop()
            shell.append(face)
            for edge in face.edges:
                for other in edge.link_faces:
                    if other.index not in seen:
                        seen.add(other.index)
                        stack.append(other)
        shells.append(shell)
    total = sum(len(s) for s in shells)
    bmesh.ops.delete(bm, geom=[f for s in shells if len(s) < total * share for f in s], context="FACES")


def _segment_distance(points: NDArray[np.float64], a: Vec3, b: Vec3) -> NDArray[np.float64]:
    start, end = np.array(a), np.array(b)
    t = np.clip((points - start) @ (end - start) / np.dot(end - start, end - start), 0.0, 1.0)
    distance: NDArray[np.float64] = np.linalg.norm(points - (start + t[:, None] * (end - start)), axis=1)
    return distance


def torso_faces(centers: NDArray[np.float64]) -> NDArray[np.bool_]:
    """Faces of the scan the torso piece keeps."""
    y = centers[:, 1]
    near = (_segment_distance(centers, SPINE[0], SPINE[1]) < SPINE[2]) | (_segment_distance(centers, SHOULDER_LINE[0], SHOULDER_LINE[1]) < SHOULDER_LINE[2])
    keep = (y >= HIPS) & (y < NECK_TOP + 0.01) & ((y < UPPER) | near | _in_box(centers, BUST))
    # Just under the chin only the neck itself: the hair's underside comes down close around it.
    keep &= (y < NECK[0]) | (_segment_distance(centers, SPINE[0], SPINE[1]) < NECK[1])
    for box in CUTS:
        keep &= ~_in_box(centers, box)
    return keep


# --- Straightening ------------------------------------------------------------------------------------------------


def _smoothstep(edge0: float, edge1: float, x: NDArray[np.float64]) -> NDArray[np.float64]:
    t = np.clip((x - edge0) / (edge1 - edge0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def straighten(points: NDArray[np.float64]) -> NDArray[np.float64]:
    """Scan pose to rest pose for the torso: the chest shifted back over the hips, the figure turned to face -Z with
    the pelvis on the Y axis. The chest isn't tilted, only shifted, so the head on it stays upright."""
    p = np.array(points, dtype=np.float64).reshape(-1, 3)
    p[:, 0] -= SWAY[0] * _smoothstep(SWAY[1], SWAY[2], p[:, 1])
    pelvis = np.array([PELVIS[0], 0.0, PELVIS[2]])
    p -= pelvis
    angle = np.radians(-TURN - HIP_TURN[0] * (1.0 - _smoothstep(HIP_TURN[1], HIP_TURN[2], p[:, 1])))
    cos, sin = np.cos(angle), np.sin(angle)
    return np.column_stack([p[:, 0] * cos + p[:, 2] * sin, p[:, 1], -p[:, 0] * sin + p[:, 2] * cos])


def _straighten_object(obj: bpy.types.Object) -> None:
    count = len(obj.data.vertices)
    co = np.empty(count * 3)
    obj.data.vertices.foreach_get("co", co)
    b = co.reshape(-1, 3)
    g = straighten(np.column_stack([b[:, 0], b[:, 2], -b[:, 1]]))
    obj.data.vertices.foreach_set("co", np.column_stack([g[:, 0], -g[:, 2], g[:, 1]]).ravel())
    obj.data.update()


# --- Tubes --------------------------------------------------------------------------------------------------------


def _resample(points: NDArray[np.float64], step: float) -> NDArray[np.float64]:
    """A polyline smoothed (Chaikin corner cutting) and resampled to even steps."""
    p = np.asarray(points, dtype=np.float64)
    for _ in range(3):
        cut = [p[0]]
        for a, b in zip(p[:-1], p[1:], strict=True):
            cut += [a * 0.75 + b * 0.25, a * 0.25 + b * 0.75]
        p = np.array([*cut, p[-1]])
    lengths = np.r_[0.0, np.cumsum(np.linalg.norm(np.diff(p, axis=0), axis=1))]
    s = np.linspace(0.0, lengths[-1], max(2, int(round(lengths[-1] / step)) + 1))
    return np.array([np.interp(s, lengths, p[:, k]) for k in range(3)]).T


def _frames(curve: NDArray[np.float64]) -> tuple[NDArray[np.float64], NDArray[np.float64], NDArray[np.float64]]:
    """Tangent and two normals along the curve, turning as little as possible (rotation-minimizing)."""
    t = np.gradient(curve, axis=0)
    t /= np.linalg.norm(t, axis=1)[:, None]
    first = np.cross(t[0], (0.0, 0.0, 1.0) if abs(t[0][2]) < 0.9 else (1.0, 0.0, 0.0))
    us = [first / np.linalg.norm(first)]
    for tangent in t[1:]:
        u = us[-1] - np.dot(us[-1], tangent) * tangent
        us.append(u / np.linalg.norm(u))
    u = np.array(us)
    return t, u, np.cross(t, u)


def _fill_columns(values: NDArray[np.float64], good: NDArray[np.bool_], nearest: bool = False) -> NDArray[np.float64]:
    """Replaces bad samples of a (ring, angle, ...) grid along each angle's column: interpolated between the good
    ones, or copied from the nearest (colors, so a painted edge like a stocking's top stays sharp)."""
    out = values.reshape(values.shape[0], values.shape[1], -1).copy()
    rows = np.arange(len(values))
    for j in range(out.shape[1]):
        known = rows[good[:, j]]
        if len(known) == 0:
            continue
        bad = rows[~good[:, j]]
        if nearest:
            after = np.clip(np.searchsorted(known, bad), 0, len(known) - 1)
            before = np.clip(after - 1, 0, len(known) - 1)
            source = np.where(np.abs(known[before] - bad) <= np.abs(known[after] - bad), known[before], known[after])
            out[bad, j] = out[source, j]
        else:
            for k in range(out.shape[2]):
                out[bad, j, k] = np.interp(bad, known, out[known, j, k])
    return out.reshape(values.shape)


def _clean_rays(distance: NDArray[np.float64], reference: NDArray[np.float64]) -> NDArray[np.bool_]:
    """Rays that ended near their ring's own radius, not inside a neighbor fused to the limb."""
    clean: NDArray[np.bool_] = np.isfinite(distance) & (distance < reference[:, None] * 1.4) & (distance > reference[:, None] * 0.6)
    return clean


def _fill(values: NDArray[np.float64], good: NDArray[np.bool_], rings: NDArray[np.bool_], nearest: bool = False) -> NDArray[np.float64]:
    """Fills the bad samples of a (ring, angle, ...) grid: around the ring in the given rings, then along the limb."""
    out = values.copy()
    for i in np.nonzero(rings)[0]:
        out[i] = _fill_around(out[i], good[i])
    return _fill_columns(out, np.repeat(rings[:, None], values.shape[1], axis=1), nearest)


def _fill_around(values: NDArray[np.float64], good: NDArray[np.bool_]) -> NDArray[np.float64]:
    """Replaces bad samples of one ring by interpolating between the good ones around it."""
    out = values.reshape(len(values), -1).copy()
    angles = np.arange(len(values))
    known = angles[good]
    wrapped = np.r_[known - len(values), known, known + len(values)]
    for k in range(out.shape[1]):
        out[~good, k] = np.interp(angles[~good], wrapped, np.tile(out[good, k], 3))
    return out.reshape(values.shape)


class Tube:
    """A limb surface as rings of radii around a centerline (ring, angle) with a color per sample."""

    def __init__(self, radii: NDArray[np.float64], colors: NDArray[np.float64], length: float) -> None:
        self.radii = radii
        self.colors = colors
        self.length = length
        # Measured tubes: the centerline's direction and angle-0 normal at the far end, in the scan's pose.
        self.end_tangent = np.array([0.0, -1.0, 0.0])
        self.end_normal = np.array([1.0, 0.0, 0.0])
        # First ring of a measured tube with a clean outline.
        self.top = 0

    @staticmethod
    def measure(scan: Scan, centerline: list[Vec3]) -> Tube:
        """Rays cast out of the centerline find the scan's surface around it. A ray that runs much further than its
        ring's usual radius went through a place where this limb is fused to something (the other thigh): it and
        any ring with too few clean rays are filled in from the clean rings above and below."""
        curve = _resample(np.array(centerline), 0.001)
        tangent, u, _w = _frames(curve)
        radii, _colors = scan.cast(curve, ANGLES)
        # A ring's own radius from its shorter rays: rays into a fused neighbor only ever run long.
        reference = np.nan_to_num(np.nanpercentile(np.where(np.isfinite(radii), radii, np.nan), 30, axis=1), nan=0.0)
        good = _clean_rays(radii, reference)
        # Rings with enough clean rays fill their gaps around the ring, the others along the limb. The outline takes
        # only rings that are nearly all clean.
        clean = good.mean(axis=1) > 0.85
        if not clean.any():
            raise RuntimeError("no clean ring on the limb")
        radii = _fill(np.where(good, radii, 0.0), good, clean)
        # Colors are cast again much finer, for the texture's detail. They take any ring with a fair share of clean
        # rays: the outer side of a fused thigh still shows its skin and stocking top.
        fine = _resample(np.array(centerline), COLOR_STEP)
        distance, colors = scan.cast(fine, COLOR_ANGLES)
        along = np.interp(np.linspace(0.0, 1.0, len(fine)), np.linspace(0.0, 1.0, len(curve)), reference)
        good = _clean_rays(distance, along)
        colors = _fill(colors, good, good.mean(axis=1) > 0.4, nearest=True)
        tube = Tube(radii, colors, float(np.sum(np.linalg.norm(np.diff(curve, axis=0), axis=1))))
        tube.end_tangent, tube.end_normal = tangent[-1], u[-1]
        tube.top = int(np.argmax(clean))
        return tube

    def widen_top(self, radius: NDArray[np.float64]) -> None:
        """Above the topmost clean ring the outline blends to `radius` (per angle) at the top."""
        if self.top > 0:
            blend = _smoothstep(0.0, 1.0, np.linspace(1.0, 0.0, self.top + 1))[:-1, None]
            self.radii[: self.top] = self.radii[self.top] * (1.0 - blend) + radius[None, :] * blend
        self.smooth()

    def smooth(self) -> None:
        """A light smoothing around and along takes out ray noise."""
        radii = self.radii
        for _ in range(2):
            radii = (np.roll(radii, 1, axis=1) + 2.0 * radii + np.roll(radii, -1, axis=1)) / 4.0
            radii[1:-1] = (radii[:-2] + 2.0 * radii[1:-1] + radii[2:]) / 4.0
        self.radii = radii

    @staticmethod
    def round(profile: list[tuple[float, float]], colors: list[tuple[float, NDArray[np.float64]]], length: float) -> Tube:
        """A round tube: radius and color at points along it (share of the length, value), blended in between."""
        rings = max(8, int(length / 0.002))
        s = np.linspace(0.0, 1.0, rings)
        r = np.interp(s, [p[0] for p in profile], [p[1] for p in profile])
        c = np.array([np.interp(s, [p[0] for p in colors], [p[1][k] for p in colors]) for k in range(3)]).T
        return Tube(np.repeat(r[:, None], ANGLES, axis=1), np.repeat(c[:, None, :], ANGLES, axis=1), length)

    def build(self, name: str, start: Vec3, direction: Vec3, normal: Vec3) -> bpy.types.Object:
        """Mesh of the tube laid out straight from `start` along `direction`; angle 0 points along `normal`.
        Closed at both ends, textured with its own colors."""
        d = np.array(direction) / np.linalg.norm(direction)
        u = np.array(normal) - np.dot(normal, d) * d
        u /= np.linalg.norm(u)
        w = np.cross(d, u)
        rings, angles = self.radii.shape
        color_rings, color_angles = self.colors.shape[:2]
        a = np.linspace(0.0, 2.0 * math.pi, angles + 1)
        verts: list[NDArray[np.float64]] = []
        uvs: list[tuple[float, float]] = []
        # The color image has one more column than angles, a copy of the first, so the texture wraps around smoothly.
        for i in range(rings):
            center = np.array(start) + d * self.length * i / (rings - 1)
            for j in range(angles + 1):
                verts.append(center + self.radii[i, j % angles] * (math.cos(a[j]) * u + math.sin(a[j]) * w))
                uvs.append(((j / angles * color_angles + 0.5) / (color_angles + 1), (i / (rings - 1) * (color_rings - 1) + 0.5) / color_rings))
        faces = []
        loop_uvs = []
        for i in range(rings - 1):
            for j in range(angles):
                quad = [i * (angles + 1) + j, i * (angles + 1) + j + 1, (i + 1) * (angles + 1) + j + 1, (i + 1) * (angles + 1) + j]
                faces.append(quad)
                loop_uvs += [uvs[k] for k in quad]
        # End caps: a fan from each end's center, colored like the ring it closes.
        for i, flip in ((0, True), (rings - 1, False)):
            center = np.array(start) + d * self.length * i / (rings - 1)
            verts.append(center)
            uvs.append((0.5 / (color_angles + 1), (i / (rings - 1) * (color_rings - 1) + 0.5) / color_rings))
            for j in range(angles):
                tri = [i * (angles + 1) + j, i * (angles + 1) + j + 1, len(verts) - 1]
                faces.append(tri[::-1] if flip else tri)
                loop_uvs += [uvs[k] for k in faces[-1]]
        obj = scene.mesh_object(name, np.array(verts).tolist(), faces)
        layer = obj.data.uv_layers.new(name="UVMap")
        layer.data.foreach_set("uv", np.array(loop_uvs).ravel())
        image = bpy.data.images.new(f"{name}Colors", color_angles + 1, color_rings)
        pixels = np.ones((color_rings, color_angles + 1, 4))
        pixels[:, :, :3] = self.colors[:, np.arange(color_angles + 1) % color_angles]
        image.pixels.foreach_set(pixels.astype(np.float32).ravel())
        obj.data.materials.append(_texture_material(f"{name}Material", image))
        return obj


def _texture_material(name: str, image: bpy.types.Image) -> bpy.types.Material:
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    texture.interpolation = "Linear"
    texture.extension = "EXTEND"
    mat.node_tree.links.new(texture.outputs["Color"], nodes["Principled BSDF"].inputs["Base Color"])
    return mat


# --- Assembly -----------------------------------------------------------------------------------------------------


def _turn_onto(a: NDArray[np.float64], b: NDArray[np.float64]) -> NDArray[np.float64]:
    """The smallest rotation taking direction a onto direction b."""
    a, b = a / np.linalg.norm(a), b / np.linalg.norm(b)
    axis = np.cross(a, b)
    s, c = np.linalg.norm(axis), float(np.dot(a, b))
    if s < 1e-9:
        return np.eye(3)
    k = np.array([[0, -axis[2], axis[1]], [axis[2], 0, -axis[0]], [-axis[1], axis[0], 0]]) / s
    turn: NDArray[np.float64] = np.eye(3) + s * k + (1 - c) * k @ k
    return turn


def _transform(obj: bpy.types.Object, rotation: NDArray[np.float64], pivot: Vec3, target: Vec3, mirror: bool = False) -> None:
    """Rotates the mesh (game space) about `pivot` and moves the pivot to `target`; mirror flips X first."""
    turn = np.array(rotation) @ np.diag([-1.0 if mirror else 1.0, 1.0, 1.0])
    # Game to Blender axes and back around the game-space matrix.
    axes = np.array([[1, 0, 0], [0, 0, -1], [0, 1, 0]], dtype=np.float64)
    m = Matrix.Identity(4)
    m3 = axes @ turn @ axes.T
    for i in range(3):
        for j in range(3):
            m[i][j] = m3[i, j]
    obj.data.transform(Matrix.Translation(scene.to_blender(target)) @ m @ Matrix.Translation(-scene.to_blender(pivot)))
    if mirror:
        obj.data.flip_normals()
    obj.data.update()


def _at_height(centerline: list[Vec3], y: float) -> NDArray[np.float64]:
    curve = _resample(np.array(centerline), 0.001)
    point: NDArray[np.float64] = curve[int(np.argmin(np.abs(curve[:, 1] - y)))]
    return point


def _toe_turn(heel: tuple[float, float], toe: tuple[float, float]) -> float:
    """Degrees around Y that point a shoe's heel-to-toe line (x, z) to -Z."""
    # With the line |d| (sin b, cos b), turning by a around Y gives |d| (sin(a + b), cos(a + b)): -Z needs a + b = 180.
    b = math.degrees(math.atan2(toe[0] - heel[0], toe[1] - heel[1]))
    return (180.0 - b + 180.0) % 360.0 - 180.0


def _left_shoe(scan: Scan) -> bpy.types.Object:
    """The left shoe, cut free where the soles are fused, turned to point forward under the left hip."""
    centers = scan.face_centers()
    flat = centers * np.array([1.0, 0.0, 1.0])
    reach = {k: _segment_distance(flat, (a[0], 0.0, a[1]), (b[0], 0.0, b[1])) for k, (a, b) in SHOE_LINES.items()}
    split = (SHOE_SPLIT[0], tuple(-np.array(SHOE_SPLIT[1])))
    shoe = scan.piece("ShoeL", (centers[:, 1] < ANKLE + 0.01) & (reach["L"] < reach["R"]), [((0.0, ANKLE, 0.0), (0.0, -1.0, 0.0)), split])
    _transform(shoe, _yaw(_toe_turn(*SHOE_LINES["L"])), tuple(_at_height(LEGS["L"], ANKLE)), (-HIP_X, ANKLE, 0.0))
    return shoe


def _right_shoe(left: bpy.types.Object) -> bpy.types.Object:
    """The right toe is buried in the left heel on the scan, so the right shoe is the left one mirrored, its red and
    blue swapped as on the figurine's mismatched pair."""
    shoe = _copy(left)
    shoe.name = shoe.data.name = "ShoeR"
    _transform(shoe, np.eye(3), (0.0, 0.0, 0.0), (0.0, 0.0, 0.0), mirror=True)
    for slot in shoe.material_slots:
        slot.material = _swap_red_blue(slot.material)
    return shoe


def _swap_red_blue(source: bpy.types.Material) -> bpy.types.Material:
    """The material with its hues mirrored about the halfway point between the figurine's red and blue, so the two
    trade places and everything keeps its shading."""
    mat = source.copy()
    tree = mat.node_tree
    bsdf = tree.nodes["Principled BSDF"]
    feed = bsdf.inputs["Base Color"].links[0].from_socket
    split, join = tree.nodes.new("ShaderNodeSeparateColor"), tree.nodes.new("ShaderNodeCombineColor")
    split.mode = join.mode = "HSV"
    mirror, wrap = tree.nodes.new("ShaderNodeMath"), tree.nodes.new("ShaderNodeMath")
    mirror.operation, wrap.operation = "SUBTRACT", "FRACT"
    mirror.inputs[0].default_value = FIGURINE_BLUE_HUE
    tree.links.new(feed, split.inputs["Color"])
    tree.links.new(split.outputs["Red"], mirror.inputs[1])
    tree.links.new(mirror.outputs[0], wrap.inputs[0])
    tree.links.new(wrap.outputs[0], join.inputs["Red"])
    tree.links.new(split.outputs["Green"], join.inputs["Green"])
    tree.links.new(split.outputs["Blue"], join.inputs["Blue"])
    tree.links.new(join.outputs["Color"], bsdf.inputs["Base Color"])
    return mat


def legs_and_shoes(scan: Scan) -> list[bpy.types.Object]:
    left = _left_shoe(scan)
    parts = [left, _right_shoe(left)]
    down = np.array([0.0, -1.0, 0.0])
    for side, sign in (("R", 1.0), ("L", -1.0)):
        centerline = LEGS[side]
        hip = np.array([sign * HIP_X, centerline[0][1], 0.0])
        end = np.array([sign * HIP_X, centerline[-1][1], 0.0])
        tube = Tube.measure(scan, centerline)
        # Laid out straight down from the hip; the rings keep the turn they had at the ankle, plus the shoe's.
        normal = _yaw(_toe_turn(*SHOE_LINES[side])) @ _turn_onto(tube.end_tangent, down) @ tube.end_normal
        # The thigh's top, where the scan has the thighs fused, is an oval filling the hips' outline.
        a = np.linspace(0.0, 2.0 * math.pi, ANGLES, endpoint=False)
        side_way = normal[None, :] * np.cos(a)[:, None] + np.cross(down, normal)[None, :] * np.sin(a)[:, None]
        tube.widen_top(1.0 / np.hypot(side_way[:, 0] / HIP_RADII[0], side_way[:, 2] / HIP_RADII[1]))
        # Inside the hips it narrows again, so no rim of it shows past the hips' outline.
        heights = np.linspace(hip[1], end[1], len(tube.radii))
        tube.radii *= (1.0 - 0.35 * _smoothstep(HIPS, hip[1], heights))[:, None]
        tube.length = float(hip[1] - end[1])
        parts.append(tube.build(f"Leg{side}", tuple(hip), tuple(down), tuple(normal)))
    return parts


def arms(scan: Scan) -> list[bpy.types.Object]:
    skin = scan.color_around(SKIN_SPOT)
    parts = []
    length = UPPER_ARM + FOREARM
    elbow = UPPER_ARM / length
    for side, sign in (("R", 1.0), ("L", -1.0)):
        glove = scan.color_around(GLOVE_SPOTS[side])
        top = GLOVE_TOP / length
        tube = Tube.round(
            [(0.0, ARM_RADII[0]), (elbow, ARM_RADII[1]), (1.0, ARM_RADII[2])],
            [(0.0, skin), (top - 0.01, skin), (top, glove), (1.0, glove)],
            length,
        )
        arm = tube.build(f"Arm{side}", SHOULDERS[side], (sign, 0.0, 0.0), (0.0, 1.0, 0.0))
        # A rounded shoulder where the arm meets the torso, in the arm's skin color.
        bm = bmesh.new()
        bm.from_mesh(arm.data)
        sphere = bmesh.ops.create_uvsphere(bm, u_segments=24, v_segments=12, radius=1.0)["verts"]
        deltoid = np.array(SHOULDERS[side]) + np.array([sign * DELTOID[0][0], DELTOID[0][1], DELTOID[0][2]])
        bmesh.ops.transform(bm, matrix=Matrix.Translation(scene.to_blender(deltoid)) @ Matrix.Diagonal((*DELTOID[1], 1.0)), verts=sphere)
        uv = bm.loops.layers.uv.active
        for face in {f for v in sphere for f in v.link_faces}:
            for loop in face.loops:
                loop[uv].uv = (0.5 / (ANGLES + 1), 0.5 / len(tube.colors))
        bm.to_mesh(arm.data)
        bm.free()
        parts.append(arm)
    return parts


def hands(scan: Scan) -> list[bpy.types.Object]:
    """The glove generator's hand, cut at the wrist, scaled to the figurine and laid at the end of each arm, palm down."""
    skin = [float(c) for c in scan.color_around(SKIN_SPOT)]
    template = scene.bisect(hand.shape(), (-HAND_SOCKET / HAND_SCALE, 0.0, 0.0), (1.0, 0.0, 0.0))
    bm = bmesh.new()
    bm.from_mesh(template.data)
    bmesh.ops.holes_fill(bm, edges=bm.edges, sides=0)
    bm.to_mesh(template.data)
    bm.free()
    parts = []
    for side, sign in (("R", 1.0), ("L", -1.0)):
        obj = template.copy()
        obj.data = template.data.copy()
        obj.name = f"Hand{side}"
        scene.link(obj)
        wrist = np.array(SHOULDERS[side]) + np.array([sign * (UPPER_ARM + FOREARM), 0.0, 0.0])
        _transform(obj, np.eye(3) * HAND_SCALE, (0.0, 0.0, 0.0), tuple(wrist), mirror=sign < 0)
        obj.data.materials.append(scene.material(f"Hand{side}Skin", skin))
        parts.append(obj)
    bpy.data.objects.remove(template)
    return parts


def torso(scan: Scan) -> bpy.types.Object:
    piece = scan.piece("Torso", torso_faces(scan.face_centers()), [((0.0, NECK_TOP, 0.0), (0.0, -1.0, 0.0))], erode=3)
    _straighten_object(piece)
    return piece


# --- Fusing, texture and rig ----------------------------------------------------------------------------------------


def _copy(obj: bpy.types.Object) -> bpy.types.Object:
    clone = obj.copy()
    clone.data = obj.data.copy()
    return scene.link(clone)


def _settle(body: bpy.types.Object, sources: list[bpy.types.Object]) -> bpy.types.Object:
    """Puts the fused surface back on the parts it was fused from: the voxels leave faint terraces that, decimated,
    shade as blotches. Points further off than a voxel or two (the fill where parts meet) stay, and a light smoothing
    evens out the seam between the two."""
    shells = scene.join("Shells", *[_copy(obj) for obj in sources])
    tree = BVHTree.FromObject(shells, bpy.context.evaluated_depsgraph_get())
    count = len(body.data.vertices)
    co = np.empty(count * 3)
    body.data.vertices.foreach_get("co", co)
    points = co.reshape(-1, 3)
    for i, point in enumerate(points):
        hit, _normal, _index, distance = tree.find_nearest(Vector(point), VOXEL * 1.5)
        if hit is not None:
            points[i] = hit[:]
    body.data.vertices.foreach_set("co", points.ravel())
    body.data.update()
    bpy.data.objects.remove(shells)
    smooth = body.modifiers.new("Smooth", "CORRECTIVE_SMOOTH")
    smooth.iterations = 2
    smooth.use_only_smooth = True
    smooth.smooth_type = "LENGTH_WEIGHTED"
    return scene.bake(body)


def _unwrap(obj: bpy.types.Object) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(60.0), island_margin=0.002)
    bpy.ops.object.mode_set(mode="OBJECT")


def _bake(target: bpy.types.Object, sources: list[bpy.types.Object]) -> bpy.types.Image:
    """Bakes the sources' colors (scan texture, cap colors, tube colors, skin) onto the target's own unwrap."""
    image = bpy.data.images.new("FigureColor", TEXTURE, TEXTURE)
    mat = bpy.data.materials.new("figure")
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    mat.node_tree.links.new(texture.outputs["Color"], nodes["Principled BSDF"].inputs["Base Color"])
    nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.6
    nodes.active = texture
    scene.finish(target, mat)
    render = bpy.context.scene.render
    render.engine = "CYCLES"
    # Base color only: no light reaches the bake, so a few samples smooth the texel edges.
    samples = bpy.context.scene.cycles.samples
    bpy.context.scene.cycles.samples = 4
    render.bake.use_selected_to_active = True
    render.bake.cage_extrusion = VOXEL * 3.0
    render.bake.max_ray_distance = VOXEL * 12.0
    render.bake.margin = 8
    bpy.ops.object.select_all(action="DESELECT")
    for obj in sources:
        obj.select_set(True)
    target.select_set(True)
    bpy.context.view_layer.objects.active = target
    bpy.ops.object.bake(type="DIFFUSE", pass_filter={"COLOR"})
    bpy.context.scene.cycles.samples = samples
    image.pack()
    return image


def _bones() -> list[tuple[str, Vec3, Vec3, str | None]]:
    """The rig in the scan's units, rest pose."""
    hip, waist, chest, neck, crown = SPINE_JOINTS
    # Head carries no part of this model: it marks where the game's head sits on the neck.
    bones: list[tuple[str, Vec3, Vec3, str | None]] = [
        ("Torso", (0.0, hip, 0.0), (0.0, waist, 0.0), None),
        ("Chest", (0.0, waist, 0.0), (0.0, chest, 0.0), "Torso"),
        ("Neck", (0.0, chest, 0.0), (0.0, neck, 0.0), "Chest"),
        ("Head", (0.0, neck, 0.0), (0.0, crown, 0.0), "Neck"),
    ]
    for side, sign in (("R", 1.0), ("L", -1.0)):
        shoulder = np.array(SHOULDERS[side])
        elbow = shoulder + np.array([sign * UPPER_ARM, 0.0, 0.0])
        wrist = elbow + np.array([sign * FOREARM, 0.0, 0.0])
        bones += [
            (f"UpperArm{side}", tuple(shoulder), tuple(elbow), "Chest"),
            (f"Forearm{side}", tuple(elbow), tuple(wrist), f"UpperArm{side}"),
        ]
        for name, head, tail, parent in hand.bones():
            # The left hand is the glove hand mirrored across X, as in hands().
            start, end = (tuple(wrist + np.array([sign * p[0], p[1], p[2]]) * HAND_SCALE) for p in (head, tail))
            bones.append((f"{name}{side}", start, end, f"{parent}{side}" if parent else f"Forearm{side}"))
        leg_top = np.array([sign * HIP_X, hip, 0.0])
        knee = np.array([sign * HIP_X, KNEE, 0.0])
        ankle = np.array([sign * HIP_X, ANKLE, 0.0])
        bones += [
            (f"Thigh{side}", tuple(leg_top), tuple(knee), "Torso"),
            (f"Shin{side}", tuple(knee), tuple(ankle), f"Thigh{side}"),
            (f"Foot{side}", tuple(ankle), (sign * HIP_X + TOE[0], TOE[1], TOE[2]), f"Shin{side}"),
        ]
    return bones


# Skinning: blend width (scan units) either side of each joint.
BLEND = 0.012


def _ramp(x: NDArray[np.float64], at: float, width: float = BLEND) -> NDArray[np.float64]:
    """0 well below `at`, 1 well above, smooth across `width` either side."""
    return _smoothstep(at - width, at + width, x)


def _chain(t: NDArray[np.float64], joints: list[float]) -> list[NDArray[np.float64]]:
    """Weights of consecutive bones along a chain: bone i owns t between joints[i] and joints[i + 1], blended at each."""
    steps = [_ramp(t, j) for j in joints[1:-1]]
    weights = []
    for i in range(len(joints) - 1):
        w = np.ones_like(t)
        if i > 0:
            w = w * steps[i - 1]
        if i < len(steps):
            w = w * (1.0 - steps[i])
        weights.append(w)
    return weights


def _skin(body: bpy.types.Object, rig: bpy.types.Object, body_vertices: int, scale: float) -> None:
    """Weights by rule, in the rest T-pose: the spine by height, arms along their length past the shoulder, legs by
    height below the hips, and each hand shell between its nearest hand and finger bones."""
    count = len(body.data.vertices)
    co = np.empty(count * 3)
    body.data.vertices.foreach_get("co", co)
    b = co.reshape(-1, 3) / scale
    p = np.column_stack([b[:, 0], b[:, 2], -b[:, 1]])
    x, y, z = p[:, 0], p[:, 1], p[:, 2]
    weights: dict[str, NDArray[np.float64]] = {}

    def add(bone: str, w: NDArray[np.float64]) -> None:
        weights[bone] = weights.get(bone, np.zeros(count)) + w

    body_part = np.arange(count) < body_vertices
    hip, waist, chest, _neck, _crown = SPINE_JOINTS
    side_of = np.where(x > 0.0, "R", "L")
    reach = np.abs(x)
    shoulder_x = abs(SHOULDERS["R"][0])
    arm = body_part & (reach > shoulder_x - BLEND) & (np.abs(y - SHOULDERS["R"][1]) < ARM_RADII[0] * 1.6) & (np.abs(z) < ARM_RADII[0] * 1.6)
    leg = body_part & ~arm & (y < hip + BLEND)
    trunk = body_part & ~arm & ~leg
    spine = _chain(y, [-1.0, waist, chest, 2.0])
    for bone, w in zip(("Torso", "Chest", "Neck"), spine, strict=True):
        add(bone, trunk * w)
    # Arms: chest at the shoulder, then upper arm, forearm and hand along the arm.
    for side in ("R", "L"):
        mine = arm & (side_of == side)
        t = reach - shoulder_x
        parts = _chain(t, [-1.0, 0.004, UPPER_ARM, UPPER_ARM + FOREARM, 1.0])
        for bone, w in zip(("Chest", f"UpperArm{side}", f"Forearm{side}", f"Hand{side}"), parts, strict=True):
            add(bone, mine * w)
        # Legs: torso at the hip, then thigh, shin and foot down the leg.
        mine = leg & (side_of == side)
        parts = _chain(-y, [-2.0, -hip, -KNEE, -ANKLE, 1.0])
        for bone, w in zip(("Torso", f"Thigh{side}", f"Shin{side}", f"Foot{side}"), parts, strict=True):
            add(bone, mine * w)
    # Hands: shared between the two nearest of their own bones, by distance to each bone.
    segments = {name: (head, tail) for name, head, tail, _parent in _bones()}
    for side in ("R", "L"):
        names = [f"{name}{side}" for name, *_ in hand.bones()]
        mine = ~body_part & (side_of == side)
        distance = np.stack([_segment_distance(p, *segments[name]) for name in names], axis=1)
        nearest = np.argsort(distance, axis=1)[:, :2]
        d = np.take_along_axis(distance, nearest, axis=1) + 1e-5
        share = (1.0 / d**4) / np.sum(1.0 / d**4, axis=1, keepdims=True)
        for k, name in enumerate(names):
            add(name, mine * np.sum(share * (nearest == k), axis=1))
    total = sum(weights.values())
    for bone, w in weights.items():
        group = body.vertex_groups.get(bone) or body.vertex_groups.new(name=bone)
        w = w / np.maximum(total, 1e-9)
        for index in np.nonzero(w > 1e-3)[0]:
            group.add([int(index)], float(w[index]), "REPLACE")
    body.parent = rig
    body.modifiers.new("Armature", "ARMATURE").object = rig


def build() -> bpy.types.Object:
    """Builds the rigged figure in the scene and returns its armature."""
    scan = Scan()
    sources = [torso(scan), *legs_and_shoes(scan), *arms(scan)]
    hand_sources = hands(scan)
    scan.bm.free()
    bpy.data.objects.remove(scan.obj)

    body = scene.join("ScanDoctor", *[_copy(obj) for obj in sources])
    body.data.materials.clear()
    body = _settle(scene.remesh(body, VOXEL), sources)
    body = scene.fit(body, BUDGET)
    fitted_hands = [scene.fit(_copy(obj), HAND_BUDGET) for obj in hand_sources]
    for obj in [body, *fitted_hands]:
        obj.data.materials.clear()
        obj.data.validate()
    body_vertices = len(body.data.vertices)
    body = scene.join("ScanDoctor", body, *fitted_hands)
    _unwrap(body)
    _bake(body, sources + hand_sources)
    for obj in sources + hand_sources:
        bpy.data.objects.remove(obj)

    scale = HIP_HEIGHT / SPINE_JOINTS[0]
    body.data.transform(Matrix.Scale(scale, 4))
    bones = [(name, tuple(np.array(head) * scale), tuple(np.array(tail) * scale), parent) for name, head, tail, parent in _bones()]
    rig = scene.armature("ScanDoctorRig", bones)
    _skin(body, rig, body_vertices, scale)
    bpy.data.orphans_purge(do_recursive=True)
    return rig


def test_pose(rig: bpy.types.Object) -> None:
    """Review pose: arms down at the sides, a step forward, the head turned, a pointing finger."""
    scene.pose(
        rig,
        {
            "UpperArmR": (-65.0, 0.0, 0.0),
            "UpperArmL": (-65.0, 0.0, 0.0),
            "ForearmR": (0.0, 0.0, 40.0),
            "ThighL": (30.0, 0.0, 0.0),
            "ShinL": (-50.0, 0.0, 0.0),
            "Head": (0.0, 25.0, 0.0),
            "Middle1R": (-70.0, 0.0, 0.0),
            "Ring1R": (-70.0, 0.0, 0.0),
            "Pinky1R": (-70.0, 0.0, 0.0),
        },
    )
