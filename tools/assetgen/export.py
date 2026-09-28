"""Writes models to assets/models/<category>/<name>.glb."""

from __future__ import annotations

from pathlib import Path

import numpy as np
import trimesh

from .geometry import Model
from .palette import material

MODELS_DIR = Path(__file__).resolve().parents[2] / "assets" / "models"


def write(model: Model) -> Path:
    scene = trimesh.Scene()
    for part in model.parts:
        mesh = part.mesh.copy()
        mesh.visual = trimesh.visual.TextureVisuals(material=material(part.material))
        transform = np.eye(4)
        transform[:3, 3] = part.pivot
        scene.add_geometry(mesh, node_name=part.name, geom_name=part.name, parent_node_name=part.parent or "world", transform=transform)
    path = MODELS_DIR / model.category / f"{model.name}.glb"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(scene.export(file_type="glb", include_normals=True))
    return path
