"""The IV bag (model "iv_bag" in data/tools.cfg): a soft film bag with a printed label, two ports and a hanger flange.

Converted from the low-poly bag of the IV bag asset pack (sources/iv_bag_lod1.glb), scaled to the tool's length: hanger
at the origin, ports at the tip, print on +Y. Its parts are merged by material into the game's names, keeping the
pack's seven-bone rig (Anchor > Neck > Upper > Middle > Lower, with LeftCorner and RightCorner under Lower) and the
EmptyBag and RestingFlat blend shapes on Bag and Label.
Level is the liquid, left unskinned with its origin at its bottom, so the game can stretch it by how full the bag is.
"""

from __future__ import annotations

import json
import struct
from pathlib import Path
from typing import Any

import numpy as np
from numpy.typing import NDArray

from .export import MODELS_DIR
from .instruments import tool_lengths
from .palette import PALETTE, to_linear

SOURCE = Path(__file__).resolve().parent / "sources" / "iv_bag_lod1.glb"
# The pack's glTF frame: Y up from the bag's foot, X across, the print toward +Z. TOP is the flange's top edge.
TOP = 0.265
# Pack mesh -> (game part, game material). Parts sharing a material become one surface.
PARTS = {
    "Bag_Film_Skinned": ("Bag", "glass"),
    "Liquid_70pct_Skinned": ("Level", "tint"),
    "Front_Print_Skinned": ("Label", "print"),
    "Sealed_Hanger_Flange": ("Frame", "clear_plastic"),
    "Bag_Seams_And_Creases_Skinned": ("Frame", "clear_plastic"),
    "Bag_Clear_Ports_Skinned": ("Frame", "clear_plastic"),
    "Main_Drip_Port_Rim": ("Frame", "plastic"),
    "Injection_Orange_Cap": ("Frame", "orange_plastic"),
}
# Closed pack meshes whose faces may wind inward: the game's outline pass draws back faces, so they're turned out.
CLOSED = {"Bag_Film_Skinned", "Liquid_70pct_Skinned", "Sealed_Hanger_Flange", "Bag_Clear_Ports_Skinned", "Main_Drip_Port_Rim", "Injection_Orange_Cap"}
MATERIALS = ["glass", "tint", "print", "clear_plastic", "plastic", "orange_plastic"]
TYPES = {5126: np.float32, 5123: np.uint16, 5121: np.uint8, 5125: np.uint32}
WIDTHS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


class Source:
    """Reads the pack's .glb: its JSON and tightly packed accessors."""

    def __init__(self, path: Path) -> None:
        data = path.read_bytes()
        length = struct.unpack_from("<I", data, 12)[0]
        self.gltf: dict[str, Any] = json.loads(data[20 : 20 + length])
        self.binary = data[28 + length :]

    def view(self, index: int) -> bytes:
        view = self.gltf["bufferViews"][index]
        start = view.get("byteOffset", 0)
        return self.binary[start : start + view["byteLength"]]

    def array(self, index: int) -> NDArray[Any]:
        accessor = self.gltf["accessors"][index]
        values = np.frombuffer(self.view(accessor["bufferView"]), TYPES[accessor["componentType"]], accessor["count"] * WIDTHS[accessor["type"]])
        return values.reshape(accessor["count"], -1) if WIDTHS[accessor["type"]] > 1 else values


def turned_out(positions: NDArray[np.float32], triangles: NDArray[Any]) -> NDArray[Any]:
    """The triangles of a closed mesh wound so its faces point outward (positive volume)."""
    a, b, c = (positions[triangles[:, i]].astype(np.float64) for i in range(3))
    return triangles if np.einsum("ij,ij->i", a, np.cross(b, c)).sum() > 0 else triangles[:, ::-1]


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
    source = Source(SOURCE)
    pack = source.gltf
    scale = tool_lengths().get("saline_bag", 0.15) / TOP
    # Pack to tool space: turned so the print faces +Y, hung from the origin, down -Z to the tip. A proper rotation
    # (no mirroring), so faces keep their winding.
    turn = np.array([[-1.0, 0.0, 0.0], [0.0, 0.0, 1.0], [0.0, 1.0, 0.0]])

    def place(points: NDArray[Any]) -> NDArray[np.float32]:
        return np.asarray((points @ turn.T - [0.0, 0.0, TOP]) * scale, np.float32)

    def placed_offsets(offsets: NDArray[Any]) -> NDArray[np.float32]:
        return np.asarray(offsets @ turn.T * scale, np.float32)

    glb = Glb()
    skin = pack["skins"][0]
    joints = skin["joints"]
    nodes: list[dict[str, Any]] = []
    parents = [next((joints.index(i) for i in joints if joint in pack["nodes"][i].get("children", [])), -1) for joint in joints]
    rest: list[NDArray[Any]] = []
    for joint, parent in zip(joints, parents, strict=True):
        local = np.array(pack["nodes"][joint].get("translation", [0.0, 0.0, 0.0]))
        rest.append(local + (rest[parent] if parent >= 0 else 0.0))
    bones = place(np.array(rest))
    for i, (joint, parent) in enumerate(zip(joints, parents, strict=True)):
        nodes.append({"name": pack["nodes"][joint]["name"], "translation": (bones[i] - (bones[parent] if parent >= 0 else 0)).tolist()})
        if parent >= 0:
            nodes[parent].setdefault("children", []).append(i)
    # glTF joints bind at their rest positions: the inverse of a translation is its negation.
    binds = np.array([np.eye(4, dtype=np.float32) for _ in joints])
    binds[:, 3, :3] = -bones

    surfaces: dict[str, dict[str, list[dict[str, NDArray[Any]]]]] = {}
    morph_names: dict[str, list[str]] = {}
    for node in pack["nodes"]:
        if node["name"] not in PARTS:
            continue
        part, kind = PARTS[node["name"]]
        mesh = pack["meshes"][node["mesh"]]
        primitive = mesh["primitives"][0]
        if "extras" in mesh:
            morph_names[part] = mesh["extras"]["targetNames"]
        attributes = primitive["attributes"]
        positions = place(source.array(attributes["POSITION"]))
        triangles = source.array(primitive["indices"]).reshape(-1, 3).astype(np.int64)
        if node["name"] in CLOSED:
            triangles = turned_out(positions, triangles)
        piece = {
            "pos": positions,
            "uv": source.array(attributes["TEXCOORD_0"]),
            "tri": triangles,
            "joints": source.array(attributes["JOINTS_0"]),
            "weights": source.array(attributes["WEIGHTS_0"]),
        }
        for t, target in enumerate(primitive.get("targets", [])):
            piece[f"morph{t}"] = placed_offsets(source.array(target["POSITION"]))
        surfaces.setdefault(part, {}).setdefault(kind, []).append(piece)

    meshes: list[dict[str, Any]] = []
    for part, by_material in surfaces.items():
        skinned = part != "Level"
        pivot = np.zeros(3, np.float32)
        if not skinned:
            pivot[2] = min(piece["pos"][:, 2].min() for pieces in by_material.values() for piece in pieces)
        primitives = []
        for kind, pieces in by_material.items():
            offsets = np.cumsum([0] + [len(piece["pos"]) for piece in pieces[:-1]])
            pos = np.concatenate([piece["pos"] for piece in pieces]) - pivot
            triangles = np.concatenate([piece["tri"] + offset for piece, offset in zip(pieces, offsets, strict=True)])
            normals = np.zeros_like(pos)
            for corner in range(3):
                np.add.at(normals, triangles[:, corner], np.cross(pos[triangles[:, 1]] - pos[triangles[:, 0]], pos[triangles[:, 2]] - pos[triangles[:, 0]]))
            normals /= np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-12)
            attributes = {
                "POSITION": glb.accessor(pos, True),
                "NORMAL": glb.accessor(normals),
                "TEXCOORD_0": glb.accessor(np.concatenate([piece["uv"] for piece in pieces])),
            }
            if skinned:
                attributes |= {
                    "JOINTS_0": glb.accessor(np.concatenate([piece["joints"] for piece in pieces])),
                    "WEIGHTS_0": glb.accessor(np.concatenate([piece["weights"] for piece in pieces])),
                }
            result: dict[str, Any] = {
                "attributes": attributes,
                "indices": glb.accessor(triangles.astype(np.uint16).ravel()),
                "material": MATERIALS.index(kind),
            }
            morphs = sorted(key for key in pieces[0] if key.startswith("morph"))
            if morphs:
                result["targets"] = [{"POSITION": glb.accessor(np.concatenate([piece[key] for piece in pieces]), True)} for key in morphs]
            primitives.append(result)
        result_mesh: dict[str, Any] = {"name": part, "primitives": primitives}
        if part in morph_names:
            result_mesh |= {"weights": [0.0] * len(morph_names[part]), "extras": {"targetNames": morph_names[part]}}
        meshes.append(result_mesh)
        nodes.append({"name": part, "mesh": len(meshes) - 1} | ({"skin": 0} if skinned else {"translation": pivot.tolist()}))

    texture = pack["images"][0]
    gltf: dict[str, Any] = {
        "asset": {"version": "2.0", "generator": "tools/assetgen/iv_bag.py"},
        "scene": 0,
        "scenes": [{"nodes": [0, *range(len(joints), len(nodes))]}],
        "nodes": nodes,
        "skins": [{"name": "IvBagRig", "joints": list(range(len(joints))), "skeleton": 0, "inverseBindMatrices": glb.accessor(binds.reshape(-1, 16))}],
        "meshes": meshes,
        "materials": [material(name) for name in MATERIALS],
        "images": [{"name": "print", "bufferView": glb.view(source.view(texture["bufferView"])), "mimeType": texture["mimeType"]}],
        "samplers": pack["samplers"],
        "textures": [{"sampler": 0, "source": 0}],
    }
    path = MODELS_DIR / "tools" / "iv_bag.glb"
    glb.write(gltf, path)
    return path
