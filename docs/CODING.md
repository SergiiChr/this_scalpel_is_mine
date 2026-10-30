# Coding

Project rules for this repository, on top of the agent's own coding guidelines.
They describe the target: new code follows them, and existing code moves to them in the migration steps of
[DESIGN.md](DESIGN.md).
The structure they serve is in [DESIGN.md](DESIGN.md), performance rules are in [PERFORMANCE.md](PERFORMANCE.md).

## Structure

- Group code by feature. A new file goes in the folder of the feature it serves.
- Game rules go in plain C# simulation classes; a node only shows state and forwards input.
- A class has one job. Keep a new class under 500 lines. Existing large classes split in their migration step, not on
  any fix.
- Partial files don't split a class: split it by moving a job into a class of its own.
- Prefer a small type that owns its data and rules over a static helper that takes the data as arguments.

## Types and data

- A closed set of values is an enum or a type, never a string: tool actions, wound kinds, objective step types, routes.
- Strings from `data/` become those types once, in `Db`. Game code never parses a file or compares a data string.
- A mistake in a data file fails loudly when `Db` loads it, naming the file and key.
- Model states so that an impossible one can't be written: separate types over flags that must agree.
- Definitions from `Db` are read-only. Anything that changes during a surgery lives in simulation state.
- Numbers that define a rule are named constants or data, never literals in the middle of logic.

## Dependencies

- Pass what a class needs through its constructor or method. Nothing new reads a global other than `Db`. A scene node
  has no constructor arguments, so it gets its dependencies in `Setup()` from the session before entering the tree.
- Time comes in as the step and randomness as a seeded `System.Random`.
- The simulation reports through events in a buffer the session owns. It never calls the HUD, sound, effects or the
  network.
- Nothing becomes `public` or `internal` only so a test can reach it.

## Errors

- Validate at the boundary (data files, network messages, player save files), then trust the value inside.
- Catch an exception only where the game can carry on, and say what was lost.
- A failed assumption inside the simulation is a bug: let it fail.

## Networking

- Only the session layer has RPCs, and each carries a typed message.
- A client's message is checked by the host before it changes anything.

## Godot

- Keep node classes thin: `_Process` and `_PhysicsProcess` hand work to the class that owns it.
- Build nodes, load resources and compile shaders while loading, never during a surgery.

## Python tools

- `build.py` uses only the standard library. Generators may use `requirements-dev.txt`.
- Generators are deterministic: the same inputs write the same model.
