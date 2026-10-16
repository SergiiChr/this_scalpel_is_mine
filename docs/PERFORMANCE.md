# Performance guidelines

## Frame-time budget

- Gameplay stays within a 16 ms worst frame so 60 fps remains possible. Displayed FPS isn't the metric.
- Two checks, one call: tests that exercise dynamic work check it with `Budget.Check()` (`tests/Support/FrameBudget.cs`),
  never with a timer around one function. A case known to go over that can't be fixed in the current change passes
  an explicit commented `localOverride` at its budget check: a `BudgetAllowance` with the reason and how many times
  this run's limit its worst frame may take. Frame times vary between runs and machines, so pick the factor from the
  worst frame measured on the machine tests usually run on, plus 30%, rounded up to a half. The check still prints its
  measurements and passes an overrun within that factor, without marking the case broken; a frame past it fails,
  runtime stalls included. Functional assertions and unlisted budget failures remain enforced; global limits do not
  change.
  - CPU budget, every local run: rendering off (`FrameBudget.RenderingOff()`), the game's own work per frame within
    8 ms on the reference machine (an i9-14900HX), scaled by how much slower this machine runs a fixed benchmark.
    It holds on any machine, with or without a GPU.
  - Render budget, `./build.py full_performance_run` only: rendering on, whole frames within 16 ms on the real GPU.
- Frame times are checked by the `frame_budget` suites (`./build.py test --tag frame_budget` runs just them), each
  of whose processes gets the machine to itself: a run does them first, one at a time, then the other processes as
  many at once as the machine has cores and memory for (`--jobs N` overrides that). CI passes `--ci-run`, which
  checks no frame times, so there every process shares the machine.

## Writing fast code

- Use multi-threading and offload the main thread as much as possible when it's available without compromising
  results.
- Predict garbage collector behavior and build around it to avoid GC stutter.
- Do all instantiation and compilation during loading to avoid in-game stutter.
- Use performant algorithms and data structures wherever possible.
