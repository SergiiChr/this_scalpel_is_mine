# Testing

Read this before writing, changing or reviewing tests.
It adds to the agent's own testing guidelines and wins where they differ.
Commands: [Checking a change](../AGENTS.md#checking-a-change).

## Confirmed seams

New tests go through one of these seams; a new seam needs the owner's confirmation first.

- Player input: `Player*` steps and `tests/Support/PlayerInput.cs`. Catches whole flows as a player plays them; misses
  number edge cases and is slow.
- Simulation classes: `TissueSim`, `DrugLevels`, `SurfaceBlood` and classes like them, called directly as their own
  interface. Catches math and boundary cases fast; misses wiring into the game.
- Network sessions: a host and a client playing over the network (`tests/Support/NetSession.cs` and the `Net*` drivers).
  Catches desync; slow, and a busy machine disturbs it.

Existing suites outside these seams, such as model and data checks, stay as they are until they are reworked.
Every user-visible change still needs a visual test: a seam says where a test goes, not whether one is needed.

## Test structure and policy

- Suites live in `tests/<Feature>/<Name>Test.cs` (namespace `Scalpel.Tests.<Feature>`), with
  `[TestSuite, RequireGodotRuntime]` on the class and `[TestCase]` on each case. Shared helpers live in
  `tests/Support/`. New test code adds no helper methods to test files.
- Compatible cases share setup through `SharedScene<T>` (`tests/Support/SharedScene.cs`): use a named group, reset its
  input and temporary tools before each case, and complete it only after all assertions pass. Run `DiscardIncomplete()`
  after each case and `Stop()` after the suite. Empty groups get fresh scenes. Keep cases that change tissue, IV
  delivery, sedation or animation baselines isolated unless their reset is explicitly verified. Aim checks may share
  disjoint tissue regions: each asserts its region is intact before use, the patient is asleep, and input/effort/tilt
  are reset between cases; the wrist reference stays separate from destructive grip checks. Keep individual cases and
  their result timings; do not replace them with one opaque loop.
- Ordinary driver cases advance only the nurse delivery/cooldown clock through `SurgeryState`; they do not wait for
  ordering delays. `NurseShopTest` alone enables `WaitForNurse` and checks delivery timing, deadline expiration and
  cooldown rejection. General case/session safety timeouts remain enabled. Fixtures that need anesthesia before tissue
  changes assert that preparation explicitly rather than relying on time spent waiting for the nurse. Pickup still waits
  for the spawned tool to land and uses normal player grab input, never a forced-grab fallback.
- Core mechanic assertions belong in a fitting full scenario flow when that flow already performs the action.
  Specialized failure paths and destructive stress checks stay separate. Shared flow checks live in `tests/Support/`.
  Extended scenario suites that validate core mechanics carry `core_validation`; suites pass their specific steps and
  final assertions as hooks to the generic scenario loop, which retains objective deadlines and capture handling.
- A case's name states the capability it proves, as a spec line. A flow case may check several steps on the way; its
  name states the flow's outcome. A name that lists unrelated checks with "and" is two cases.
- Expected values come from an independent source: a literal, a worked example or the spec. A new case never builds its
  expected value from the game constant under test. A constant may be the expected value only when the claim is that two
  separate parts agree, such as a generated model matching its solver.
- Cases that differ only in a value (every scenario) are one method with a `[TestCase(value)]` row per value.
- A suite declares its categories with `[TestCategory("...")]` and its Godot options with
  `[GodotArgs("--fixed-fps", "60")]`; `[IsolateCases]` gives every case its own Godot process.
- Categories describe the test type or a feature worth rerunning: `smoke`, `slow`, `scenario`, `network`,
  `tissue_modification`, `liquids`, `tool_<id>`, `tool_all`, `core_validation`, `visual_confirmation`, `frame_budget`,
  `allocation_budget`, `skin_pull`, `hand_hold`, `room`, `broken`. `allocation_budget` marks a suite that holds
  per-frame code to its allocation budget (`AllocationBudget.Check`, see docs/PERFORMANCE.md). `frame_budget` marks a
  suite that checks frame times (`Budget.Check` or `FrameBudget.Limit`, itself or through `ToolSession`, `SutureChecks`,
  `ScenarioFlow` or `GameplaySweep`; `RenderingOff()` alone doesn't count); its processes run alone, before the rest.
  `slow` means one case takes over half the per-test limit (`TEST_TIMEOUT` in `build.py`); its cases take
  `[TestCase(Timeout = Limits.Slow)]`.
- `smoke` cases are headless and programmatic, each under five minutes. They may take key frames only when their
  assertions fully validate the behavior without them.
- A test does what its name and messages claim. A case that says a scenario is completed plays it like a player and
  passes only on the game's own success report, never on state the test set, a shortened run or a loosened threshold. If
  the claim can't be reached, the case fails, or is a known issue or broken (below).
- Prefer comprehensive end-to-end checks: the surgeon picks a scalpel up from the table, walks to the patient, makes a
  light 5 cm cut, and the opening measures 5 cm.
- Don't check the same thing twice. A mechanic an end-to-end case already proves needs no dedicated check; a sweep over
  every scenario runs scenario-independent checks once.
- Choose one representative per behavior class, plus values just inside, at and beyond a meaningful boundary. Repeated
  input is justified by cumulative behavior or an animation transition; avoid replaying equivalent strokes or
  repositioning when the target has not changed.
- Use hard programmatic checks (geometry, gaps, alignment, masks, depth, physics) wherever a defect is measurable.
- Player actions and direct state changes stay apart. `Player*` steps in `tests/Support/SurgeryDriver*.cs`
  (`PlayerRequestsItem("scalpel")`, `PlayerCutsSkin(from, to, level)`) walk, hold tools and press the player's controls.
  `tests/Support/SurgeryState.cs` methods are named for the state they write (`SkinIsCut(...)`, `PatientIsAsleep(...)`)
  and only set up what a test isn't about. Add reusable steps there.
- Simulated input goes through `tests/Support/PlayerInput.cs` (`Input.ParseInputEvent()`). Never call an input handler
  (`_UnhandledInput()`, `SteerHand()`, `AimTool()`) or set what it would set. Sent input arrives with the next frame:
  wait a frame before checking it.
- Only members that form a confirmed seam's interface are `internal` (the tests build into the game's assembly), never
  public for a test. A new test reads results through a seam, not through other members.
- Known issue: if a non-trivial failure can't be fixed in the current change and fails the same way every run, wrap the
  case in `KnownIssue.Expect(failure, ...)` (`tests/Support/KnownIssue.cs`) with
  `[TestCase(..., Description = "KNOWN ISSUE: reason")]`. It runs with the rest of the regression and passes only when
  it fails with a message containing `failure`: pick the part that says where it breaks (the objective that doesn't
  complete), so everything before it is still checked. Another failure fails it as usual, and once it passes it fails
  until it's made an ordinary case again.
- Broken: a failure that can't be pinned that way (it passes or fails from run to run, or shows only in a key frame
  review) moves its case to its own method with `[TestCategory("broken")]` and
  `[TestCase(..., Description = "BROKEN: reason")]` (GdUnit's own ignore doesn't work with the adapter). It's skipped:
  `build.py` lists it as pending, and `RUN_BROKEN=1` runs it.
- Disable any scenario that relies on a known issue or a broken case (`disabled=true` in its cfg, with a comment naming
  the case). Never delete or weaken the case.

## Visual verification

- Every user-visible change adds or updates a deterministic visual test. It captures the untouched state, the key
  interaction and the persistent result, plus an oblique close-up for spatial or layered changes.
- Capture with `KeyFrames` right after a named action, never on elapsed time. A case with key frames is one case: it
  takes them only when `KeyFrames.Wanted()`, never in a copy made for screenshots. A feature an end-to-end case already
  captures needs no more screenshots.
- Capture each distinct visual claim once. Shared helpers opt into stitch/staple progress shots; callers capture their
  final result. Keep one untouched reference per distinct fixture, and use the player view for HUD, zoom and
  transparency claims instead of adding site views that cannot show them. Internal objective flags use assertions.
- Every key frame carries a description of what it should show (`Capture("on_hook", "the card on its hook, ...")`): the
  review prints it beside the sheet. Write what a reviewer checks the picture against, not the action's name. When an
  assertion right before it claims the same thing, use `SurgeryDriver.AssertAndCapture(holds, claim, keyFrame)` so the
  text is written once.
- Review the frames for continuity, clipping, mesh intersections, material consistency, tool contact, animation
  transitions and UI legibility. Screenshot comparisons may flag regressions but don't replace that review.
- Never commit key frames of a failed run, of a case marked broken, or ones that show a defect; never edit the baseline
  by hand. A change with no missing, changed or new key frames leaves it untouched.
- The failed-run rule has one exception: a full regression may supply manually reviewed, non-defective frames. Focused
  updates always require a passing run; an accepted baseline never makes a failing run ready for handoff.
- Broken cases are skipped and take no key frames. A passing full regression drops their old baseline frames.
- Baseline acceptance, update commands and the failed-regression procedure:
  [review-key-frames](../.claude/skills/review-key-frames/SKILL.md).
- Key frames are taken and compared in local runs only, never on CI. They are always rendered in a virtual display by
  the software renderer (`xvfb-run`), without film grain, so machines save the same or nearly the same pixels for the
  same scene. The review errs toward listing a key frame: a faint change over the whole view counts, so a sheet may show
  no visible difference.
