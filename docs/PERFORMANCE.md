# Performance guidelines

## Frame-time budget

- Gameplay stays within a 16 ms worst frame so 60 fps remains possible. Displayed FPS isn't the metric.
- Two checks, one call: tests that exercise dynamic work check it with `Budget.Check()` (`tests/Support/FrameBudget.cs`),
  never with a timer around one function. A case known to go over that can't be fixed in the current change passes
  an explicit commented `localOverride` reason at its budget check. The check still prints its measurements and passes
  that approved overrun, without marking the case broken. Functional assertions and unlisted budget failures remain
  enforced; global limits do not change.
  - CPU budget, every local run: rendering off (`FrameBudget.RenderingOff()`), the game's own work per frame within
    8 ms on the reference machine (an i9-14900HX), scaled by how much slower this machine runs a fixed benchmark.
    It holds on any machine, with or without a GPU.
  - Render budget, `./build.py full_performance_run` only: rendering on, whole frames within 16 ms on the real GPU.
- Frame times are checked only in suites that run alone (`--jobs 1`, the default, or with key frames). CI passes
  `--ci-run`, which doesn't check them, and runs suites four at a time.

## Writing fast code

- Use multi-threading and offload the main thread as much as possible when it's available without compromising
  results.
- Predict garbage collector behavior and build around it to avoid GC stutter.
- Do all instantiation and compilation during loading to avoid in-game stutter.
- Use performant algorithms and data structures wherever possible.
