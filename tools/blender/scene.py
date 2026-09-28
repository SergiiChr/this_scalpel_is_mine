"""Blender helpers: organic mesh building, materials, rigging, glTF export and review renders.

Everything is authored in game space (Y up, patient's left is +Z) and converted to Blender's Z up space here,
so numbers can be compared with the game code directly.
"""

from __future__ import annotations

import math
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from numpy.typing import NDArray

Vec3 = tuple[float, float, float]


def to_blender(p: Sequence[float]) -> Vector:
    """Game (x, y up, z) to Blender (x, -z, y up). The glTF exporter maps it back."""
    return Vector((p[0], -p[2], p[1]))


def reset() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)


def link(obj: bpy.types.Object) -> bpy.types.Object:
    bpy.context.scene.collection.objects.link(obj)
    return obj


def material(name: str, color: Sequence[float], roughness: float = 0.5, subsurface: float = 0.0, metallic: float = 0.0) -> bpy.types.Material:
    """Principled material. The game only reads the name (see model_slot.gd) and base color; the rest is for review renders."""
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    if mat.node_tree is None:
        mat.use_nodes = True
    bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*color[:3], 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic
    if subsurface > 0.0:
        bsdf.inputs["Subsurface Weight"].default_value = subsurface
        bsdf.inputs["Subsurface Radius"].default_value = (0.012, 0.004, 0.002)
        bsdf.inputs["Subsurface Scale"].default_value = 0.5
    mat.diffuse_color = (*color[:3], 1.0)
    return mat


def mesh_object(name: str, verts: Sequence[Sequence[float]], faces: Sequence[Sequence[int]], edges: Sequence[tuple[int, int]] = ()) -> bpy.types.Object:
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([to_blender(v) for v in verts], list(edges), [list(f) for f in faces])
    mesh.update()
    return link(bpy.data.objects.new(name, mesh))


def bake(obj: bpy.types.Object) -> bpy.types.Object:
    """Applies every modifier (or converts a metaball/curve) into a plain mesh object with the same name."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    mesh = bpy.data.meshes.new_from_object(obj.evaluated_get(depsgraph))
    name = obj.name
    data = obj.data
    bpy.data.objects.remove(obj)
    if isinstance(data, bpy.types.Mesh) and data.users == 0:
        bpy.data.meshes.remove(data)
    result = link(bpy.data.objects.new(name, mesh))
    return result


def skin(
    name: str, points: Sequence[Vec3], edges: Sequence[tuple[int, int]], radii: Sequence[float | tuple[float, float]], subdivisions: int = 2
) -> bpy.types.Object:
    """Tube network from a skeleton (Skin modifier), smoothed by subdivision. Radii are per point, round or (a, b)."""
    obj = mesh_object(name, points, [], edges)
    obj.modifiers.new("Skin", "SKIN")
    data = obj.data.skin_vertices[0].data
    for i, r in enumerate(radii):
        data[i].radius = (r, r) if isinstance(r, float | int) else r
    data[0].use_root = True
    sub = obj.modifiers.new("Subsurf", "SUBSURF")
    sub.levels = sub.render_levels = subdivisions
    return bake(obj)


@dataclass(frozen=True)
class Blob:
    """One metaball element in game space.
    An ellipsoid uses `size` as half extents; a capsule runs from `center` to `end` with radius size[0].
    Sizes are what a lone element would measure; blended neighbours swell a little. Negative stiffness carves."""

    center: Vec3
    size: Vec3
    stiffness: float = 2.0
    end: Vec3 | None = None


def ellipsoid(center: Vec3, size: Vec3, stiffness: float = 2.0) -> Blob:
    return Blob(center, size, stiffness)


def capsule(a: Vec3, b: Vec3, radius: float, stiffness: float = 2.0) -> Blob:
    return Blob(a, (radius, radius, radius), stiffness, b)


def blobs(name: str, elements: Sequence[Blob], resolution: float = 0.004, threshold: float = 0.6) -> bpy.types.Object:
    """Metaball elements melted into one smooth mesh.
    Only elements in the same call blend; separate calls stay separate shells until remesh() fuses them."""
    # Blender won't polygonize metaballs finer than 5 mm, so small shapes are built scaled up and shrunk afterwards.
    k = max(1.0, 0.005 / resolution)
    ball = bpy.data.metaballs.new(name)
    ball.resolution = ball.render_resolution = resolution * k
    ball.threshold = threshold
    for blob in elements:
        # Blender's field is stiffness * (1 - (r / size)^2)^3, so a lone element's surface sits at this fraction of its size.
        reach = math.sqrt(1.0 - (threshold / abs(blob.stiffness)) ** (1.0 / 3.0))
        if blob.end is None:
            el = ball.elements.new(type="ELLIPSOID")
            el.co = to_blender(blob.center) * k
            el.size_x, el.size_y, el.size_z = blob.size[0] / reach * k, blob.size[2] / reach * k, blob.size[1] / reach * k
        else:
            a, b = to_blender(blob.center) * k, to_blender(blob.end) * k
            el = ball.elements.new(type="CAPSULE")
            el.co = (a + b) * 0.5
            el.size_x = (b - a).length * 0.5
            el.rotation = (b - a).to_track_quat("X", "Z")
        el.radius = blob.size[0] / reach * k if blob.end is not None else 1.0
        el.stiffness = abs(blob.stiffness)
        el.use_negative = blob.stiffness < 0.0
    obj = bake(link(bpy.data.objects.new(name, ball)))
    obj.data.transform(Matrix.Scale(1.0 / k, 4))
    return obj


def cut_below(obj: bpy.types.Object, axis: int, limit: float, above: bool = False) -> bpy.types.Object:
    """Deletes the part of the mesh below (or above) limit along a game axis, leaving an open edge (a glove opening, a severed tube)."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    game = [lambda v: v.co.x, lambda v: v.co.z, lambda v: -v.co.y][axis]
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if (game(v) > limit) == above and game(v) != limit], context="VERTS")
    bm.to_mesh(obj.data)
    bm.free()
    return obj


def rim(name: str, obj: bpy.types.Object, radius: float) -> bpy.types.Object:
    """Rolled edge following the open boundary of a mesh (glove opening, sleeve cuff)."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    edge = [v.co.copy() for v in bm.verts if v.is_boundary]
    bm.free()
    center = sum(edge, Vector()) / len(edge)
    # Order the loop by angle around its center in the plane of the opening.
    normal = (edge[0] - center).cross(edge[len(edge) // 3] - center).normalized()
    side = (edge[0] - center).normalized()
    up = normal.cross(side)
    edge.sort(key=lambda p: math.atan2((p - center).dot(up), (p - center).dot(side)))
    step = max(1, len(edge) // 24)
    loop = [center + (p - center) * 1.02 for p in edge[::step]]
    game = [(p.x, p.z, -p.y) for p in loop]
    return curve_tube(name, [*game, *game[:3]], radius)


def curve_tube(name: str, points: Sequence[Vec3], radius: float, radii: Sequence[float] | None = None, resolution: int = 12) -> bpy.types.Object:
    """Smooth tube along a NURBS path through the points, with optional per-point radius scale."""
    curve = bpy.data.curves.new(name, "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = radius
    curve.bevel_resolution = resolution // 2
    curve.resolution_u = resolution
    curve.use_fill_caps = True
    spline = curve.splines.new("NURBS")
    spline.points.add(len(points) - 1)
    for i, p in enumerate(points):
        spline.points[i].co = (*to_blender(p), 1.0)
        spline.points[i].radius = radii[i] if radii else 1.0
    spline.use_endpoint_u = True
    spline.order_u = 4
    return bake(link(bpy.data.objects.new(name, curve)))


def join(name: str, *objects: bpy.types.Object) -> bpy.types.Object:
    """Joins meshes into one object (keeps separate shells; use remesh() to fuse them)."""
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    objects[0].name = name
    return objects[0]


def remesh(obj: bpy.types.Object, voxel: float, smooth: int = 0) -> bpy.types.Object:
    """Voxel remesh fuses overlapping shells into one clean closed surface (like DynTopo sculpt blending)."""
    mod = obj.modifiers.new("Remesh", "REMESH")
    mod.mode = "VOXEL"
    mod.voxel_size = voxel
    mod.adaptivity = 0.0
    if smooth:
        sm = obj.modifiers.new("Smooth", "CORRECTIVE_SMOOTH")
        sm.iterations = smooth
        sm.use_only_smooth = True
        sm.smooth_type = "LENGTH_WEIGHTED"
    return bake(obj)


def subtract(obj: bpy.types.Object, cutter: bpy.types.Object) -> bpy.types.Object:
    """Boolean difference; the cutter is consumed."""
    mod = obj.modifiers.new("Subtract", "BOOLEAN")
    mod.operation = "DIFFERENCE"
    mod.solver = "EXACT"
    mod.object = cutter
    cutter.hide_render = True
    result = bake(obj)
    bpy.data.objects.remove(cutter)
    return result


def bisect(obj: bpy.types.Object, point: Vec3, normal: Vec3) -> bpy.types.Object:
    """Cuts the mesh with a plane (game space) and keeps the side the normal points to, with a clean edge."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
    bmesh.ops.bisect_plane(bm, geom=geom, plane_co=to_blender(point), plane_no=to_blender(normal), clear_inner=True)
    bm.to_mesh(obj.data)
    bm.free()
    return obj


def on_surface(target: bpy.types.Object, points: Sequence[Vec3], lift: float, samples: int = 4) -> list[Vec3]:
    """Snaps a path onto a mesh surface, lifted along the normal (vessels lying on an organ).
    The path is resampled first so it follows the surface between the given points."""
    from mathutils.bvhtree import BVHTree

    tree = BVHTree.FromObject(target, bpy.context.evaluated_depsgraph_get())
    dense = []
    for a, b in zip(points, points[1:], strict=False):
        dense += [to_blender(np.array(a) + (np.array(b) - np.array(a)) * t) for t in np.linspace(0.0, 1.0, samples, endpoint=False)]
    dense.append(to_blender(points[-1]))
    out: list[Vec3] = []
    for p in dense:
        hit, normal, _index, _dist = tree.find_nearest(p)
        q = hit + normal * lift
        out.append((q.x, q.z, -q.y))
    return out


def decimate(obj: bpy.types.Object, ratio: float) -> bpy.types.Object:
    mod = obj.modifiers.new("Decimate", "DECIMATE")
    mod.ratio = ratio
    return bake(obj)


def displace(obj: bpy.types.Object, strength: float, scale: float, detail: int = 2, seed: int = 0) -> bpy.types.Object:
    """Organic lumpiness from a cloud noise texture along the normals."""
    tex = bpy.data.textures.new(f"{obj.name}_noise", "CLOUDS")
    tex.noise_scale = scale
    tex.noise_depth = detail
    mod = obj.modifiers.new("Displace", "DISPLACE")
    mod.texture = tex
    mod.strength = strength
    mod.mid_level = 0.5
    obj.location = Vector((seed * 13.7, seed * 7.1, seed * 3.3))
    result = bake(obj)
    result.location = Vector((0.0, 0.0, 0.0))
    return result


def deform(obj: bpy.types.Object, fn: Callable[[NDArray[np.float64]], NDArray[np.float64]]) -> bpy.types.Object:
    """Moves every vertex with fn(points) in game space, like a scripted sculpt stroke."""
    mesh = obj.data
    co = np.empty(len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", co)
    blender = co.reshape(-1, 3)
    game = np.column_stack([blender[:, 0], blender[:, 2], -blender[:, 1]])
    game = fn(game)
    blender = np.column_stack([game[:, 0], -game[:, 2], game[:, 1]])
    mesh.vertices.foreach_set("co", blender.ravel())
    mesh.update()
    return obj


def finish(obj: bpy.types.Object, mat: bpy.types.Material, smooth: bool = True) -> bpy.types.Object:
    obj.data.materials.clear()
    obj.data.materials.append(mat)
    for poly in obj.data.polygons:
        poly.use_smooth = smooth
    return obj


def armature(name: str, bones: Sequence[tuple[str, Vec3, Vec3, str | None]]) -> bpy.types.Object:
    """Armature from (bone, head, tail, parent) in game space. Bones keep their names as node names in the glTF."""
    data = bpy.data.armatures.new(name)
    rig = link(bpy.data.objects.new(name, data))
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    for bone, head, tail, parent in bones:
        eb = data.edit_bones.new(bone)
        eb.head = to_blender(head)
        eb.tail = to_blender(tail)
        # Local Z points up (game +Y), so bending a finger toward the palm is a rotation around local X.
        eb.align_roll(Vector((0.0, 0.0, 1.0)))
        if parent:
            eb.parent = data.edit_bones[parent]
    bpy.ops.object.mode_set(mode="OBJECT")
    return rig


def bind(mesh: bpy.types.Object, rig: bpy.types.Object) -> None:
    """Skins the mesh to the rig with Blender's bone heat weights."""
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")


def attach(obj: bpy.types.Object, rig: bpy.types.Object, bone: str) -> None:
    """Rigid attachment (eyes, nails): parented straight to a bone."""
    world = obj.matrix_world.copy()
    obj.parent = rig
    obj.parent_type = "BONE"
    obj.parent_bone = bone
    obj.matrix_world = world


def pose(rig: bpy.types.Object, rotations: dict[str, Vec3]) -> None:
    """Rotates pose bones (degrees, around the bone's local X, Y, Z) for review renders."""
    for bone, (x, y, z) in rotations.items():
        pb = rig.pose.bones[bone]
        pb.rotation_mode = "XYZ"
        pb.rotation_euler = (math.radians(x), math.radians(y), math.radians(z))


def export(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=str(path), export_format="GLB", export_apply=True, export_yup=True, export_animations=False)


def triangle_count() -> int:
    total = 0
    for obj in bpy.context.scene.objects:
        if obj.type == "MESH":
            total += sum(len(p.vertices) - 2 for p in obj.data.polygons)
    return total


# --- Review renders ---------------------------------------------------------------------------------


def _look_at(obj: bpy.types.Object, target: Vector, up: Vector) -> None:
    """Points the camera at target with `up` (Blender space) toward the top of the image."""
    forward = (target - obj.location).normalized()
    if abs(forward.dot(up.normalized())) > 0.99:
        up = Vector((0.0, 1.0, 0.0))
    right = forward.cross(up).normalized()
    cam_up = right.cross(forward)
    obj.matrix_world = Matrix.Translation(obj.location) @ Matrix((right, cam_up, -forward)).transposed().to_4x4()


def setup_render(resolution: int = 640, samples: int = 48) -> None:
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.render.resolution_x = scene.render.resolution_y = resolution
    scene.render.film_transparent = False
    # Standard keeps base colors true, so reviews show the colors the game will use.
    scene.view_settings.view_transform = "Standard"
    world = bpy.data.worlds.new("World")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.16, 0.17, 0.18, 1.0)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.35
    scene.world = world
    for name, energy, direction in (("Key", 2.4, (-0.5, -0.7, -1.0)), ("Fill", 0.6, (0.8, -0.3, -0.4)), ("Rim", 1.4, (0.2, 1.0, -0.3))):
        light = bpy.data.lights.new(name, "SUN")
        light.energy = energy
        light.angle = math.radians(12)
        obj = link(bpy.data.objects.new(name, light))
        obj.rotation_euler = Vector(direction).to_track_quat("-Z", "Y").to_euler()


def _bounds() -> tuple[Vector, float]:
    """Center and radius of every visible mesh, as posed."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    points = []
    for obj in bpy.context.scene.objects:
        if obj.type == "MESH":
            evaluated = obj.evaluated_get(depsgraph)
            points += [evaluated.matrix_world @ v.co for v in evaluated.data.vertices]
    lo = Vector((min(p.x for p in points), min(p.y for p in points), min(p.z for p in points)))
    hi = Vector((max(p.x for p in points), max(p.y for p in points), max(p.z for p in points)))
    return (lo + hi) * 0.5, (hi - lo).length * 0.5


def render_views(
    out_dir: Path,
    name: str,
    views: Sequence[tuple[str, Vec3]],
    zoom: float = 1.0,
    lens: float = 60.0,
    focus: tuple[Vec3, float] | None = None,
    up: Vec3 = (0.0, 1.0, 0.0),
) -> list[Path]:
    """Renders one image per (label, direction to the camera) view, in game space.
    Frames the whole model, or focus = (center, radius) for a close-up. zoom > 1 moves in closer."""
    scene = bpy.context.scene
    cam_data = bpy.data.cameras.new("ReviewCamera")
    cam_data.lens = lens
    cam_data.clip_start = 0.002
    cam = link(bpy.data.objects.new("ReviewCamera", cam_data))
    scene.camera = cam
    target, radius = _bounds() if focus is None else (to_blender(focus[0]), focus[1])
    distance = radius / math.sin(cam_data.angle * 0.5) * 1.05 / zoom
    paths = []
    for label, direction in views:
        cam.location = target + to_blender(direction).normalized() * distance
        _look_at(cam, target, to_blender(up))
        path = out_dir / f"{name}_{label}.png"
        scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True)
        paths.append(path)
    bpy.data.objects.remove(cam)
    return paths


def contact_sheet(images: Sequence[Path], out: Path, title: str) -> Path:
    """Puts the views side by side in a 2-wide grid with a title bar."""
    from PIL import Image, ImageDraw

    tiles = [Image.open(p) for p in images]
    w, h = tiles[0].size
    cols = 2 if len(tiles) > 1 else 1
    rows = math.ceil(len(tiles) / cols)
    sheet = Image.new("RGB", (w * cols, h * rows + 40), (24, 24, 26))
    draw = ImageDraw.Draw(sheet)
    draw.text((12, 12), title, fill=(230, 230, 230))
    for i, (tile, path) in enumerate(zip(tiles, images, strict=True)):
        x, y = (i % cols) * w, 40 + (i // cols) * h
        sheet.paste(tile.convert("RGB"), (x, y))
        draw.text((x + 10, y + 8), path.stem.replace("_", " "), fill=(200, 200, 200))
    sheet.save(out)
    return out
