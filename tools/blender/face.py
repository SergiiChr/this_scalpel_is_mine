"""The patient's head: base volumes, ears and eyelids as shells to fuse, then sculpt strokes for the fine forms.

Head frame (game space, patient lying face up): x runs from chin (0.555) to crown (0.795), y is out of the face,
z is the patient's left. Proportions follow the classic thirds: hairline 0.755, brow 0.69, nose base 0.625, chin 0.56.
"""

from __future__ import annotations

import bpy
import numpy as np
from numpy.typing import NDArray

from . import scene
from .scene import Blob, Stroke, Vec3, capsule, dab, ellipsoid, line

EYE_CENTER: Vec3 = (0.675, 0.0845, 0.032)
EYE_RADIUS = 0.0118


def _m(p: Vec3, side: float) -> Vec3:
    return (p[0], p[1], p[2] * side)


def base() -> list[Blob]:
    """Large volumes only: skull, face mask, jaw, chin, nose, cheekbones. Details come from strokes."""
    elements = [
        ellipsoid((0.7, 0.0, 0.0), (0.092, 0.1, 0.076)),
        # Parietal dome keeps the crown round instead of egg-pointed; the frontal bone keeps the forehead upright.
        ellipsoid((0.735, -0.02, 0.0), (0.058, 0.085, 0.074)),
        ellipsoid((0.722, 0.058, 0.0), (0.04, 0.04, 0.058)),
        ellipsoid((0.648, 0.05, 0.0), (0.06, 0.05, 0.056)),
        ellipsoid((0.598, 0.045, 0.0), (0.036, 0.05, 0.05)),
        ellipsoid((0.569, 0.083, 0.0), (0.019, 0.021, 0.025), 2.0),
        # Nose: bridge, then the tip.
        capsule((0.684, 0.098, 0.0), (0.64, 0.118, 0.0), 0.0072, 3.0),
        ellipsoid((0.634, 0.119, 0.0), (0.009, 0.0085, 0.0095), 3.0),
        # Muzzle: the teeth and lips push the mouth area forward.
        ellipsoid((0.607, 0.095, 0.0), (0.02, 0.017, 0.025), 2.5),
    ]
    for side in (1.0, -1.0):
        elements += [
            # Chewing muscle fills out the side of the jaw.
            ellipsoid(_m((0.607, 0.03, 0.049), side), (0.03, 0.028, 0.014), 2.0),
            # Nostril holes, opening toward the chin, and the passage running up into the nose.
            ellipsoid(_m((0.624, 0.111, 0.0075), side), (0.004, 0.0035, 0.003), -2.0),
            capsule(_m((0.624, 0.111, 0.0075), side), _m((0.644, 0.107, 0.0045), side), 0.0024, -2.5),
            # The eye sits in a socket: carve the space the eyeball and lids fill.
            ellipsoid(_m((0.675, 0.098, 0.032), side), (0.0085, 0.006, 0.0125), -1.5),
        ]
    return elements


def eyelid_shells() -> list[bpy.types.Object]:
    """Upper and lower lids as rims wrapped over each eyeball, leaving an almond-shaped opening."""
    shells = []
    for side in (1.0, -1.0):
        c = np.array(_m(EYE_CENTER, side))
        for name, rise, radius in (("Upper", 1.0, 0.0028), ("Lower", -0.75, 0.0018)):
            arc: list[Vec3] = []
            for t in np.linspace(0.0, 1.0, 9):
                # Inner corner sits lower and further forward than the outer one.
                dz = (-0.016 + 0.032 * t) * side
                dx = rise * 0.0058 * np.sin(np.pi * t) + 0.0008 - 0.0016 * t
                dy = np.sqrt(max((EYE_RADIUS + 0.0012) ** 2 - dz**2 - dx**2, 0.0))
                arc.append((float(c[0] + dx), float(c[1] + dy), float(c[2] + dz)))
            shells.append(scene.curve_tube(f"{name}Lid{side}", arc, radius, [0.5, 0.9, 1.0, 1.0, 1.0, 1.0, 1.0, 0.9, 0.5]))
    return shells


def mouth_shells() -> list[bpy.types.Object]:
    """Nose wings as rolled rims, like the eyelids: crisp borders a sculpt stroke can't make."""
    shells = []
    for side in (1.0, -1.0):
        wing = [_m(p, side) for p in ((0.631, 0.115, 0.006), (0.633, 0.111, 0.0135), (0.628, 0.104, 0.0165), (0.622, 0.1, 0.0135), (0.621, 0.103, 0.009))]
        shells.append(scene.curve_tube(f"NoseWing{side}", wing, 0.0034, [0.6, 1.0, 1.0, 0.9, 0.6]))
    return shells


def ear_shells() -> list[bpy.types.Object]:
    """Each ear: a flat base, the curled helix around its rim, the antihelix inside and a soft lobe.
    Ear-local (up, back) coordinates map onto the side of the head, tilted back a little."""
    shells = []
    for side in (1.0, -1.0):

        def at(up: float, back: float, out: float = 0.0, side: float = side) -> Vec3:
            # The ear stands off the skull more at its back edge.
            return (0.668 + up, 0.004 - back, (0.075 + out + 0.5 * max(back, 0.0)) * side)

        # Base of the ear plus the cup behind it that ties it to the skull.
        base = [ellipsoid(at(0.003, 0.007, 0.004), (0.028, 0.019, 0.0055), 2.5), ellipsoid(at(0.0, 0.008, -0.003), (0.022, 0.014, 0.008), 2.0)]
        shells.append(scene.blobs(f"EarBase{side}", base, resolution=0.0012))
        helix = [
            at(-0.004, -0.005, 0.004),
            at(0.016, -0.006, 0.005),
            at(0.029, 0.0, 0.006),
            at(0.026, 0.013, 0.007),
            at(0.012, 0.02, 0.007),
            at(-0.006, 0.019, 0.006),
            at(-0.019, 0.011, 0.005),
        ]
        shells.append(scene.curve_tube(f"Helix{side}", helix, 0.0026, [0.6, 0.9, 1.0, 1.0, 1.0, 0.9, 0.8]))
        anti = [at(0.018, 0.001, 0.006), at(0.012, 0.009, 0.006), at(0.0, 0.012, 0.005), at(-0.01, 0.008, 0.005)]
        shells.append(scene.curve_tube(f"Antihelix{side}", anti, 0.0022, [0.7, 1.0, 1.0, 0.8]))
        shells.append(scene.blobs(f"Lobe{side}", [ellipsoid(at(-0.024, 0.006, 0.004), (0.009, 0.007, 0.0035), 2.5)], resolution=0.0012))
        shells.append(scene.curve_tube(f"Tragus{side}", [at(-0.004, -0.007, 0.004), at(0.002, -0.008, 0.005)], 0.0028))
    return shells


def strokes() -> list[Stroke]:
    """Sculpt passes, big forms first. Amounts are meters along the surface normal (negative carves)."""
    s: list[Stroke] = [
        # Forehead: flatter and more upright, a soft brow ridge above the eyes.
        line((0.692, 0.097, -0.04), (0.692, 0.097, 0.04), 0.01, 0.0015),
        dab((0.688, 0.1, 0.0), (0.006, 0.006, 0.008), -0.002),
        # Nose: narrow bridge, defined tip, wings with a crease around them, nostrils underneath.
        line((0.672, 0.105, 0.0), (0.648, 0.116, 0.0), 0.005, 0.0012),
        dab((0.63, 0.119, 0.0), (0.004, 0.004, 0.004), -0.0035),
        # Philtrum: two ridges from nose to lip with a groove between, and the cupid's bow.
        line((0.624, 0.107, 0.0), (0.614, 0.109, 0.0), 0.0025, -0.001),
        # The mouth line where the lips meet.
        line((0.6068, 0.109, -0.02), (0.6068, 0.109, 0.02), 0.001, -0.0012),
        # A small dip under the lower lip.
        line((0.59, 0.101, -0.012), (0.59, 0.101, 0.012), 0.004, -0.0008),
        # Chin: slight cleft, square jaw corners, a clean jawline.
        dab((0.566, 0.101, 0.0), (0.004, 0.003, 0.004), -0.001),
    ]
    for side in (1.0, -1.0):
        s += [
            # Temples sink in, cheeks hollow under the cheekbones.
            dab(_m((0.7, 0.03, 0.072), side), (0.02, 0.025, 0.015), -0.002),
            dab(_m((0.628, 0.068, 0.05), side), (0.015, 0.014, 0.014), -0.003),
            line(_m((0.662, 0.074, 0.036), side), _m((0.657, 0.05, 0.066), side), 0.008, 0.003),
            # The crease around each nose wing.
            line(_m((0.636, 0.106, 0.02), side), _m((0.623, 0.101, 0.02), side), 0.0015, -0.0012),
            # Philtrum ridges.
            line(_m((0.624, 0.108, 0.0045), side), _m((0.614, 0.11, 0.005), side), 0.0018, 0.0008),
            # Nasolabial folds from the nose wing down past the mouth corner.
            line(_m((0.626, 0.1, 0.022), side), _m((0.602, 0.093, 0.03), side), 0.003, -0.0006),
            line(_m((0.626, 0.096, 0.028), side), _m((0.602, 0.089, 0.035), side), 0.005, 0.0005),
            # Mouth corners tuck in.
            dab(_m((0.606, 0.101, 0.024), side), (0.0025, 0.0025, 0.0025), -0.0008),
            # Upper eyelid crease and the bag under the eye.
            line(_m((0.683, 0.098, 0.02), side), _m((0.684, 0.094, 0.046), side), 0.0018, -0.0008),
            line(_m((0.664, 0.096, 0.022), side), _m((0.662, 0.091, 0.044), side), 0.003, 0.0005),
            # Jaw angle and the line under the jaw.
            dab(_m((0.588, 0.012, 0.053), side), (0.01, 0.012, 0.007), 0.0015),
            # The neck meets the jaw in a crisp line, not a double chin.
            line(_m((0.568, 0.01, 0.045), side), _m((0.552, 0.06, 0.015), side), 0.007, -0.002),
            # Ear: bowl of the concha behind the canal.
            dab(_m((0.664, 0.004, 0.082), side), (0.008, 0.007, 0.004), -0.003),
        ]
    return s


def eyebrows(head: bpy.types.Object) -> bpy.types.Object:
    """Short, thick brows lying on the brow ridge: fuller at the inner end, thinning toward the temple."""
    parts = []
    for side in (1.0, -1.0):
        arc = [_m(p, side) for p in ((0.688, 0.104, 0.011), (0.691, 0.103, 0.023), (0.692, 0.099, 0.034), (0.689, 0.093, 0.044))]
        path = scene.on_surface(head, arc, -0.0003)
        parts.append(scene.curve_tube(f"Brow{side}", path, 0.0029, list(np.linspace(1.0, 0.5, len(path))), resolution=8))
    brows = scene.join("Brows", *parts)
    return scene.finish(scene.displace(brows, 0.0004, 0.0012, detail=3, seed=51), scene.material("hair", (0.12, 0.09, 0.07), roughness=0.8))


# The mouth: a slit between the lips into a cavity, with teeth and a tongue. Everything below the line from the
# mouth corners back to the jaw hinge follows the Jaw bone, so the mouth opens.
MOUTH_LINE_X = 0.6068
JAW_HINGE: Vec3 = (0.645, 0.025, 0.0)
CAVITY: tuple[Vec3, Vec3] = ((0.606, 0.088, 0.0), (0.012, 0.019, 0.023))


def carve_mouth(head: bpy.types.Object) -> bpy.types.Object:
    """Cuts the slit between the lips and hollows out the mouth behind it (lined with the dark mouth material)."""
    cavity = scene.blobs("Cavity", [ellipsoid(*CAVITY, 2.0)], resolution=0.001)
    slit = scene.box("Slit", (MOUTH_LINE_X, 0.103, 0.0), (0.0013, 0.022, 0.04))
    head = scene.subtract(scene.subtract(head, slit, fast=True), cavity, fast=True)
    center, size = np.array(CAVITY[0]), np.array(CAVITY[1]) * 1.03

    def inside(p: NDArray[np.float64]) -> NDArray[np.bool_]:
        # The cavity walls plus the two faces of the slit, but none of the skin around the lips.
        cavity_wall = np.sum(((p - center) / size) ** 2, axis=1) < 1.0
        slit_wall = (np.abs(p[:, 0] - MOUTH_LINE_X) < 0.0009) & (np.abs(p[:, 2]) < 0.02) & (p[:, 1] < 0.11)
        return np.asarray(cavity_wall | slit_wall, dtype=np.bool_)

    return scene.assign(head, scene.material("mouth", (0.35, 0.08, 0.09), roughness=0.3, subsurface=0.1), inside)


def jaw_weight(p: NDArray[np.float64]) -> NDArray[np.float64]:
    """How much each point follows the jaw (0..1): hard at the lips so they part cleanly, softer toward the hinge."""
    x, y, z = p[:, 0], p[:, 1], p[:, 2]
    depth = np.clip((0.109 - y) / (0.109 - JAW_HINGE[1]), 0.0, 1.0)
    boundary = MOUTH_LINE_X + depth * (JAW_HINGE[0] - MOUTH_LINE_X)
    # Sharp only across the slit between the lips; past the mouth corners the cheek stretches over a wide blend.
    at_slit = np.clip((0.021 - np.abs(z)) / 0.002, 0.0, 1.0) * np.clip((y - 0.09) / 0.004, 0.0, 1.0)
    width = at_slit * 0.0008 + (1.0 - at_slit) * (0.01 + 0.012 * depth)
    below = np.clip((boundary - x) / width + 0.5, 0.0, 1.0)
    # Only the face: not the ears, not the back of the head, fading into the neck under the chin.
    face = np.clip((0.068 - np.abs(z)) / 0.012, 0.0, 1.0) * np.clip((y - 0.005) / 0.02, 0.0, 1.0) * np.clip((x - 0.54) / 0.03, 0.0, 1.0)
    return np.asarray(below * face, dtype=np.float64)


def teeth_and_tongue() -> tuple[bpy.types.Object, bpy.types.Object, bpy.types.Object]:
    """Upper teeth (on the head), lower teeth and tongue (on the jaw), just behind the lips."""
    rows = []
    for name, x, height in (("UpperTeeth", 0.6095, 0.009), ("LowerTeeth", 0.6025, 0.008)):
        z = np.linspace(-0.021, 0.021, 15)
        arch: list[Vec3] = [(x, float(0.0995 - 32.0 * v * v), float(v)) for v in z]
        row = scene.curve_tube(name, arch, height * 0.5, [0.6, *([1.0] * 13), 0.6], resolution=8)
        # A shallow groove between each tooth.
        row = scene.sculpt(row, [dab((x, 0.0995 - 32.0 * v * v + 0.003, float(v)), (0.006, 0.003, 0.0008), -0.0006) for v in np.linspace(-0.0195, 0.0195, 12)])
        rows.append(scene.finish(row, scene.material("teeth", (0.86, 0.83, 0.72), roughness=0.3)))
    tongue = scene.blobs("Tongue", [ellipsoid((0.6, 0.083, 0.0), (0.006, 0.014, 0.016), 2.5)], resolution=0.001)
    scene.finish(tongue, scene.material("tongue", (0.62, 0.26, 0.28), roughness=0.35, subsurface=0.2))
    return rows[0], rows[1], tongue


def eyes() -> tuple[bpy.types.Object, bpy.types.Object, bpy.types.Object]:
    """EyeL and EyeR: separate objects (eyeball plus iris) with their origin at the eye's center so they can look around.
    Lids: the closed eyelids, shown while unconscious."""
    out = []
    for side, suffix in ((1.0, "L"), (-1.0, "R")):
        c = _m(EYE_CENTER, side)
        ball = scene.finish(
            scene.blobs(f"Eye{suffix}", [ellipsoid(c, (EYE_RADIUS,) * 3, 3.0)], resolution=0.0008), scene.material("eye", (0.9, 0.88, 0.84), roughness=0.08)
        )
        iris = scene.blobs(
            f"Iris{suffix}", [ellipsoid((c[0] + 0.0004, c[1] + EYE_RADIUS * 0.9, c[2] - 0.0006 * side), (0.0055, 0.0022, 0.0055), 3.0)], resolution=0.0004
        )
        scene.finish(iris, scene.material("iris", (0.2, 0.24, 0.16), roughness=0.08))
        eye = scene.join(f"Eye{suffix}", ball, iris)
        scene.set_origin(eye, c)
        out.append(eye)
    lids = [
        ellipsoid((c[0] + 0.0005, c[1] + 0.0008, c[2]), (EYE_RADIUS * 1.02, EYE_RADIUS * 1.03, EYE_RADIUS * 1.28), 3.0)
        for c in (_m(EYE_CENTER, 1.0), _m(EYE_CENTER, -1.0))
    ]
    lid = scene.cut_below(scene.blobs("Lids", lids, resolution=0.0008), 1, EYE_CENTER[1] + 0.001)
    scene.finish(lid, scene.material("skin", (0.8, 0.6, 0.5), roughness=0.55, subsurface=0.2))
    return out[0], out[1], lid


def ear_canals() -> list[bpy.types.Object]:
    """Cutters for the ear canals, from the bowl of each ear into the head."""
    return [
        scene.curve_tube(f"EarCanal{side}", [_m((0.664, 0.003, 0.095), side), _m((0.666, 0.001, 0.078), side), _m((0.668, 0.0, 0.062), side)], 0.0032)
        for side in (1.0, -1.0)
    ]
