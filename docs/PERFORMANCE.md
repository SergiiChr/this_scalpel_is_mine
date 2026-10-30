# Performance guidelines

## Frame-time budget

- Gameplay stays within a 16 ms worst frame so 60 fps remains possible. Displayed FPS isn't the metric.
- Two checks, one call: tests that exercise dynamic work check it with `Budget.Check()`
  (`tests/Support/FrameBudget.cs`), never with a timer around one function. Frame times vary between runs, so a check
  fails only on a frame 30% or more over its limit (`FrameBudget.Fluctuation`).
  - CPU budget, unless `--render-budget`: rendering off (`FrameBudget.RenderingOff()`), the game's own work per frame
    within 8 ms on the reference machine (an i9-14900HX), scaled by how much slower this machine runs a fixed benchmark.
    It holds on any machine, with or without a GPU. Tests build optimized, so it times the code an exported game runs.
  - Render budget, `./build.py performance_test --render-budget` only: rendering on, whole frames within 16 ms on the
    real GPU.
- Known issues: a case known to go over that can't be fixed in the current change passes an explicit commented
  `localOverride` at its budget check: a `BudgetAllowance` with the reason and how many times this run's limit its worst
  frame may take. Pick the factor from the worst frame measured on the machine tests usually run on, plus 30%, rounded
  up to a half, so only a change of 30% or more fails it; a factor at or under 1.3 is no allowance. The check still
  prints its measurements and passes an overrun within that factor, without marking the case broken; a frame past it
  fails, runtime stalls included. Functional assertions and unlisted budget failures remain enforced; global limits do
  not change.
- When frame times are checked:
  - Focused runs (`./build.py test --tag ...` or `--case ...`): a change checks the budget where it works.
  - `./build.py performance_test`: every `frame_budget` suite. A failure says whether it's a new issue or a known one
    that grew.
  - Never in the default smoke run, full regressions (`--all`, `screenshot_review`, `generate_baseline`) or on CI
    (`--ci-run`): a long run isn't held up by frame times.
- Frame times are checked by the `frame_budget` suites, each of whose processes gets the machine to itself when they're
  checked: a run does them first, one at a time, then the other processes as many at once as the machine has cores and
  memory for (`--jobs N` overrides that). Without frame times checked, every process shares the machine.

## Writing fast code

- Use multi-threading and offload the main thread as much as possible when it's available without compromising results.
- Do all instantiation and compilation during loading to avoid in-game stutter.
- Code that runs every frame or tick allocates nothing once warmed up: every byte is garbage the runtime collects later
  in a pause that stalls a frame. Allocation is fine while loading, setting up and on rare events.
  - Keep a temporary collection a feature needs every frame as a field and refill it (`Clear()`, then fill): a `List<T>`
    keeps its capacity. Hand out its contents as `CollectionsMarshal.AsSpan(list)` where a loop needs an array's speed.
  - Let a caller pass a `Span<T>` to fill rather than returning a new array, and take `ReadOnlySpan<T>` rather than
    `IEnumerable<T>`: a collection expression passed as a span lives on the stack.
  - LINQ, lambdas that capture, `ToArray()`, `ToList()`, `Clone()` and `foreach` over an interface allocate: keep them
    out of those paths.
  - Godot's own memory (meshes, images, packed arrays) isn't the .NET heap: these rules don't cover it.
- Allocation budgets: a smoke suite tagged `allocation_budget` holds each per-frame or per-tick simulation path to 0
  bytes with `AllocationBudget.Check()` (`tests/Support/AllocationBudget.cs`), after a warm-up. It counts the calling
  thread's allocations, so it holds on any machine and runs on CI. A new path that runs every frame or tick gets a case
  there; a path that must allocate passes its measured bytes per run as an explicit, commented budget. Whole surgery
  frames, every thread's allocations, are held to measured budgets with `AllocationBudget.CheckFrames()`: idle, cutting
  and bleeding. A change that adds a third more garbage to them fails; one that removes some lowers the budget.
- Leaks: `MemorySoakTest` plays short surgeries one after another in one session and fails when, after a warm-up, the
  managed heap or Godot's own memory grows more than a small allowance per surgery, or objects, nodes or resources stay
  behind. Run it after changes to what a surgery builds or frees, or to anything that outlives a surgery (autoloads,
  statics, signals on them).
- Every frame budget check also reports what its frames allocated, on every thread, the garbage collections that caused
  and the longest collection pause, to find where garbage comes from.
- Use performant algorithms and data structures wherever possible.
