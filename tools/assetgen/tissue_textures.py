"""Seamless 3D textures for the tissue layers (tissue_layer.gdshader), baked so the shader samples detail instead of
computing it per pixel. Each is saved as a PNG of its slices laid out in a grid, imported by Godot as a Texture3D.

- fat_cells.png: distance to the nearest of randomly placed cell centers (Worley F1), CELLS cells along each side.
- tissue_noise.png: smooth value noise, LATTICE lattice cells along each side.

Both wrap around on every axis, so they tile in all three directions."""

from __future__ import annotations

from pathlib import Path

import numpy as np
from PIL import Image

TEXTURES_DIR = Path(__file__).resolve().parents[2] / "assets" / "textures"

CELL_SIZE = 128
CELLS = 8
NOISE_SIZE = 64
LATTICE = 16
# Slices across and down the PNG.
COLUMNS = 16


def _cells(rng: np.random.Generator) -> np.ndarray:
    # Grid coordinates in cell units, voxel centers.
    axis = (np.arange(CELL_SIZE) + 0.5) * CELLS / CELL_SIZE
    p = np.stack(np.meshgrid(axis, axis, axis, indexing="ij"), axis=-1)
    centers = rng.random((CELLS, CELLS, CELLS, 3))
    home = np.floor(p).astype(int)
    best = np.full(p.shape[:3], 8.0)
    for offset in np.ndindex(3, 3, 3):
        cell = home + np.array(offset) - 1
        wrapped = cell % CELLS
        center = cell + centers[wrapped[..., 0], wrapped[..., 1], wrapped[..., 2]]
        best = np.minimum(best, np.linalg.norm(p - center, axis=-1))
    clipped: np.ndarray = np.clip(best, 0.0, 1.0)
    return clipped


def _noise(rng: np.random.Generator) -> np.ndarray:
    values = rng.random((LATTICE, LATTICE, LATTICE))
    axis = (np.arange(NOISE_SIZE) + 0.5) * LATTICE / NOISE_SIZE
    i = np.floor(axis).astype(int)
    f = axis - i
    # The same smooth step between lattice values as the shader's noise3() had.
    f = f * f * (3.0 - 2.0 * f)
    out = np.zeros((NOISE_SIZE,) * 3)
    for corner in np.ndindex(2, 2, 2):
        weight = [f if c else 1.0 - f for c in corner]
        index = [(i + c) % LATTICE for c in corner]
        out += values[np.ix_(*index)] * weight[0][:, None, None] * weight[1][None, :, None] * weight[2][None, None, :]
    return out


def _save(volume: np.ndarray, name: str) -> Path:
    size = volume.shape[0]
    rows = size // COLUMNS
    # Slice z goes to column z % COLUMNS, row z // COLUMNS; within a slice x runs across and y down.
    image = volume.transpose(2, 1, 0).reshape(rows, COLUMNS, size, size).transpose(0, 2, 1, 3).reshape(rows * size, -1)
    TEXTURES_DIR.mkdir(parents=True, exist_ok=True)
    path = TEXTURES_DIR / name
    Image.fromarray(np.round(image * 255.0).astype(np.uint8)).save(path)
    return path


def build() -> list[Path]:
    rng = np.random.default_rng(7)
    return [_save(_cells(rng), "fat_cells.png"), _save(_noise(rng), "tissue_noise.png")]
