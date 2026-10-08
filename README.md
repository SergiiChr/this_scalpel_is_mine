# This Scalpel Is Mine

Co-op roguelike surgery thriller. Two surgeons, one patient, not enough hands.
Inspired by Trauma Center, with Overcooked-style pressure and random surgeon and patient quirks every run.

Built with Godot 4.7 .NET (C#, Forward+ renderer).

## Install

Everything goes through one script, `build.py` (Python 3, standard library only). It's written for Fedora and works on
any x86_64 Linux.
On Fedora it installs missing system packages itself with `dnf` (it asks for your password); elsewhere it tells you what to install.

### Just play

Needs: `git`, Python 3, a GPU with Vulkan drivers (or run with `--rendering-method gl_compatibility`).

```bash
git clone https://github.com/SergiiChr/this_scalpel_is_mine.git
cd this_scalpel_is_mine
./build.py play
```

The first run downloads Godot 4.7.2 .NET (about 70 MB) into `.tools/`, and the .NET 10 SDK too unless you have it, builds
the C# and starts the game.
Already have Godot 4.7.2 .NET? `GODOT_BIN=/path/to/godot ./build.py play`.

### Develop

Needs everything above plus Python 3.11 (`python3.11` on Fedora) and Xvfb (`xorg-x11-server-Xvfb`, for the screenshot tool
and the visual tests). Any C# IDE works; the repository has Rider and VS Code test settings (`.runsettings`).

```bash
./build.py dev      # one-time setup, see below
./build.py setup    # only Godot, the .NET SDK, the build and the import, no sudo (enough to test and take screenshots)
./build.py editor   # open the Godot editor
./build.py test     # the smoke tests; --all for the full regression
./build.py build    # run the tests, then export build/ThisScalpelIsMine.x86_64
./build.py assets   # regenerate models and sounds
./build.py lint     # C# analyzers and dotnet format, ruff and strict mypy on the Python tools
```

`./build.py dev` does, in order:

1. Installs missing system packages: `git`, `python3.11`, `xorg-x11-server-Xvfb`.
2. Downloads Godot 4.7.2 .NET into `.tools/` (and the .NET 10 SDK into `.tools/dotnet` when the system has none) and the
   Linux export templates into `~/.local/share/godot/` (about 1 GB, one time).
3. Creates the Python virtualenv `.venv/` and installs `requirements-dev.txt`: the asset generator's libraries, Blender 5.0 as a Python module (`bpy`, about 360 MB), ruff and mypy.
4. Builds the C# and imports the project so the editor opens straight away.

It's safe to run again; it only fetches what's missing.

## Assets

Every model and sound is generated from code; nothing is downloaded or made by hand.

- `tools/assetgen`: props, instruments and sounds (lofted and lathed meshes exported as `.glb`, sounds synthesized from noise, oscillators and filters).
- `tools/blender`: organic and rigged models (patient, gloves, organs) built with Blender's Python module.
  It renders review sheets to `build/blender_review/`.

The generated files are committed, so you only need `./build.py assets` after changing a generator.

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
| Tested cases | Executable GdUnit4 suites under [`tests/`](tests/) |
| Model names, parts and bones the game expects | [assets/models/README.md](assets/models/README.md) |
| Architecture, data formats, mechanics list | [docs/DESIGN.md](docs/DESIGN.md) |

## Multiplayer

Host-client over ENet, default port **24565** (UDP).
The host picks the scenario and keeps the progress save.
The other player joins with the host's IP from **Multiplayer**, and can save it as a known host.
Over the internet the host needs to forward UDP 24565 (or pick another port in the menu).

## Controls

Shown in the bottom right corner in game, for what you're doing right now (they change while you hold a hand key
or hold a tool). Everything can be rebound in **Settings**.

| Action | Default |
|---|---|
| Look around | Mouse |
| Move left / right hand (that hand becomes the active one) | Hold Q / Hold E |
| Use the active hand's tool: lower it onto its spot and work it (pinch / let go, strike, tighten, place graft, charge and shock) | Hold LMB |
| Effort level 0-3: cut depth, stitch tension, heat, saw speed, suction, gauze pressure, syringe plunger | Mouse wheel |
| Pick up / put down the highlighted tool (near a partner's empty hand: pass the tool) | RMB |
| Zoom, three steps | Shift |
| Interact (manual, card, nurse, IV, sink, sanitizer, lab, X-ray cart, turn patient) | F |
| Lift hand over hands and tall tools; pull slowly on what it holds | Hold Alt |
| Hold the tool up to look at it (read a syringe) | Hold X |
| Crouch (reach the floor, step over the IV line) | Hold Ctrl |
| Hold breath (steady hands) | Hold Space |
| Tilt / rotate tool | R, T / C, V |
| Drink / wear | H |
| Belt slots | 1-4 |
| Move | WASD |

The aim shows where the tool works: a dot for point tools, a line along a blade's edge. A blade only cuts along that
line (rotate the tool to turn it); moving it sideways just drags it. Drugs come in labelled vials and syringes are
unlabelled: lower the needle into a vial and the plunger draws, lower it into the patient and it pushes. Doses add
up, however many pushes they take. Work out the dose from the patient's weight and the manual. Don't drop syringes.

The game never tells you what to do next: the manual on the shelf is the reference. **Settings > Debug mode**
shows what the game tracks behind the scenes (the scenario's steps, the score and every scored action, a needle that
hit the vein, how many ml of what each syringe pushed where and each ml of a drug that runs down the IV line).

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
src/             C# code by area (Core, Data, Operation, Patients, Surgeons, Tools, UI, Visuals, World)
tests/           GdUnit4 suites by feature, and in tests/Support the test helpers and dev tools (screenshots,
                 grip fitting)
tools/assetgen/  Model, sound and manual art generator (Python)
tools/blender/   Organic and rigged models built with Blender (Python)
```

## Tests

```bash
./build.py test    # the fast headless smoke suites; fails on any error
./build.py shots   # screenshots of a scenario in a virtual display: ./build.py shots [scenario] [out dir]
```

The GdUnit4 (GdUnit4Net) suites under [`tests/`](tests/) are the test catalog and source of truth. `./build.py test`
runs the smoke category; `./build.py test --all` runs the full regression, `--with-key-frames` saves screenshots of key
moments. They also run from the IDE's test explorer. CI gates the full suite and export on smoke.

See [docs/DESIGN.md](docs/DESIGN.md) for architecture, data formats and the mechanics list.
