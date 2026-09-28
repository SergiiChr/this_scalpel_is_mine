"""Builds the Blender models, exports them for review and renders a contact sheet per model.

Run from the repo root with Blender's Python module installed (pip install bpy, Python 3.11):
    python -m tools.blender [model ...]
Review renders go to build/blender_review/, the models themselves into assets/models/ for the game.
"""

from __future__ import annotations

import sys
from collections.abc import Callable
from pathlib import Path

from . import anatomy, hand, patient, scene

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "build" / "blender_review"
ASSETS = ROOT / "assets" / "models"


def _hand() -> list[Path]:
    rig = hand.build()
    scene.export(OUT / "glove.glb")
    scene.export(ASSETS / "surgeon" / "glove.glb")
    views = [("back", (0.0, 1.0, 0.05)), ("palm", (0.1, -1.0, 0.1)), ("thumb_side", (0.2, 0.1, -1.0)), ("three_quarter", (0.7, 0.7, -0.6))]
    shots = scene.render_views(OUT, "glove_open", views)
    hand.curl(rig, 0.85)
    return shots + scene.render_views(OUT, "glove_curled", [("thumb_side", (0.1, 0.1, -1.0)), ("front", (1.0, 0.4, -0.4))], zoom=1.3)


def _patient() -> list[Path]:
    rig = patient.build()
    scene.export(OUT / "patient.glb")
    scene.export(ASSETS / "patient" / "body.glb")
    patient.bake_site_heights()
    shots = scene.render_views(OUT, "patient", [("top", (0.0, 1.0, 0.02)), ("left_side", (0.0, 0.15, 1.0)), ("three_quarter", (-0.6, 0.8, 0.7))])
    face = ((0.655, 0.05, 0.0), 0.13)
    head_up = (1.0, 0.0, 0.0)
    shots += scene.render_views(OUT, "patient_face", [("front", (0.1, 1.0, 0.0)), ("profile", (0.05, 0.3, 1.0))], focus=face, up=head_up)
    shots += scene.render_views(OUT, "patient_hand", [("left", (0.2, 1.0, 0.5))], focus=((-0.2, -0.045, 0.26), 0.11))
    patient.show_lids(True)
    shots += scene.render_views(OUT, "patient_face_asleep", [("three_quarter", (0.2, 1.0, 0.6))], focus=face, up=head_up)
    patient.show_lids(False)
    shots += scene.render_views(OUT, "patient_shoulders", [("top", (0.0, 1.0, 0.0))], focus=((0.3, 0.0, 0.0), 0.32))
    patient.test_pose(rig)
    shots += scene.render_views(OUT, "patient_mouth_open", [("three_quarter", (0.1, 1.0, 0.45))], focus=face, up=head_up)
    return shots + scene.render_views(OUT, "patient_posed", [("three_quarter", (-0.3, 0.8, 0.8))])


TURNAROUND = [("top", (0.1, 1.0, 0.15)), ("front", (0.0, 0.25, 1.0)), ("side", (1.0, 0.25, 0.0)), ("three_quarter", (0.8, 0.7, 0.8))]


def _still(name: str, build: Callable[[], None], ship: str = "") -> Callable[[], list[Path]]:
    """A model with no rig: build, export, render it from four sides.
    ship = "category" also writes it into assets/models/<category>/ for the game (approved or brand-new models)."""

    def run() -> list[Path]:
        build()
        scene.export(OUT / f"{name}.glb")
        if ship:
            scene.export(ASSETS / ship / f"{name}.glb")
        return scene.render_views(OUT, name, TURNAROUND)

    return run


MODELS: dict[str, Callable[[], list[Path]]] = {
    "patient": _patient,
    "glove": _hand,
    "bowel": _still("bowel", anatomy.bowel, ship="organs"),
    "lobe": _still("lobe", anatomy.lobe, ship="organs"),
    "sac": _still("sac", anatomy.sac, ship="organs"),
    "appendix": _still("appendix", anatomy.appendix, ship="targets"),
    "tumor": _still("tumor", anatomy.tumor, ship="targets"),
    "clot": _still("clot", anatomy.clot, ship="targets"),
    "bone": _still("bone", anatomy.bone, ship="targets"),
    "fragment": _still("fragment", anatomy.fragment, ship="targets"),
    "rib": _still("rib", anatomy.rib, ship="targets"),
    "splinter": _still("splinter", anatomy.splinter, ship="targets"),
}


def main() -> None:
    wanted = sys.argv[1:] or list(MODELS)
    OUT.mkdir(parents=True, exist_ok=True)
    for name in wanted:
        scene.reset()
        scene.setup_render()
        shots = MODELS[name]()
        sheet = scene.contact_sheet(shots, OUT / f"{name}_sheet.png", f"{name}  ({scene.triangle_count()} triangles)")
        print(f"{name}: {sheet}")


if __name__ == "__main__":
    main()
