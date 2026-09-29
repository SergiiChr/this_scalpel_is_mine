# This Scalpel Is Mine

Co-op roguelike surgery thriller. Two surgeons, one patient, not enough hands.
Inspired by Trauma Center, with Overcooked-style pressure and random surgeon and patient quirks every run.

Built with Godot 4.7 (GDScript, Forward+ renderer).

## Install

Everything goes through one script, `build.sh`. It's written for Fedora and works on any x86_64 Linux.
On Fedora it installs missing system packages itself with `dnf` (it asks for your password); elsewhere it tells you what to install.

### Just play

Needs: `git`, `curl`, `unzip`, a GPU with Vulkan drivers (or run with `--rendering-method gl_compatibility`).

```bash
git clone https://github.com/SergiiChr/this_scalpel_is_mine.git
cd this_scalpel_is_mine
./build.sh play
```

The first run downloads Godot 4.7.2 (about 60 MB) into `.tools/`, then starts the game.
Already have Godot 4.7.2? `GODOT_BIN=/path/to/godot ./build.sh play`.

### Develop

Needs everything above plus Python 3.11 (`python3.11` on Fedora) and Xvfb (`xorg-x11-server-Xvfb`, for the screenshot tool).

```bash
./build.sh dev      # one-time setup, see below
./build.sh setup    # only Godot and the project import, no sudo (enough to test and take screenshots)
./build.sh editor   # open the Godot editor
./build.sh test     # run every automated test (about 5 minutes)
./build.sh build    # run the tests, then export build/ThisScalpelIsMine.x86_64
./build.sh assets   # regenerate models and sounds
./build.sh lint     # ruff and strict mypy on the Python tools
```

`./build.sh dev` does, in order:

1. Installs missing system packages: `git`, `curl`, `unzip`, `python3.11`, `xorg-x11-server-Xvfb`.
2. Downloads Godot 4.7.2 into `.tools/` and the Linux export templates into `~/.local/share/godot/` (about 1 GB, one time).
3. Creates the Python virtualenv `.venv/` and installs `requirements-dev.txt`: the asset generator's libraries, Blender 5.0 as a Python module (`bpy`, about 360 MB), ruff and mypy.
4. Imports the project so the editor opens straight away.

It's safe to run again; it only fetches what's missing.

## Assets

Every model and sound is generated from code; nothing is downloaded or made by hand.

- `tools/assetgen`: props, instruments and sounds (lofted and lathed meshes exported as `.glb`, sounds synthesized from noise, oscillators and filters).
- `tools/blender`: organic and rigged models (patient, gloves, organs) built with Blender's Python module.
  It renders review sheets to `build/blender_review/`.

The generated files are committed, so you only need `./build.sh assets` after changing a generator.

## Sources of truth

Design lives in plain files; the game reads them at startup, so change the file, not the code.

| What | Where |
|---|---|
| Patient and surgeon quirks (effects, icons, flavor) | [data/quirks/patient_quirks.md](data/quirks/patient_quirks.md), [data/quirks/surgeon_quirks.md](data/quirks/surgeon_quirks.md) |
| Scenarios (patient, wounds, targets, tools, objectives) | [data/scenarios/](data/scenarios) |
| Tools, drugs, random events, run modifiers | [data/tools.cfg](data/tools.cfg), [data/drugs.cfg](data/drugs.cfg), [data/events.cfg](data/events.cfg), [data/run_modifiers.cfg](data/run_modifiers.cfg) |
| Scoring and post-op consequences | [data/scoring.cfg](data/scoring.cfg), [data/consequences.cfg](data/consequences.cfg) |
| Surgical sites on the body | [data/patient_sites.json](data/patient_sites.json) |
| Patient dialogue, sounds | [data/dialogue/](data/dialogue), [data/audio.cfg](data/audio.cfg) |
| In-game manual | [data/manual/](data/manual) |
| Tested cases | [tests/TEST_CASES.md](tests/TEST_CASES.md) |
| Model names, parts and bones the game expects | [assets/models/README.md](assets/models/README.md) |
| Architecture, data formats, mechanics list | [docs/DESIGN.md](docs/DESIGN.md) |

## Multiplayer

Host-client over ENet, default port **24565** (UDP).
The host picks the scenario and keeps the progress save.
The other player joins with the host's IP from **Multiplayer**, and can save it as a known host.
Over the internet the host needs to forward UDP 24565 (or pick another port in the menu).

## Controls

Shown in the bottom right corner in game, for what you're doing right now (they change while you hold a hand key
or lower a tool). Everything can be rebound in **Settings**.

| Action | Default |
|---|---|
| Look around | Mouse |
| Move left / right hand (that hand becomes the active one) | Hold Q / Hold E |
| Lower the active hand's tool onto its spot | Hold LMB |
| Tool action: pinch / let go, strike, tighten, place graft, charge and shock | RMB |
| Effort level 0-3 of a lowered tool: cut depth, stitch tension, heat, saw speed, suction, gauze pressure, syringe plunger | Mouse wheel |
| Zoom (while the tool isn't lowered) | Mouse wheel |
| Grab / release the highlighted tool (near a partner's empty hand: pass the tool) | G |
| Interact (manual, card, nurse, IV, sink, sanitizer, lab, X-ray cart, turn patient) | F |
| Lift hand over hands and tall tools; pull slowly on what it holds | Hold Shift |
| Crouch (reach the floor, step over the IV line) | Hold Ctrl |
| Hold breath (steady hands) | Hold Space |
| Tilt / rotate tool | R, T / C, V |
| Drink / wear | H |
| Belt slots | 1-4 |
| Move | WASD |

The aim shows where the tool works: a dot for point tools, a line along a blade's edge. A blade only cuts along that
line (rotate the tool to turn it); moving it sideways just drags it. Drugs come in labelled vials and syringes are
unlabelled: lower the needle into a vial and the plunger draws, lower it into the patient and it pushes. The dose is
given when the needle comes out. Work out the dose from the patient's weight and the manual. Don't drop syringes.

The game never tells you what to do next: the manual on the shelf is the reference. **Settings > Debug mode**
shows what the game tracks behind the scenes (the scenario's steps, the score and every scored action).

## Project layout

```
data/            Everything designers edit. The game reads these at startup.
  quirks/        Patient and surgeon quirk sheets (markdown, source of truth)
  scenarios/     One file per scenario
  manual/        In-game manual pages (BBCode, plus "## " headings and "> " hand written notes)
  tools.cfg, drugs.cfg, events.cfg, scoring.cfg, consequences.cfg, audio.cfg, dialogue/
assets/          Art and sound, organized for review and replacement
  icons/         Quirk icons (Pip-Boy style SVG), app icon
  manual/        Manual paper, frame and diagrams (generated by tools/assetgen/manual.py)
  fonts/         Manual fonts: IM FELL English and La Belle Aurore (SIL Open Font License, see OFL-*.txt)
  shaders/       Shading, outline, skin damage, fat and muscle layers, flesh, screen grime
  models/        Generated models (see README there)
  audio/         Generated sounds (see README there)
scenes/          Scene files (menus, surgery)
src/             Code, see docs/DESIGN.md for the architecture
tests/           Headless smoke test, network test, screenshot tool
tools/assetgen/  Model, sound and manual art generator (Python)
tools/blender/   Organic and rigged models built with Blender (Python)
```

## Tests

```bash
./build.sh test    # import, tissue, every scenario, two-process co-op; fails on any error
./build.sh shots   # screenshots of a scenario in a virtual display: ./build.sh shots [scenario] [out dir]
```

Every tested case is listed in [tests/TEST_CASES.md](tests/TEST_CASES.md). `./build.sh` runs the tests before exporting, and CI runs them on every push and pull request.

See [docs/DESIGN.md](docs/DESIGN.md) for architecture, data formats and the mechanics list.
