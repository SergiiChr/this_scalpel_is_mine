"""Material names and base colors. The game re-skins these with its cel shader by name (src/visual/model_slot.gd).

Special names the game treats differently:
- "skin": the patient's skin (cavity carving, skin tone) or the surgeon's face.
- "tint": recolored per instance (drug color, scrubs color).
"""

from __future__ import annotations

from dataclasses import dataclass

import trimesh


@dataclass(frozen=True)
class Swatch:
    color: tuple[float, float, float]
    roughness: float = 0.6
    metallic: float = 0.0


PALETTE: dict[str, Swatch] = {
    "skin": Swatch((0.84, 0.66, 0.56), 0.6),
    "tint": Swatch((0.5, 0.5, 0.5), 0.5),
    "hair": Swatch((0.12, 0.09, 0.07), 0.8),
    "lips": Swatch((0.62, 0.38, 0.36), 0.5),
    "eye": Swatch((0.92, 0.9, 0.86), 0.2),
    "iris": Swatch((0.15, 0.12, 0.1), 0.2),
    "gown": Swatch((0.4, 0.55, 0.5), 0.9),
    "glove": Swatch((0.56, 0.7, 0.82), 0.45),
    "mask": Swatch((0.55, 0.72, 0.78), 0.9),
    "steel": Swatch((0.78, 0.8, 0.83), 0.25, 1.0),
    "dark_steel": Swatch((0.35, 0.37, 0.4), 0.35, 1.0),
    "chrome": Swatch((0.88, 0.9, 0.92), 0.12, 1.0),
    "plastic": Swatch((0.88, 0.88, 0.86), 0.5),
    "clear_plastic": Swatch((0.85, 0.92, 0.95), 0.2),
    "black_plastic": Swatch((0.08, 0.08, 0.09), 0.5),
    "rubber": Swatch((0.12, 0.12, 0.12), 0.9),
    "blue_plastic": Swatch((0.2, 0.35, 0.6), 0.5),
    "green_plastic": Swatch((0.2, 0.5, 0.35), 0.5),
    "red_plastic": Swatch((0.7, 0.15, 0.12), 0.5),
    "yellow_plastic": Swatch((0.85, 0.72, 0.12), 0.5),
    "wood": Swatch((0.38, 0.26, 0.16), 0.8),
    "brass": Swatch((0.72, 0.58, 0.25), 0.3, 1.0),
    "gold": Swatch((0.85, 0.66, 0.2), 0.25, 1.0),
    "leather": Swatch((0.25, 0.15, 0.1), 0.8),
    "fabric_white": Swatch((0.93, 0.93, 0.9), 0.95),
    "fabric_dark": Swatch((0.15, 0.15, 0.16), 0.95),
    "paper": Swatch((0.88, 0.85, 0.76), 0.95),
    "mattress": Swatch((0.15, 0.3, 0.3), 0.8),
    "screen": Swatch((0.02, 0.05, 0.04), 0.1),
    "flesh": Swatch((0.6, 0.2, 0.2), 0.3),
    "organ": Swatch((0.55, 0.28, 0.26), 0.25),
    "bone": Swatch((0.9, 0.87, 0.76), 0.6),
    "blood_bag": Swatch((0.45, 0.03, 0.06), 0.2),
    "flame": Swatch((1.0, 0.6, 0.15), 0.5),
    "cotton": Swatch((0.96, 0.95, 0.92), 1.0),
    "iodine": Swatch((0.55, 0.3, 0.12), 0.9),
    "whiskey": Swatch((0.6, 0.35, 0.1), 0.2),
}


def material(name: str) -> trimesh.visual.material.PBRMaterial:
    swatch = PALETTE[name]
    rgba = [int(c * 255) for c in swatch.color] + [255]
    return trimesh.visual.material.PBRMaterial(name=name, baseColorFactor=rgba, roughnessFactor=swatch.roughness, metallicFactor=swatch.metallic)
