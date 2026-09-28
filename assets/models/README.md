# Models

Everything in the game is primitive placeholder geometry until a model file replaces it.
Drop a `.glb`, `.gltf` or `.tscn` with the right name here and it's used automatically (`src/visual/model_slot.gd`).

| Folder | File name | Notes |
|---|---|---|
| `tools/` | `<tool id>.glb` (ids from `data/tools.cfg`) | Grip at origin, tip pointing along -Z, meters. Or set `model=` in tools.cfg. |
| `targets/` | `bullet`, `knife`, `appendix`, `tumor`, `clot`, `figurine`, `bone`, `fragment`, `fluid`, `air` | Centered on origin. |
| `patient/` | `body.glb` | Lying along +X (head at +X), origin at the body's center line. Colliders and the surgical site stay code-driven. |
| `props/` | `operating_table`, `instrument_tray`, `manual`, `card`, `bell`, `gloves`, `sanitizer`, `iv` | Origin on the floor. |
