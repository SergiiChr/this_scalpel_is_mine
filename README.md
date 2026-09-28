# This Scalpel Is Mine

Co-op roguelike surgery thriller. Two surgeons, one patient, not enough hands.
Inspired by Trauma Center, with Overcooked-style pressure and random surgeon and patient quirks every run.

Built with Godot 4.7 (GDScript, Forward+ renderer).

## Run and build (Fedora / any x86_64 Linux)

```bash
./build.sh run      # play from source
./build.sh          # build a standalone executable: build/ThisScalpelIsMine.x86_64
./build.sh editor   # open the Godot editor
```

The script downloads Godot 4.7.2 into `.tools/` on first use.
The first `./build.sh` also downloads the export templates (about 1 GB, one time, only the Linux templates are kept).
Already have Godot 4.7.2? `GODOT_BIN=/path/to/godot ./build.sh run`.

## Multiplayer

Host-client over ENet, default port **24565** (UDP).
The host picks the scenario and keeps the progress save.
The other player joins with the host's IP from **Multiplayer**, and can save it as a known host.
Over the internet the host needs to forward UDP 24565 (or pick another port in the menu).

## Controls

Shown in the bottom right corner in game. Everything can be rebound in **Settings**.

| Action | Default |
|---|---|
| Move active hand | Mouse |
| Look around | Hold RMB |
| Use tool (hold) / toggle clamp | LMB |
| Hand height, or pressure while using | Mouse wheel |
| Grab / release | E |
| Interact (manual, card, nurse, IV, lab, turn patient) | F |
| Switch hand | Tab |
| Lift hand over hands and tall tools | Hold Shift |
| Hold breath (steady hands) | Hold Space |
| Tilt / twist tool | R, T / Z, X |
| Drink / wear | H |
| Belt slots | 1-4 |
| Move | WASD |

## Project layout

```
data/            Everything designers edit. The game reads these at startup.
  quirks/        Patient and surgeon quirk sheets (markdown, source of truth)
  scenarios/     One file per scenario
  manual/        In-game manual pages (BBCode)
  tools.cfg, drugs.cfg, events.cfg, scoring.cfg, consequences.cfg, audio.cfg, dialogue/
assets/          Art and sound, organized for review and replacement
  icons/         Quirk icons (Pip-Boy style SVG), app icon
  manual/        Manual diagrams
  shaders/       Cel shading, outline, skin damage, flesh, screen grime
  models/        Drop-in replacements for placeholder geometry (see README there)
  audio/         Sound files (see README there)
scenes/          Scene files (menus, surgery)
src/             Code, see docs/DESIGN.md for the architecture
tests/           Headless smoke test, network test, screenshot tool
```

## Tests

```bash
godot --headless --path . res://tests/smoke_test.tscn     # every scenario, every tool, every event
godot --headless --path . res://tests/net_test.tscn -- --role=host &
godot --headless --path . res://tests/net_test.tscn -- --role=client
```

See [docs/DESIGN.md](docs/DESIGN.md) for architecture, data formats and the mechanics list.
