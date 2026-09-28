"""Builds every generated asset. Run from the repo root: python -m tools.assetgen"""

from __future__ import annotations

from . import anatomy, instruments, patient, props, sounds, surgeon
from .export import MODELS_DIR, write


def main() -> None:
    for module in (patient, surgeon, anatomy, instruments, props):
        for model in module.build():
            print("wrote", write(model).relative_to(MODELS_DIR.parent.parent))
    for path in sounds.build():
        print("wrote", path.relative_to(MODELS_DIR.parent.parent))


if __name__ == "__main__":
    main()
