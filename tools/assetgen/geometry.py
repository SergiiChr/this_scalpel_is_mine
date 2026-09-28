"""Mesh building blocks. Units are meters, Y is up, tools point along -Z."""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass, field
from typing import cast

import numpy as np
import trimesh
from numpy.typing import NDArray
from shapely.geometry import Polygon

Vec = NDArray[np.float64]


@dataclass
class Part:
    """One node in an exported model. Vertices are relative to `pivot`, so rotating the node pivots there."""

    name: str
    mesh: trimesh.Trimesh
    material: str
    pivot: tuple[float, float, float] = (0.0, 0.0, 0.0)
    parent: str | None = None


@dataclass
class Model:
    """A named set of parts that becomes one .glb file."""

    category: str
    name: str
    parts: list[Part] = field(default_factory=list)

    def add(self, name: str, mesh: trimesh.Trimesh, material: str, pivot: Sequence[float] = (0, 0, 0), parent: str | None = None) -> Part:
        """Adds a part. `mesh` and `pivot` are in model space; the node origin sits on the pivot."""
        world = np.asarray(pivot, float)
        parent_world = np.asarray(self.world_pivot(parent)) if parent else np.zeros(3)
        local = world - parent_world
        part = Part(name, moved(mesh, -world), material, (float(local[0]), float(local[1]), float(local[2])), parent)
        self.parts.append(part)
        return part

    def world_pivot(self, name: str) -> tuple[float, float, float]:
        """Model-space position of a part's node origin."""
        part = next(p for p in self.parts if p.name == name)
        base = np.array(part.pivot)
        if part.parent is not None:
            base = base + np.array(self.world_pivot(part.parent))
        return (float(base[0]), float(base[1]), float(base[2]))


def finish(mesh: trimesh.Trimesh) -> trimesh.Trimesh:
    """Welds seams, drops degenerate pole triangles and makes sure normals point outward."""
    mesh.merge_vertices()
    mesh.update_faces(mesh.nondegenerate_faces())
    mesh.remove_unreferenced_vertices()
    mesh.fix_normals()
    if mesh.volume < 0:
        mesh.invert()
    return mesh


def merge(*meshes: trimesh.Trimesh) -> trimesh.Trimesh:
    """Concatenates meshes that share a material."""
    return cast(trimesh.Trimesh, trimesh.util.concatenate(list(meshes)))


def _catmull_rom(points: NDArray[np.float64], samples: int) -> NDArray[np.float64]:
    """Smooth curve through control rows (any column count), `samples` points per span."""
    padded = np.vstack([points[0], points, points[-1]])
    out = []
    for i in range(1, len(padded) - 2):
        p0, p1, p2, p3 = padded[i - 1], padded[i], padded[i + 1], padded[i + 2]
        for t in np.linspace(0.0, 1.0, samples, endpoint=False):
            t2, t3 = t * t, t * t * t
            out.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3))
    out.append(points[-1])
    return np.array(out)


def tube(
    path: Sequence[Sequence[float]],
    radii: Sequence[Sequence[float]],
    ring: int = 24,
    exponent: float = 2.0,
    up: Sequence[float] = (0.0, 1.0, 0.0),
    smooth: int = 6,
    caps: bool = True,
) -> trimesh.Trimesh:
    """Lofts superellipse cross sections along a smoothed path.

    radii rows are (half height along `up`, half width). exponent 2 is an ellipse, higher is boxier.
    """
    rows = np.hstack([np.asarray(path, float), np.asarray(radii, float)])
    rows = _catmull_rom(rows, smooth) if smooth > 1 and len(rows) > 2 else rows
    centers, sizes = rows[:, :3], rows[:, 3:]
    up_hint = np.asarray(up, float)
    angles = np.linspace(0.0, 2 * np.pi, ring, endpoint=False)
    cos, sin = np.cos(angles), np.sin(angles)
    power = 2.0 / exponent
    shape_c = np.sign(cos) * np.abs(cos) ** power
    shape_s = np.sign(sin) * np.abs(sin) ** power
    vertices = []
    for i, center in enumerate(centers):
        tangent = centers[min(i + 1, len(centers) - 1)] - centers[max(i - 1, 0)]
        tangent /= np.linalg.norm(tangent)
        normal = up_hint - tangent * np.dot(up_hint, tangent)
        if np.linalg.norm(normal) < 1e-6:
            normal = np.array([1.0, 0.0, 0.0]) - tangent * tangent[0]
        normal /= np.linalg.norm(normal)
        binormal = np.cross(tangent, normal)
        for c, s in zip(shape_c, shape_s, strict=True):
            vertices.append(center + normal * sizes[i, 0] * c + binormal * sizes[i, 1] * s)
    faces = []
    for i in range(len(centers) - 1):
        for j in range(ring):
            a, b = i * ring + j, i * ring + (j + 1) % ring
            c, d = a + ring, b + ring
            faces += [[a, c, b], [b, c, d]]
    verts = np.array(vertices)
    if caps:
        start, end = len(verts), len(verts) + 1
        verts = np.vstack([verts, centers[0], centers[-1]])
        last = (len(centers) - 1) * ring
        for j in range(ring):
            faces.append([start, j, (j + 1) % ring])
            faces.append([end, last + (j + 1) % ring, last + j])
    return finish(trimesh.Trimesh(verts, np.array(faces), process=True))


def lathe(profile: Sequence[tuple[float, float]], segments: int = 32) -> trimesh.Trimesh:
    """Revolves (radius, height) points around +Y. Profiles should start and end on the axis to be closed."""
    pts = np.asarray(profile, float)
    angles = np.linspace(0.0, 2 * np.pi, segments, endpoint=False)
    verts = np.array([[r * np.cos(a), y, r * np.sin(a)] for r, y in pts for a in angles])
    faces = []
    for i in range(len(pts) - 1):
        for j in range(segments):
            a, b = i * segments + j, i * segments + (j + 1) % segments
            c, d = a + segments, b + segments
            faces += [[a, b, c], [b, d, c]]
    return finish(trimesh.Trimesh(verts, np.array(faces), process=True))


def superellipsoid(size: Sequence[float], roundness: float = 0.25, center: Sequence[float] = (0, 0, 0), detail: int = 20) -> trimesh.Trimesh:
    """Rounded box (roundness near 0) through ellipsoid (1). size is the full extent."""
    u = np.linspace(-np.pi / 2, np.pi / 2, detail)
    v = np.linspace(-np.pi, np.pi, detail * 2, endpoint=False)

    def f(w: NDArray[np.float64], m: float) -> NDArray[np.float64]:
        return np.asarray(np.sign(w) * np.abs(w) ** m, dtype=np.float64)

    verts = []
    for a in u:
        for b in v:
            verts.append([f(np.cos(a), roundness) * f(np.cos(b), roundness), f(np.sin(a), roundness), f(np.cos(a), roundness) * f(np.sin(b), roundness)])
    verts_arr = np.array(verts, float) * (np.asarray(size, float) / 2.0) + np.asarray(center, float)
    ring = len(v)
    faces = []
    for i in range(len(u) - 1):
        for j in range(ring):
            a, b = i * ring + j, i * ring + (j + 1) % ring
            c, d = a + ring, b + ring
            faces += [[a, b, c], [b, d, c]]
    return finish(trimesh.Trimesh(verts_arr, np.array(faces), process=True))


def ellipsoid(radii: Sequence[float], center: Sequence[float] = (0, 0, 0), subdivisions: int = 3) -> trimesh.Trimesh:
    mesh = cast(trimesh.Trimesh, trimesh.creation.icosphere(subdivisions=subdivisions))
    mesh.apply_scale(radii)
    mesh.apply_translation(center)
    return mesh


def cylinder(radius: float, a: Sequence[float], b: Sequence[float], sections: int = 20) -> trimesh.Trimesh:
    """Cylinder between two points."""
    return cast(trimesh.Trimesh, trimesh.creation.cylinder(radius=radius, segment=np.array([a, b], float), sections=sections))


def extrude(outline: Sequence[tuple[float, float]], depth: float, faceted: bool = True) -> trimesh.Trimesh:
    """Extrudes an XY outline along Z, centered on Z=0. Faceted keeps edges crisp (blades, plates)."""
    mesh = trimesh.creation.extrude_polygon(Polygon(outline), depth)
    mesh.apply_translation([0, 0, -depth / 2])
    if faceted:
        mesh.unmerge_vertices()
    return mesh


def torus(major: float, minor: float, center: Sequence[float] = (0, 0, 0), axis: str = "y") -> trimesh.Trimesh:
    mesh = cast(trimesh.Trimesh, trimesh.creation.torus(major_radius=major, minor_radius=minor, major_sections=32, minor_sections=12))
    if axis == "x":
        mesh.apply_transform(trimesh.transformations.rotation_matrix(np.pi / 2, [0, 1, 0]))
    elif axis == "y":
        mesh.apply_transform(trimesh.transformations.rotation_matrix(np.pi / 2, [1, 0, 0]))
    mesh.apply_translation(center)
    return mesh


def rotated(mesh: trimesh.Trimesh, angle: float, axis: Sequence[float], about: Sequence[float] = (0, 0, 0)) -> trimesh.Trimesh:
    out = mesh.copy()
    out.apply_transform(trimesh.transformations.rotation_matrix(angle, axis, about))
    return out


def moved(mesh: trimesh.Trimesh, offset: Sequence[float] | Vec) -> trimesh.Trimesh:
    out = mesh.copy()
    out.apply_translation(offset)
    return out


def scaled(mesh: trimesh.Trimesh, factors: Sequence[float]) -> trimesh.Trimesh:
    out = mesh.copy()
    out.apply_scale(factors)
    return out


def bumpy(mesh: trimesh.Trimesh, amount: float, seed: int, frequency: float = 40.0) -> trimesh.Trimesh:
    """Organic lumps: displaces vertices along normals with smooth pseudo-noise."""
    out = mesh.copy()
    rng = np.random.default_rng(seed)
    phases = rng.uniform(0, 2 * np.pi, (3, 3))
    v = out.vertices
    noise = sum(np.sin(v @ rng.normal(size=3) * frequency + phases[i, 0]) for i in range(3)) / 3.0
    out.vertices = v + out.vertex_normals * (noise[:, None] * amount)
    return out
