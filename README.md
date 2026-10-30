# This Scalpel Is Mine

Co-op roguelike surgery thriller. Two surgeons, one patient, not enough hands.
Inspired by Trauma Center, with Overcooked-style pressure and random surgeon and patient quirks every run.

Built with Godot 4.7.2 .NET (C#, Forward+ renderer).

## Install

Everything goes through one script, `build.py` (Python 3, standard library only). It's written for Fedora and works on
any x86_64 Linux.
Development setup can install missing git and Python packages on Fedora with `sudo dnf`; elsewhere it tells you
what to install. Visual-test dependencies are provisioned separately as described below.

### Just play

Needs: `git`, Python 3, a GPU with Vulkan drivers (or run with `--rendering-method gl_compatibility`).

```bash
git clone https://github.com/SergiiChr/this_scalpel_is_mine.git
cd this_scalpel_is_mine
./build.py play
```

The first run downloads Godot 4.7.2 .NET (about 70 MB) into `.tools/`, and the .NET 10 SDK too unless you have it,
builds the C# and starts the game.
Already have Godot 4.7.2 .NET? `GODOT_BIN=/path/to/godot ./build.py play`.

### Develop

Asset generation and lint need Python 3.13 (`python3.13` on Fedora). Visual tests draw their key frames on an NVIDIA GPU
from a Wayland desktop (Xwayland), or else in software under Xvfb and xauth, as screenshots do; headless tests need no
GPU. On GNOME, `gsettings set org.gnome.desktop.wm.preferences focus-new-windows 'strict'` opens their windows
behind the one in use, with a "Ready" notice, instead of on top. Any C# IDE works; the repository has Rider and VS Code test settings
(`.runsettings`).
All agents start with [CLAUDE.md](CLAUDE.md), which links to the shared guidelines and [workflows](.claude/skills/).

```bash
./build.py dev      # one-time setup, see below
./build.py setup    # Godot, the .NET SDK, the build and the import, no sudo; visual runs also need Xvfb and xauth
./build.py editor   # open the Godot editor
./build.py test     # the smoke tests; --all for the full regression
./build.py build    # run the tests, export build/ThisScalpelIsMine.x86_64 and start it once
./build.py assets   # regenerate models and sounds
./build.py lint     # C# analyzers, ExportRelease build and dotnet format; ruff and strict mypy
```

`./build.py dev` does, in order:

1. Ensures `git` is installed and provisions Xvfb and xauth. On Fedora, missing visual dependencies are extracted
   into `.tools/xvfb` without sudo; `dnf download`, `rpm2cpio`, `cpio` and `/usr/bin/xkbcomp` must already be installed.
   Elsewhere install `xvfb` and `xauth` with the system package manager first.
2. Downloads Godot 4.7.2 .NET into `.tools/` (and the .NET 10 SDK into `.tools/dotnet` when the system has none) and the
   Linux export templates into `~/.local/share/godot/` (about 1 GB, one time).
3. Ensures Python 3.13 is installed, creates the Python virtualenv `.venv/` and installs `requirements-dev.txt`: the
   asset generator's libraries, Blender 5.2 as a Python module (`bpy`, about 360 MB), ruff and mypy.
4. Builds the C# and imports the project so the editor opens straight away.

It's safe to run again; it only fetches what's missing.

## Assets

Most models and all bundled sound effects are generated from code. The surgeon head is a supplied model with no
generator in this repository; some generated props incorporate source models in `tools/assetgen/sources/`.
See [assets/models/README.md](assets/models/README.md) for provenance and model contracts.

- `tools/assetgen`: props, instruments and sounds (lofted and lathed meshes exported as `.glb`, sounds synthesized
  from noise, oscillators and filters).
- `tools/blender`: organic and rigged models (patient, gloves, organs) built with Blender's Python module.
  It renders review sheets to `build/blender_review/`.

The generated files are committed, so you only need `./build.py assets` after changing a generator or its inputs.

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

Shown in the bottom right corner in game, for what you're doing right now. Everything can be rebound in **Settings**.
Default bindings: [src/Core/InputActions.cs](src/Core/InputActions.cs).

The aim shows where the tool works: a dot for point tools, a line along a blade's edge. A blade only cuts along that
line (rotate the tool to turn it); moving it sideways just drags it. Drugs come in labelled vials and syringes are
unlabelled: place the needle in a vial or patient, then scroll down to draw or up to push the plunger. Doses add
up, however many pushes they take. Work out the dose from the patient's weight and the manual. Don't drop syringes.

How much the game tells you depends on the difficulty, picked on the first start and changed under **Settings**:
**I'm just a baby** lists the scenario's main tasks with step-by-step subtasks (doses for this patient included),
**Intern** only the main tasks (and an optional X-ray), and at **MD** the manual on the shelf is your only reference.
F1 hides or shows the goals. **Settings > Debug mode**
adds what the game tracks behind the scenes (the score and every scored action, objective progress, a needle that hit
the vein, how many ml of what each syringe pushed where and each ml of a drug that runs down the IV line).

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
  fonts/         Barlow for menus and screens, Inter for the symbols Barlow lacks; IM FELL English and La Belle
                 Aurore for the manual (SIL Open Font License, see OFL-*.txt)
  shaders/       Shading, outline, skin damage, fat and muscle layers, flesh, screen grime
  models/        Generated models (see README there)
  audio/         Generated sounds (see README there)
scenes/          Scene files (menus, surgery)
src/             C# code by area (Core, Data, Operation, Patients, Surgeons, Tools, UI, Visuals, World)
tests/           GdUnit4 suites by feature, and in tests/Support the test helpers and dev tools (screenshots,
                 grip fitting)
tools/assetgen/  Model, sound and manual art generator (Python)
tools/blender/   Organic and rigged models built with Blender (Python)
tools/keyframes.py  Key frame review report (./build.py screenshot_review)
.claude/skills/ Shared agent workflows, indexed in CLAUDE.md
```

## Tests

The GdUnit4 (GdUnit4Net) suites under [`tests/`](tests/) are the test catalog and source of truth.
`./build.py test` runs the headless smoke category; `./build.py test --all` runs the full regression.
`--with-key-frames` saves screenshots of key moments. Suites also run from the IDE's test explorer.
CI gates the remaining functional regression and export on smoke; visual review runs locally.

See [CLAUDE.md](CLAUDE.md#checking-a-change) for verification commands and test policy,
[review-key-frames](.claude/skills/review-key-frames/SKILL.md) for visual review and baseline updates, and
[docs/RELEASE.md](docs/RELEASE.md) for the development and release flow.
