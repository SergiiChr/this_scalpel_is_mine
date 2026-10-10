"""The IV bag (model "iv_bag" in data/tools.cfg): a soft film bag with a printed label, two ports and a hanger flange.

From the IV bag asset pack (build_iv_bag.py), scaled to the tool's length: hanger hole at the origin, ports at the tip,
print on +Y. trimesh can't write skins or blend shapes, so this writes its own glTF.
Bag, Label and Frame are skinned to a seven-bone rig (Anchor > Neck > Upper > Middle > Lower, with LeftCorner and
RightCorner under Lower), and Bag and Label carry the EmptyBag and RestingFlat blend shapes.
Level is the liquid, left unskinned with its origin at its bottom, so the game can stretch it by how full the bag is.
"""

from __future__ import annotations

import io
import json
import math
import struct
from collections.abc import Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np
from numpy.typing import NDArray
from PIL import Image, ImageDraw, ImageFont
from shapely.geometry import Polygon
from shapely.ops import triangulate

from .export import MODELS_DIR
from .instruments import tool_lengths
from .palette import PALETTE, to_linear

# The pack's own frame: meters, Z up from the bag's foot, X across, the print toward -Y. TOP is the flange's top edge.
TOP = 0.265
BONES = ["Anchor", "Neck", "Upper", "Middle", "Lower", "LeftCorner", "RightCorner"]
BONE_AT = [(0.0, 0.0, 0.255), (0.0, 0.0, 0.225), (0.0, 0.0, 0.179), (0.0, 0.0, 0.123), (0.0, 0.0, 0.068), (-0.046, 0.0, 0.068), (0.046, 0.0, 0.068)]
BONE_PARENT = [-1, 0, 1, 2, 3, 4, 4]
MORPHS = ["EmptyBag", "RestingFlat"]
FONT_DIR = Path("/usr/share/fonts/truetype/dejavu")

Array = NDArray[np.float64]


@dataclass
class Mesh:
    material: str
    pos: list[Sequence[float]] = field(default_factory=list)
    uv: list[tuple[float, float]] = field(default_factory=list)
    idx: list[int] = field(default_factory=list)

    def v(self, xyz: Sequence[float], uv: tuple[float, float] = (0.0, 0.0)) -> int:
        self.pos.append(xyz)
        self.uv.append(uv)
        return len(self.pos) - 1

    def tri(self, a: int, b: int, c: int) -> None:
        self.idx.extend([a, b, c])

    def quad(self, a: int, b: int, c: int, d: int) -> None:
        self.tri(a, b, c)
        self.tri(a, c, d)

    def extend(self, other: Mesh) -> None:
        offset = len(self.pos)
        self.pos += other.pos
        self.uv += other.uv
        self.idx += [i + offset for i in other.idx]


def bone_weights(x: float, z: float) -> tuple[list[int], list[float]]:
    """Soft falloff down the spine, the lower corners pulled by their own bones. Four influences at most."""
    centers = np.array([0.255, 0.226, 0.182, 0.124, 0.061])
    sigmas = np.array([0.010, 0.021, 0.029, 0.032, 0.040])
    raw = np.exp(-0.5 * ((z - centers) / sigmas) ** 2)
    if z > 0.246:
        raw[0] *= 3
    weights = {i: float(w) for i, w in enumerate(raw)}
    if z < 0.115:
        corner = max(0.0, min(0.33, (0.115 - z) / 0.065 * 0.32)) * max(0.0, (abs(x) - 0.014) / 0.044)
        if corner > 0:
            weights[5 if x < 0 else 6] = corner
    chosen = sorted(weights.items(), key=lambda kv: kv[1], reverse=True)[:4]
    total = sum(w for _, w in chosen)
    return [i for i, _ in chosen], [w / total for _, w in chosen]


def half_width(z: float) -> float:
    return float(
        np.interp(z, [0.032, 0.040, 0.048, 0.060, 0.080, 0.180, 0.213, 0.228, 0.237], [0.026, 0.044, 0.055, 0.062, 0.0645, 0.065, 0.063, 0.054, 0.046])
    )


def face_depth(x: float, z: float, front: bool, inset: float = 0.0) -> float:
    """How far the filled film bulges out of the middle plane, thinning to the sealed edges, slightly wrinkled up top."""
    u = min(1.0, abs(x) / max(half_width(z), 0.001))
    puff = 0.0032 + 0.0101 * math.sin(math.pi * min(1.0, max(0.0, (z - 0.032) / 0.209))) ** 0.6
    edge = max(0.0, 1 - u * u) ** 0.67
    wrinkle = 0.0006 * math.sin(190 * z + 18 * u) * max(0.0, (z - 0.18) / 0.06) * edge
    depth = 0.002 + max(0.0001, puff * edge + wrinkle)
    return float((-1 if front else 1) * max(0.0009, depth - inset))


def panel(material: str, bottom: float, top: float, rings: int, cols: int, width_scale: float = 1.0, inset: float = 0.0) -> Mesh:
    """A closed pillow: front and back grids following the bag's outline, stitched at the sides, top and bottom."""
    m = Mesh(material)
    faces = []
    for front in (True, False):
        grid = []
        for j in range(rings + 1):
            z = bottom + (top - bottom) * j / rings
            row = []
            for i in range(cols + 1):
                u = 2 * i / cols - 1
                x = u * half_width(z) * width_scale
                row.append(m.v((x, face_depth(x / width_scale, z, front, inset), z), ((u + 1) / 2, j / rings)))
            grid.append(row)
        for j in range(rings):
            for i in range(cols):
                a, b, c, d = grid[j][i], grid[j][i + 1], grid[j + 1][i + 1], grid[j + 1][i]
                if front:
                    m.quad(a, b, c, d)
                else:
                    m.quad(d, c, b, a)
        faces.append(grid)
    front_grid, back_grid = faces
    for j in range(rings):
        m.quad(front_grid[j][0], front_grid[j + 1][0], back_grid[j + 1][0], back_grid[j][0])
        m.quad(front_grid[j][-1], back_grid[j][-1], back_grid[j + 1][-1], front_grid[j + 1][-1])
    for i in range(cols):
        m.quad(back_grid[0][i], front_grid[0][i], front_grid[0][i + 1], back_grid[0][i + 1])
        m.quad(front_grid[-1][i], back_grid[-1][i], back_grid[-1][i + 1], front_grid[-1][i + 1])
    return m


def print_decal() -> Mesh:
    """The label print, just off the front film."""
    m = Mesh("print")
    bottom, top = 0.079, 0.209
    grid = []
    for j in range(17):
        z = bottom + (top - bottom) * j / 16
        grid.append([m.v(((i / 12 - 0.5) * 0.104, face_depth((i / 12 - 0.5) * 0.104, z, True) - 0.00045, z), (i / 12, 1 - j / 16)) for i in range(13)])
    for j in range(16):
        for i in range(12):
            m.quad(grid[j][i], grid[j][i + 1], grid[j + 1][i + 1], grid[j + 1][i])
    return m


def flange() -> Mesh:
    """The sealed hanger tab with its hole."""
    m = Mesh("clear_plastic")
    outer = [
        (-0.049, 0.22),
        (-0.049, 0.232),
        (-0.038, 0.242),
        (-0.037, 0.257),
        (-0.03, 0.265),
        (0.03, 0.265),
        (0.037, 0.257),
        (0.038, 0.242),
        (0.049, 0.232),
        (0.049, 0.22),
    ]
    hole = [(0.007 * math.cos(2 * math.pi * i / 24), 0.252 + 0.006 * math.sin(2 * math.pi * i / 24)) for i in range(24)][::-1]
    shape = Polygon(outer, [hole])
    triangles = [list(t.exterior.coords)[:3] for t in triangulate(shape) if shape.covers(t.representative_point())]
    for side in (-1, 1):
        ids: dict[tuple[float, float], int] = {}
        for triangle in triangles:
            for x, z in triangle:
                if (x, z) not in ids:
                    ids[x, z] = m.v((x, side * 0.0024, z))
            a, b, c = (ids[corner] for corner in triangle)
            if side == -1:
                m.tri(a, b, c)
            else:
                m.tri(c, b, a)
    for ring in (outer, hole):
        for (ax, az), (bx, bz) in zip(ring, ring[1:] + ring[:1], strict=True):
            m.quad(m.v((ax, -0.0024, az)), m.v((bx, -0.0024, bz)), m.v((bx, 0.0024, bz)), m.v((ax, 0.0024, az)))
    return m


def tube(points: list[tuple[float, float, float]], radius: float, sides: int = 5) -> Mesh:
    """An open tube through the points: a heat seal ridge or a crease in the film."""
    m = Mesh("clear_plastic")
    pts = np.asarray(points)
    rings = []
    for i, point in enumerate(pts):
        direction = pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]
        direction /= max(float(np.linalg.norm(direction)), 1e-10)
        helper = np.array([0.0, 1.0, 0.0]) if abs(direction[1]) < 0.9 else np.array([0.0, 0.0, 1.0])
        a = np.cross(direction, helper)
        a /= np.linalg.norm(a)
        b = np.cross(direction, a)
        rings.append([m.v(tuple(point + radius * (math.cos(k * 2 * math.pi / sides) * a + math.sin(k * 2 * math.pi / sides) * b))) for k in range(sides)])
    for j in range(len(rings) - 1):
        for i in range(sides):
            m.quad(rings[j][i], rings[j][(i + 1) % sides], rings[j + 1][(i + 1) % sides], rings[j + 1][i])
    return m


def turned(material: str, x: float, profile: list[tuple[float, float]], sides: int = 12) -> Mesh:
    """A port turned around a vertical axis at `x`, from (z, radius) rows, closed at both ends."""
    m = Mesh(material)
    rings = [[m.v((x + r * math.cos(2 * math.pi * k / sides), r * math.sin(2 * math.pi * k / sides), z)) for k in range(sides)] for z, r in profile]
    for j in range(len(rings) - 1):
        for k in range(sides):
            # Wound so the sides face out: the game's outline pass draws the back faces.
            m.quad(rings[j][k], rings[j + 1][k], rings[j + 1][(k + 1) % sides], rings[j][(k + 1) % sides])
    for ring in (rings[0], rings[-1][::-1]):
        center = m.v(tuple(np.mean([m.pos[i] for i in ring], axis=0)))
        for k in range(sides):
            m.tri(center, ring[k], ring[(k + 1) % sides])
    return m


def frame() -> list[Mesh]:
    """Everything but the film, liquid and print: flange, seal ridges, creases (clear), the port rim and cap."""
    clear = flange()
    for front in (True, False):
        offset = -0.0007 if front else 0.0007
        for z in (0.229, 0.235):
            xs = np.linspace(-half_width(z) * 0.94, half_width(z) * 0.94, 19)
            clear.extend(tube([(float(x), face_depth(float(x), z, front) + offset, z) for x in xs], 0.00069))
        for side in (-1, 1):
            edge = [(side * half_width(float(z)) * 0.985, float(z)) for z in np.linspace(0.048, 0.221, 23)]
            clear.extend(tube([(x, face_depth(x, z, front) + offset * 3 / 7, z) for x, z in edge], 0.00072))
        # Sparse wrinkles around the shoulders, clear of the print.
        for line in (
            [(-0.044, 0.229), (-0.036, 0.222), (-0.032, 0.215), (-0.023, 0.212)],
            [(-0.043, 0.228), (-0.028, 0.225), (-0.020, 0.217)],
            [(0.045, 0.231), (0.037, 0.222), (0.030, 0.214), (0.020, 0.210)],
            [(0.037, 0.232), (0.023, 0.221), (0.017, 0.218)],
            [(-0.027, 0.230), (-0.014, 0.224), (-0.009, 0.218)],
        ):
            crease = [(max(-half_width(z) * 0.94, min(half_width(z) * 0.94, x)), z) for x, z in line]
            clear.extend(tube([(x, face_depth(x, z, front) + offset / 2, z) for x, z in crease], 0.00033))
    clear.extend(
        turned(
            "clear_plastic",
            -0.014,
            [(0.031, 0.006), (0.025, 0.0063), (0.023, 0.005), (0.018, 0.005), (0.017, 0.0064), (0.014, 0.0064), (0.012, 0.0047), (0.006, 0.0047)],
        )
    )
    clear.extend(turned("clear_plastic", 0.014, [(0.033, 0.0043), (0.023, 0.0048), (0.020, 0.004), (0.012, 0.004)], 10))
    rim = turned("plastic", -0.014, [(0.014, 0.0063), (0.010, 0.0063), (0.009, 0.0046), (0.005, 0.0046)])
    cap = turned("orange_plastic", 0.014, [(0.013, 0.0049), (0.010, 0.0057), (0.004, 0.0057), (0.003, 0.0048)])
    return [clear, rim, cap]


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    path = FONT_DIR / ("DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf")
    return ImageFont.truetype(str(path), size) if path.exists() else ImageFont.load_default(size)


def label_png() -> bytes:
    """The printed markings: ink on a clear background."""
    image = Image.new("RGBA", (1024, 1280), (255, 255, 255, 0))
    d = ImageDraw.Draw(image)
    ink, muted = (26, 35, 37, 245), (49, 64, 68, 215)
    d.text((92, 90), "STERILE • SINGLE USE", font=font(35, True), fill=muted)
    d.line((92, 150, 932, 150), fill=(28, 37, 40, 170), width=3)
    d.text((92, 176), "SODIUM CHLORIDE", font=font(57, True), fill=ink)
    d.text((92, 249), "0.9%  IV INFUSION", font=font(50, True), fill=ink)
    d.rectangle((746, 322, 935, 405), outline=ink, width=4)
    d.text((764, 338), "500 mL", font=font(38, True), fill=ink)
    d.text((92, 334), "Isotonic saline solution", font=font(28), fill=ink)
    d.line((92, 439, 936, 439), fill=(28, 37, 40, 125), width=2)
    details = [
        "Each 100 mL contains:",
        "Sodium Chloride USP ................ 0.9 g",
        "Water for injection ................. q.s.",
        "Na+ 154 mmol/L    Cl- 154 mmol/L",
        "For intravenous administration only.",
        "Do not use if solution is cloudy.",
        "Use only if container is intact.",
    ]
    for i, line in enumerate(details):
        d.text((93, 472 + i * 48), line, font=font(27 if i == 0 else 25, i == 0), fill=ink)
    d.line((92, 842, 937, 842), fill=(28, 37, 40, 140), width=2)
    d.text((93, 862), "EXP 04/28     LOT 0276-A", font=font(29, True), fill=ink)
    d.text((93, 915), "KEEP AT ROOM TEMPERATURE", font=font(23), fill=muted)
    d.text((93, 976), "IV", font=font(69, True), fill=ink)
    d.text((214, 1003), "Medical prop • not for clinical use", font=font(19), fill=muted)
    for i in range(5):
        d.line((120, 1075 + i * 40, 177 if i % 2 == 0 else 159, 1075 + i * 40), fill=muted, width=4)
    out = io.BytesIO()
    image.save(out, format="PNG")
    return out.getvalue()


class Glb:
    """Accumulates the binary buffer and its views and accessors."""

    def __init__(self) -> None:
        self.data = bytearray()
        self.views: list[dict[str, int]] = []
        self.accessors: list[dict[str, Any]] = []

    def view(self, blob: bytes, target: int | None = None) -> int:
        self.data.extend(b"\0" * (-len(self.data) % 4))
        self.views.append({"buffer": 0, "byteOffset": len(self.data), "byteLength": len(blob)} | ({"target": target} if target else {}))
        self.data.extend(blob)
        return len(self.views) - 1

    def accessor(self, array: NDArray[Any], bounds: bool = False) -> int:
        array = np.ascontiguousarray(array)
        component = {np.dtype(np.float32): 5126, np.dtype(np.uint16): 5123}[array.dtype]
        kind = {1: "SCALAR", 2: "VEC2", 3: "VEC3", 4: "VEC4", 16: "MAT4"}[array.shape[1] if array.ndim == 2 else 1]
        accessor: dict[str, Any] = {"bufferView": self.view(array.tobytes(), 34963 if array.ndim == 1 else 34962 if kind != "MAT4" else None)}
        accessor |= {"componentType": component, "count": len(array), "type": kind}
        if bounds:
            accessor |= {"min": array.min(axis=0).tolist(), "max": array.max(axis=0).tolist()}
        self.accessors.append(accessor)
        return len(self.accessors) - 1

    def write(self, gltf: dict[str, Any], path: Path) -> None:
        self.data.extend(b"\0" * (-len(self.data) % 4))
        gltf |= {"buffers": [{"byteLength": len(self.data)}], "bufferViews": self.views, "accessors": self.accessors}
        text = json.dumps(gltf, separators=(",", ":")).encode()
        text += b" " * (-len(text) % 4)
        chunks = struct.pack("<I4s", len(text), b"JSON") + text + struct.pack("<I4s", len(self.data), b"BIN\0") + bytes(self.data)
        path.write_bytes(b"glTF" + struct.pack("<II", 2, 12 + len(chunks)) + chunks)


def material(name: str) -> dict[str, Any]:
    swatch = PALETTE[name]
    alpha = 0.42 if name == "glass" else 1.0
    pbr: dict[str, Any] = {
        "baseColorFactor": [to_linear(c) for c in swatch.color] + [alpha],
        "metallicFactor": swatch.metallic,
        "roughnessFactor": swatch.roughness,
    }
    extra: dict[str, Any] = {"alphaMode": "BLEND"} if name == "glass" else {}
    if name == "print":
        pbr["baseColorTexture"] = {"index": 0}
        extra = {"alphaMode": "MASK", "alphaCutoff": 0.5}
    return {"name": name, "pbrMetallicRoughness": pbr, "doubleSided": True} | extra


def build() -> Path:
    scale = tool_lengths().get("saline_bag", 0.15) / TOP

    def place(points: Array) -> NDArray[np.float32]:
        """Pack frame to tool space: turned half round Z (print to +Y), hung from the origin, down -Z to the tip."""
        return np.asarray(np.stack([-points[:, 0], -points[:, 1], points[:, 2] - TOP], axis=1) * scale, np.float32)

    glb = Glb()
    materials = ["glass", "tint", "print", "clear_plastic", "plastic", "orange_plastic"]
    nodes: list[dict[str, Any]] = []
    for name, at, parent in zip(BONES, place(np.array(BONE_AT)), BONE_PARENT, strict=True):
        nodes.append({"name": name, "translation": (at - (place(np.array(BONE_AT))[parent] if parent >= 0 else 0)).tolist()})
        if parent >= 0:
            nodes[parent].setdefault("children", []).append(len(nodes) - 1)
    # glTF joints bind at their rest positions: the inverse of a translation is its negation.
    binds = np.array([np.eye(4, dtype=np.float32) for _ in BONES])
    binds[:, 3, :3] = -place(np.array(BONE_AT))
    skin = {"name": "IvBagRig", "joints": list(range(len(BONES))), "skeleton": 0, "inverseBindMatrices": glb.accessor(binds.reshape(-1, 16))}

    def primitive(mesh: Mesh, skinned: bool, morphs: bool, origin: Array) -> dict[str, Any]:
        source = np.array(mesh.pos)
        pos = place(source)
        ids = np.array(mesh.idx).reshape(-1, 3)
        normals = np.zeros_like(pos)
        for corner in range(3):
            np.add.at(normals, ids[:, corner], np.cross(pos[ids[:, 1]] - pos[ids[:, 0]], pos[ids[:, 2]] - pos[ids[:, 0]]))
        normals /= np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-12)
        attributes = {
            "POSITION": glb.accessor(pos - origin.astype(np.float32), True),
            "NORMAL": glb.accessor(normals),
            "TEXCOORD_0": glb.accessor(np.array(mesh.uv, np.float32)),
        }
        if skinned:
            influences = [bone_weights(x, z) for x, _, z in source]
            joints = np.zeros((len(source), 4), np.uint16)
            weights = np.zeros((len(source), 4), np.float32)
            for row, (bones, shares) in enumerate(influences):
                joints[row, : len(bones)] = bones
                weights[row, : len(shares)] = shares
            attributes |= {"JOINTS_0": glb.accessor(joints), "WEIGHTS_0": glb.accessor(weights)}
        result = {"attributes": attributes, "indices": glb.accessor(np.array(mesh.idx, np.uint16)), "material": materials.index(mesh.material)}
        if morphs:
            # Emptied the film falls flat and draws in a little; resting on a surface it spreads and flattens.
            zero = np.zeros(len(pos), np.float32)
            empty = np.stack([-0.055 * pos[:, 0], -0.64 * pos[:, 1], zero], axis=1)
            flat = np.stack([0.075 * pos[:, 0], -0.43 * pos[:, 1], zero], axis=1)
            result["targets"] = [{"POSITION": glb.accessor(empty.astype(np.float32), True)}, {"POSITION": glb.accessor(flat.astype(np.float32), True)}]
        return result

    meshes: list[dict[str, Any]] = []

    def part(name: str, parts: list[Mesh], skinned: bool = True, morphs: bool = False, origin: Array | None = None) -> None:
        pivot = np.zeros(3) if origin is None else origin
        mesh: dict[str, Any] = {"name": name, "primitives": [primitive(p, skinned, morphs, pivot) for p in parts]}
        if morphs:
            mesh |= {"weights": [0.0] * len(MORPHS), "extras": {"targetNames": MORPHS}}
        meshes.append(mesh)
        nodes.append({"name": name, "mesh": len(meshes) - 1} | ({"skin": 0} if skinned else {"translation": pivot.tolist()}))

    part("Bag", [panel("glass", 0.032, 0.237, 25, 16)], morphs=True)
    liquid = panel("tint", 0.038, 0.179, 18, 14, 0.956, 0.0012)
    part("Level", [liquid], skinned=False, origin=np.array([0.0, 0.0, place(np.array(liquid.pos))[:, 2].min()]))
    part("Label", [print_decal()], morphs=True)
    part("Frame", frame())
    gltf: dict[str, Any] = {
        "asset": {"version": "2.0", "generator": "tools/assetgen/iv_bag.py"},
        "scene": 0,
        "scenes": [{"nodes": [0, *range(len(BONES), len(nodes))]}],
        "nodes": nodes,
        "skins": [skin],
        "meshes": meshes,
        "materials": [material(name) for name in materials],
        "images": [{"name": "print", "bufferView": glb.view(label_png()), "mimeType": "image/png"}],
        "samplers": [{"magFilter": 9729, "minFilter": 9987, "wrapS": 33071, "wrapT": 33071}],
        "textures": [{"sampler": 0, "source": 0}],
    }
    path = MODELS_DIR / "tools" / "iv_bag.glb"
    glb.write(gltf, path)
    return path
