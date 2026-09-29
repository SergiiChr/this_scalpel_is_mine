# Models

Generated from code; run `./build.sh assets` to rebuild them all.
The game loads them through `src/visual/model_slot.gd` and re-skins materials with the game shader by material name,
so a replacement `.glb` only needs the same file name, node or bone names and material names.
`tests/models_test.gd` checks the names the game depends on, and that each model stays within its triangle budget:
patient 52k, surgeon parts 16k (the glove about 8k), organs 7k, targets 5k, tools 6k, props 15k.
The Blender generator decimates to its budgets (`BUDGETS` in `tools/blender/__main__.py` and `tools/blender/patient.py`);
Godot adds LODs on import.

| Folder | Contents | Made by | Animated (bone or node names) |
|---|---|---|---|
| `patient/` | `body.glb` (one skinned mesh), `site_heights.json` (skin heights per surgical site) | `tools/blender` | Bones: Torso, Chest, Neck, Head, Jaw, UpperArm/Forearm/Hand L/R, Thigh/Shin/Foot L/R. Parts: EyeL, EyeR, Lids |
| `surgeon/` | `glove` (skinned) | `tools/blender` | Bones: Hand, Index/Middle/Ring/Pinky/Thumb 1-3 |
| `surgeon/` | `body`, `head`, `upper_arm`, `forearm` | `tools/assetgen` | Torso, LegL/R |
| `organs/` | bowel, lobe, sac (unit radius) | `tools/blender` | |
| `targets/` | appendix, tumor, clot, bone (femur), fragment, rib, splinter, nasal_hump, skull_flap, sternum | `tools/blender` | |
| `targets/` | bullet, knife, figurine, fluid, air | `tools/assetgen` | |
| `tools/` | one per tool id or `model=` in tools.cfg | `tools/assetgen` | JawA/JawB, Plunger, Trigger, Blade, Flame, Glow, Light |
| `props/` | table, tray, IV stand, shelf, clipboard, stations, lamp, monitor, X-ray cart and print, straps, streetlight | `tools/assetgen` | X-ray `Arm` |

Model space is the game's: Y up, and for the patient +X toward the head and +Z the patient's left.
The glove is modeled wrist at the origin, fingers along +X, palm facing -Y, thumb toward -Z.

Special material names: `skin` and `gown` (cut away under the surgical site where the simulated skin layer takes over),
`tint` (recolored per instance), `flame` (unshaded glow), `organ` (wet flesh shader).
Tools: grip at the origin, tip at `(0, 0, -length)` with `length` from `data/tools.cfg`.
