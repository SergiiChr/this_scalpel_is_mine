# Performance guidelines

## Frame-time budget

- Gameplay stays within a 16 ms worst frame so 60 fps remains possible. Displayed FPS isn't the metric.
- Two checks, one call: tests that exercise dynamic work check it with `Budget.Check()` (`tests/Support/FrameBudget.cs`),
  never with a timer around one function. Frame times vary between runs, so a check fails only on a frame 30% or more
  over its limit (`FrameBudget.Fluctuation`).
  - CPU budget, by default: rendering off (`FrameBudget.RenderingOff()`), the game's own work per frame within
    8 ms on the reference machine (an i9-14900HX), scaled by how much slower this machine runs a fixed benchmark.
    It holds on any machine, with or without a GPU.
    Tests build optimized, so it times the code an exported game runs.
  - Render budget, `./build.py performance_test --render-budget` only: rendering on, whole frames within 16 ms on the
    real GPU.
- Known issues: a case known to go over that can't be fixed in the current change passes an explicit commented
  `localOverride` at its budget check: a `BudgetAllowance` with the reason and how many times this run's limit its
  worst frame may take. Pick the factor from the worst frame measured on the machine tests usually run on, plus 30%,
  rounded up to a half, so only a change of 30% or more fails it. The check still prints its measurements and passes an
  overrun within that factor, without marking the case broken; a frame past it fails, runtime stalls included.
  Functional assertions and unlisted budget failures remain enforced; global limits do not change.
- When frame times are checked:
  - Focused runs (`./build.py test --tag ...` or `--case ...`): a change checks the budget where it works.
  - `./build.py performance_test`: every `frame_budget` suite. Its failures are new issues and known ones that grew.
  - Never in smoke runs, full regressions (`--all`, `screenshot_review`, `generate_baseline`) or on CI (`--ci-run`):
    a long run isn't held up by frame times.
- Frame times are checked by the `frame_budget` suites, each of whose processes gets the machine to itself when they're
  checked: a run does them first, one at a time, then the other processes as many at once as the machine has cores and
  memory for (`--jobs N` overrides that). Without frame times checked, every process shares the machine.

## Writing fast code

- Use multi-threading and offload the main thread as much as possible when it's available without compromising
  results.
- Predict garbage collector behavior and build around it to avoid GC stutter.
- Do all instantiation and compilation during loading to avoid in-game stutter.
- Use performant algorithms and data structures wherever possible.
