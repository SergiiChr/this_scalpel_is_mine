# Design

This file describes the target architecture.
New code follows it, and existing code moves to it when touched.
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

Simulation is plain C#.
It uses no nodes, scene tree, input, rendering, sound, RPCs, `Engine` or `GD` calls.
Godot math types such as `Vector3` and `Transform3D` are fine.
Anything it needs from the engine, such as a ray against the body mesh, comes through a small interface the session
implements.

Layers are a dependency rule, not a folder layout: folders stay by feature.
A feature with both layers has separate classes for them, such as `Patient` for the rules and `PatientView` for the
node.

## One simulation step

1. Presentation reads input and sends a command, such as "use tool here at effort 2".
2. The session passes the commands of this step to the simulation, with the time step.
3. The simulation applies them, updates its state and returns events, such as "wound opened" or "patient spoke".
4. Presentation draws the state and plays the events.

The simulation never calls upward: a sound, toast or effect is an event.
Time always comes in as the step, never from a clock.
Randomness comes from generators seeded from the session seed, one per system, so one system's rolls can't shift
another's.

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
The simulation doesn't know whether it runs on a host, a client or alone.

## Dependencies

- `Db` stays static, because it is read-only after loading.
- The surgery, network, sound and settings are passed to what needs them. Simulation gets settings as plain values.
- Nothing new reads `Surgery.Current` or `Net.Instance`.
- Tools act on the simulation they are given and never reach the HUD.

## Workers

A heavy simulation step runs on a worker at a fixed rate.
Presentation reads its last finished result and never waits for one.
What a worker hands over can't change afterward.

## Migration

Each step leaves the game working and moves its rule tests down to the simulation tier as it goes.

1. Test runner: a tier that runs without starting Godot.
2. Time and randomness passed in, replacing `GameClock` and `GD.Rand*` in game rules.
3. Objectives, scoring, nurse, lab and events become plain classes.
4. Tool rules: `ToolActions` and `src/Tools/Actions` take the simulation they act on, and tool actions become a closed
   type.
5. `Patient` splits into rules and view, and its RPCs move to the session.
6. `TissueSim` and blood: their rules drop every engine dependency.
7. `PatientBody` and `Surgeon` split by feature, into rules and view.
8. A check that fails the build when simulation code uses something the layer forbids.
