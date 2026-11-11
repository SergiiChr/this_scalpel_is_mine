# Design

This file describes the target architecture.
New code follows it.
Existing code moves to it in the migration steps below, and a fix elsewhere doesn't force a move.
Code that doesn't match yet is not a pattern to copy.

How the game plays: [MECHANICS.md](MECHANICS.md).
Files designers edit: [DATA.md](DATA.md).
Code rules: [CODING.md](CODING.md).
Test rules: [TESTING.md](TESTING.md).
Frame budget: [PERFORMANCE.md](PERFORMANCE.md).

## Goals

In order of priority:

1. A surgery plays out the same for every player in a session.
2. Any game rule can be proved in milliseconds, without starting the engine.
3. Designers change the game in data files, without touching code.
4. Gameplay stays within the 16 ms frame budget.

## Layers

| Layer | Holds | Depends on |
|---|---|---|
| Data | Definitions parsed from `data/`, read-only once loaded (`Db`) | Nothing else in the game |
| Simulation | Game rules and state: patient, wounds, drugs, tissue, blood, tools, objectives, scoring, nurse, events | Data |
| Session | One surgery: builds the simulation, steps it, connects players, turns input and network messages into commands | Simulation, Data |
| Presentation | Godot nodes: models, shaders, HUD, sound, input reading | Session, Simulation (read-only), Data |

Dependencies only point down the table: the simulation knows nothing about the layers above it.
Simulation code lives in `*.Simulation` namespaces, so a build check can hold it to these rules.

Simulation is plain C#.
It uses no nodes, scene tree, input, rendering, sound, RPCs, `Engine` or `GD` calls, and nothing native: no
`GodotObject`, `Geometry2D`, `Geometry3D`, `Image` or Godot collections.
Pure Godot math structs such as `Vector3` and `Transform3D` and `Mathf` are fine.
It never queries the engine.
Before a step, presentation samples what the rules need, such as hand poses and ray hits against the body mesh, into
that step's commands, as `ToolStep.Probe` does today.

Layers are a dependency rule, not a folder layout: folders stay by feature.
A feature with both layers has separate classes for them, such as `Patient` for the rules and `PatientView` for the
node.

## One simulation step

1. Presentation reads input and samples the scene into a command: a hand's pose and what it's doing, such as "using this
   tool here at effort 2". A remote peer's synced hand state is the same command.
2. The session passes the latest command of each hand to the simulation, with the fixed time step of the physics tick. A
   late message takes effect on the step it arrives.
3. The simulation applies them, updates its state and writes events, such as "wound opened" or "patient spoke", to a
   buffer the session owns, so a step allocates nothing.
4. Presentation draws the state and plays the events.

The simulation never calls upward: a sound, toast or effect is an event.
Time always comes in as the step, never from a clock.
Randomness is `System.Random` seeded from the session seed, one per system, so one system's rolls can't shift another's.

## Networking

- **Host is the authority** for the patient, tools, objectives, events and score. It runs the rules and sends results.
- **Each peer owns its surgeon** and streams body, head and hand state at 30 Hz (unreliable).
- **Tools are simulated on the host.** It reads each hand's synced position and "using" state and applies the tool's
  rules. This keeps all game logic in one place and rules out desync between two co-op players.
- **Skin damage and tissue topology are broadcast.** Paint ops, cuts, stitches, bursts, snapped springs and skin taken
  off are reliable messages, and every peer applies them to its own copy. Only the host decides what happens, then tells
  the others.
- Vitals, targets, organs and tissue grips sync at 5 Hz. Free-falling tools sync at 10 Hz.
- Solo play is the same code with an offline peer. There is no separate single player path.
- Every peer builds the same patient and tray from the session seed, so setup needs no messages.
- **Spotty connections don't end the session.** ENet drops a peer only after 15-45 s without answers, reliable messages
  queue up, and unreliable state picks up with the next packet. A 4 Hz heartbeat tracks how long each peer has been
  silent: after 0.75 s the host pauses that surgeon's tools, without releasing them, and after 1.5 s the HUD says who
  the connection is waiting on. Joining gives up after 10 s if the host never answers.
- A partner who drops out for good leaves the room and their tools fall where they were. The host refuses joins while a
  surgery runs. A client that loses the host goes back to the menu with the reason shown.

RPCs live only in the session layer and carry typed messages.
The session gives the simulation a role: the authority runs the rules, and a replica applies broadcasts and steps its
own tissue and blood.
Solo is the authority with no peers.
Rules never check for a network.

## Dependencies

- `Db` stays static, because it is read-only after loading.
- The surgery, network, sound and settings are passed to what needs them. Simulation gets settings as plain values.
- Nothing new reads `Surgery.Current` or `Net.Instance`.
- Tools act on the simulation they are given and never reach the HUD.

## Workers

A heavy simulation step runs on a worker at a fixed rate.
The session joins the worker before the next step, so rules always read finished state.
Handover uses reused buffers, and nothing else writes them while the worker runs.

## Migration

Each step leaves the game working and moves its rule tests down to the simulation tier as it goes.

1. Test runner: a tier that runs without starting Godot, proving first that the allowed math structs work there.
2. A check that fails the build when `*.Simulation` code uses something the layer forbids.
3. `Db` parses `data/` with plain C#, with no `res://` or Godot file types.
4. Time and randomness passed in, replacing `GameClock`, `GD.Rand*` and `RandomNumberGenerator` in game rules.
5. Tissue, wounds, vitals and drugs drop their native engine calls.
6. `Patient` and `PatientBody` split into rules and view.
7. The session class grows out of `Surgery`, sets the role, and takes over the RPCs.
8. Tool rules: `ToolActions` and `src/Tools/Actions` take the simulation they act on, and tool actions become a closed
   type.
9. Objectives, scoring, nurse, lab and events become plain classes.
10. `Surgeon` splits into rules and view.
