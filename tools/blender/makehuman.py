"""Extracts the patient's base body from MakeHuman's CC0 assets into sources/makehuman_male.npz.

MakeHuman's base mesh, targets and rig weights are CC0 (https://static.makehumancommunity.org/about/license.html).
They ship inside the MPFB add-on; only its data files are read, none of its (GPL) code. Run from the repo root with the
add-on's zip from https://extensions.blender.org/add-ons/mpfb/ (tested with 2.0.18):
    python -m tools.blender.makehuman add-on-mpfb-v2.0.18.zip

The body is an average young man: the base mesh with the caucasian young male and the average muscle and weight
targets at full strength. Everything stays in MakeHuman's frame (metres, Y up, facing +Z, the man's left toward +X):
- verts, quads: the skin ("body" group), with quads indexing verts.
- joint_<name>: the center of each of MakeHuman's joint helper cubes (shoulder, elbow, jaw, l-eye, ...).
- helper_<name>: the vertices of the eye, teeth and tongue helpers, which mark where those parts belong.
- weight_<bone>: one row per skin vertex with its weight for each of MakeHuman's game engine rig bones.
- weight_jaw: the same for the jaw, which that rig leaves in the head: MakeHuman's default rig's jaw with the lower lip
  and tongue bones it carries.
"""

from __future__ import annotations

import gzip
import json
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from numpy.typing import NDArray

SOURCE = Path(__file__).resolve().parent / "sources" / "makehuman_male.npz"
TARGETS = ("caucasian-male-young", "universal-male-young-averagemuscle-averageweight")
HELPERS = ("l-eye", "r-eye", "upper-teeth", "lower-teeth", "tongue")


def _read(archive: zipfile.ZipFile, path: str) -> str:
    name = next(n for n in archive.namelist() if n.endswith(path))
    data = archive.read(name)
    return gzip.decompress(data).decode() if name.endswith(".gz") else data.decode()


def _jaw(archive: zipfile.ZipFile) -> list[tuple[int, float]]:
    """(vertex, weight) pairs for everything the default rig's jaw bone moves."""
    rig = json.loads(_read(archive, "data/rigs/standard/rig.default.json"))
    weights = json.loads(_read(archive, "data/rigs/standard/weights.default.json"))["weights"]

    def on_jaw(bone: str | None) -> bool:
        return bool(bone) and (bone == "jaw" or on_jaw(rig[bone].get("parent")))

    total: dict[int, float] = {}
    for bone, pairs in weights.items():
        if on_jaw(bone):
            for index, weight in pairs:
                total[index] = total.get(index, 0.0) + weight
    return list(total.items())


def extract(zip_path: Path) -> dict[str, NDArray[np.float64] | NDArray[np.int32]]:
    with zipfile.ZipFile(zip_path) as archive:
        verts: list[list[float]] = []
        faces: dict[str, list[list[int]]] = {}
        group = ""
        for line in _read(archive, "data/3dobjs/base.obj").splitlines():
            if line.startswith("v "):
                verts.append([float(t) for t in line.split()[1:4]])
            elif line.startswith("g "):
                group = line[2:].strip()
            elif line.startswith("f "):
                faces.setdefault(group, []).append([int(t.split("/")[0]) - 1 for t in line.split()[1:]])
        v = np.array(verts)
        for target in TARGETS:
            for line in _read(archive, f"data/targets/macrodetails/{target}.target.gz").splitlines():
                if line[:1].isdigit():
                    i, x, y, z = line.split()
                    v[int(i)] += (float(x), float(y), float(z))
        weights = json.loads(_read(archive, "data/rigs/standard/weights.game_engine.json"))["weights"]
        weights["jaw"] = _jaw(archive)
    # MakeHuman works in decimetres with the ground at the feet's soles.
    v *= 0.1
    body = np.array(faces["body"], dtype=np.int32)
    used = np.unique(body)
    remap = np.full(len(v), -1, dtype=np.int32)
    remap[used] = np.arange(len(used), dtype=np.int32)
    ground = v[used, 1].min()
    v[:, 1] -= ground
    out: dict[str, NDArray[np.float64] | NDArray[np.int32]] = {"verts": v[used], "quads": remap[body]}
    for name, quads in faces.items():
        if name.startswith("joint-"):
            out[f"joint_{name[6:]}"] = v[np.unique(quads)].mean(axis=0)
        elif name.startswith("helper-") and name[7:] in HELPERS:
            out[f"helper_{name[7:]}"] = v[np.unique(quads)]
    for bone, pairs in weights.items():
        column = np.zeros(len(used))
        for index, weight in pairs:
            if remap[index] >= 0:
                column[remap[index]] = weight
        if column.any():
            out[f"weight_{bone}"] = column
    return out


# Where the man lies on the table, in game space: crown at the head end where the old body's was, back on the mattress.
CROWN_X, BACK_Y = 0.806, -0.11
# Arm directions in MakeHuman's frame (the man's left side, standing): straight down by the hips, a little out from
# them, the forearm turned so the palm faces back (down on the table) and the thumb toward the body.
UPPER_ARM_DOWN, FOREARM_DOWN, PALM_TURN = (0.13, -1.0, -0.02), (0.05, -1.0, -0.03), 90.0
# How far apart the ankles lie, from the middle: legs together, as a patient lies.
ANKLE_SPREAD = 0.11
# MakeHuman's game engine rig bones that make up each of the patient's (L side; R mirrors it).
HAND_BONES = ("hand", *(f"{finger}_0{joint}" for finger in ("thumb", "index", "middle", "ring", "pinky") for joint in (1, 2, 3)))
BONE_WEIGHTS = {
    "Torso": ("pelvis", "spine_01", "spine_02"),
    "Chest": ("spine_03", "clavicle_l", "clavicle_r"),
    "Neck": ("neck_01",),
    "Head": ("head",),
    "Jaw": ("jaw",),
}
for _s, _m in (("L", "l"), ("R", "r")):
    BONE_WEIGHTS |= {
        f"UpperArm{_s}": (f"upperarm_{_m}",),
        f"Forearm{_s}": (f"lowerarm_{_m}",),
        f"Hand{_s}": tuple(f"{b}_{_m}" for b in HAND_BONES),
        f"Thigh{_s}": (f"thigh_{_m}",),
        f"Shin{_s}": (f"calf_{_m}",),
        f"Foot{_s}": (f"foot_{_m}", f"ball_{_m}"),
    }


@dataclass(frozen=True)
class Man:
    """The patient's body lying on the table, in game space: +X toward the head, +Y up out of the face, +Z his left."""

    verts: NDArray[np.float64]
    quads: NDArray[np.int32]
    joints: dict[str, NDArray[np.float64]]
    helpers: dict[str, NDArray[np.float64]]
    # Skin weight per vertex for each of the patient's rig bones (BONE_WEIGHTS), adding up to 1.
    weights: dict[str, NDArray[np.float64]]


def _rotation(axis: NDArray[np.float64], angle: float) -> NDArray[np.float64]:
    a = axis / np.linalg.norm(axis)
    k = np.array([[0.0, -a[2], a[1]], [a[2], 0.0, -a[0]], [-a[1], a[0], 0.0]])
    return np.asarray(np.eye(3) + np.sin(angle) * k + (1.0 - np.cos(angle)) * k @ k, dtype=np.float64)


def _swing(start: NDArray[np.float64], end: NDArray[np.float64]) -> NDArray[np.float64]:
    """The rotation that turns direction start onto direction end."""
    a, b = start / np.linalg.norm(start), end / np.linalg.norm(end)
    axis = np.cross(a, b)
    if np.linalg.norm(axis) < 1e-9:
        return np.eye(3)
    return _rotation(axis, float(np.arccos(np.clip(a @ b, -1.0, 1.0))))


def _pose(v: NDArray[np.float64], joints: dict[str, NDArray[np.float64]], w: dict[str, NDArray[np.float64]]) -> None:
    """Lowers the arms from MakeHuman's A pose to the sides, palms down, and brings the legs together, in place.
    Linear blend skinning with MakeHuman's own weights; the forearm's turn grows from elbow to wrist like a real one."""
    zero = np.zeros(len(v))
    for side, s in ((1.0, "l"), (-1.0, "r")):
        shoulder, elbow, wrist = joints[f"{s}-shoulder"], joints[f"{s}-elbow"], joints[f"{s}-hand"]
        upper = _swing(elbow - shoulder, np.array(UPPER_ARM_DOWN) * (side, 1.0, 1.0))
        new_elbow = shoulder + upper @ (elbow - shoulder)
        fore = _swing(upper @ (wrist - elbow), np.array(FOREARM_DOWN) * (side, 1.0, 1.0)) @ upper
        new_wrist = new_elbow + fore @ (wrist - elbow)
        axis = (new_wrist - new_elbow) / np.linalg.norm(new_wrist - new_elbow)
        hand = sum((w.get(f"{b}_{s}", zero) for b in HAND_BONES), zero)
        arm = w[f"upperarm_{s}"]
        forearm = w[f"lowerarm_{s}"] + hand
        by_upper = (v - shoulder) @ upper.T + shoulder
        by_fore = (v - elbow) @ fore.T + new_elbow
        along = np.clip((by_fore - new_elbow) @ axis / np.linalg.norm(new_wrist - new_elbow), 0.0, 1.0)
        turn = np.radians(PALM_TURN) * side * np.where(hand > 0.5, 1.0, along)
        for i in np.nonzero(forearm > 0.0)[0]:
            by_fore[i] = new_elbow + _rotation(axis, turn[i]) @ (by_fore[i] - new_elbow)
        v[:] = v * (1.0 - arm - forearm)[:, None] + by_upper * arm[:, None] + by_fore * forearm[:, None]
        full_turn = _rotation(axis, np.radians(PALM_TURN) * side)
        for name in joints:
            if name.startswith((f"{s}-finger", f"{s}-hand")):
                joints[name] = new_elbow + full_turn @ (fore @ (joints[name] - elbow))
        joints[f"{s}-elbow"] = new_elbow
        # Legs: swing each one in about the hip until its ankle lies ANKLE_SPREAD from the middle.
        hip, ankle = joints[f"{s}-upper-leg"], joints[f"{s}-ankle"]
        target = np.array([ANKLE_SPREAD * side, ankle[1], ankle[2]])
        leg = _swing(ankle - hip, target - hip)
        weight = w[f"thigh_{s}"] + w[f"calf_{s}"] + w[f"foot_{s}"] + w.get(f"ball_{s}", zero)
        v[:] = v * (1.0 - weight)[:, None] + ((v - hip) @ leg.T + hip) * weight[:, None]
        for name in joints:
            if name.startswith((f"{s}-knee", f"{s}-ankle", f"{s}-foot", f"{s}-toe")):
                joints[name] = hip + leg @ (joints[name] - hip)


def load() -> Man:
    """The man from sources/makehuman_male.npz, posed and laid on the table."""
    data = np.load(SOURCE)
    verts = np.array(data["verts"], dtype=np.float64)
    joints = {k[6:]: np.array(data[k], dtype=np.float64) for k in data.files if k.startswith("joint_")}
    w = {k[7:]: np.array(data[k], dtype=np.float64) for k in data.files if k.startswith("weight_")}
    _pose(verts, joints, w)
    height = verts[:, 1].max()
    # Standing (x his left, y up, z forward) to lying: head along +X, face up +Y, his left +Z.
    offset = np.array([CROWN_X - height, BACK_Y - verts[:, 2].min(), 0.0])

    def lay(p: NDArray[np.float64]) -> NDArray[np.float64]:
        return np.asarray(p[..., [1, 2, 0]] + offset, dtype=np.float64)

    weights = {bone: np.sum([w[name] for name in names if name in w], axis=0) for bone, names in BONE_WEIGHTS.items()}
    # The game engine rig's head carries the jaw too: hand the jaw its share.
    weights["Jaw"] = np.minimum(weights["Jaw"], weights["Head"])
    weights["Head"] -= weights["Jaw"]
    helpers = {k[7:]: lay(np.array(data[k], dtype=np.float64)) for k in data.files if k.startswith("helper_")}
    return Man(lay(verts), np.array(data["quads"], dtype=np.int32), {k: lay(p) for k, p in joints.items()}, helpers, weights)


def main() -> None:
    data = extract(Path(sys.argv[1]))
    np.savez_compressed(SOURCE, **data)  # type: ignore[arg-type]
    print(f"{SOURCE}: {len(data['verts'])} vertices, {len(data['quads'])} quads")


if __name__ == "__main__":
    main()
