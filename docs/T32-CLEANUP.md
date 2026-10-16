# T32 cleanup audit

Audited from `develop` at `08ae426b`. This is a bounded cleanup of verified unused members, not a claim that every
remaining symbol or asset is used. No runtime behavior, test cases, assets or UID files are changed.

## Removed members

Before removal, each name occurred only at its declaration in tracked source, scenes, configuration, data, tools
and documentation. Searches also checked Godot-style snake_case names. The `cut_pools` key-frame label in
`BleedSourcesTest` names a capture, not a property lookup.

| Member | Evidence for removal |
| --- | --- |
| `InputActions.LabelFor` | No callers. Settings builds labels from `InputActions.Defaults`; HUD binding text uses `BindingText`. The containing type is static, not a Godot script instance. |
| `TissueSim.TapeBreak` | No callers. Tape strength is written through `TapeStrength`; active tape strain is read through `TapeStrain`. `TissueSim` is an ordinary C# class, not a scene-bound node. |
| `BloodFlow.CutPools` | No readers. Internal getter only exposed `_cutPools`; the list and its rendering uses remain intact. No scene bindings or reflection lookups target this property. |
| `SurgeryDriver.PlayerHoldsSignature` | No callers or scene/signal bindings. Discharge cases send player input directly; `PlayerDischarges` remains the shared complete-discharge action. This helper has no test-case attribute. |
| `SurgeryState.FloorIsClear` | No callers. Static fixture helper, not a discovered test or scene-bound callback. No tests are removed or weakened. |

Reflection was checked separately: `ManagedRuntime.GameMethods` enumerates methods to prepare their compiled code,
without invoking them; it does not make unused helpers gameplay entry points. `ManualPage` reads public static
constants and public instance data members for manual expressions; none of the removed members supplies those
expressions. Test discovery uses suite/case attributes; Godot lifecycle overrides, RPCs and signal callbacks were
excluded from removal candidates.

## Inventory and retained candidates

- Tracked code contains 230 C# files, 24 Python files and three shell helpers; no GDScript files remain. The C# project
  includes tests in development and excludes them and the generated test adapter in export configurations.
- All 12 tracked scenes have existing external resource paths. The four game scenes are selected by
  `project.godot` and `Net` scene constants. The eight support scenes include network drivers and the documented
  `FitGrips`, `GripGallery` and `Screenshot` command-line tools; absence of a menu link does not make them obsolete.
  No tracked standalone `.tres` or `.res` files were found.
- `GodotArgsAttribute.Args` looks unread by C#, but the attribute's arguments are parsed from source by
  `build.py.parse_suite`. Keep the declaration and its metadata contract.
- `ModelSlot` constructs model paths from category, data-defined model name and extension. `ToolDef.ModelName`,
  `CavityTarget.Kind`, organ selection and `Room` prop keys supply names indirectly. `Db` discovers scenarios and
  manual pages by directory, including disabled scenarios needed by tests. No such resource is verified dead here.
- `tools.assetgen` and `tools.blender` are both invoked by `build.py assets`. They generate different model groups:
  assetgen's anatomy module makes non-anatomical targets; Blender's makes organs and anatomical targets.
  `tools/assetgen/sources` contains generator inputs, not abandoned game assets. No generator outputs were rewritten.
- `SewAction` and `SutureAction` are distinct live tool actions: a needle places thread holes and ties knots; pressed
  closures close wounds incrementally. Both are registered in `ToolAction`. Similar names do not justify merging them.
- Python build/key-frame tests and shell helpers have CI/build callers. Known-issue and broken cases, disabled
  scenario data, generated import metadata and `.uid` files remain; UID auditing belongs to T31.
- No Git branch was classified as obsolete from its name or age. Branch removal needs evidence about ownership and
  outstanding work beyond this source audit.

## Verification

Verified with `./build.py setup` (C# build and Godot import), `./build.py lint` (development/export builds, analyzers,
formatting, Ruff and mypy), and `.venv/bin/python -m unittest tools.test_build tools.test_keyframes` (42 tests).
`./build.py test --jobs 4` passed all 56 smoke suites, including the affected tape, blood-pool, discharge, HUD,
manual and model/resource coverage. `SKIP_TESTS=1 ./build.py build` then exported the game and passed its headless
startup check. All tracked scene external resource paths were also checked to exist.

Godot import and .NET formatting/test processes needed execution outside the restricted sandbox for local sockets
and Godot's editor settings. The successful reruns used the unchanged repository workflows.
This change has no visual behavior change and requires no new key frames or duplicate tests.
The GdUnit suites remain the executable test catalog. Independent diff review found no actionable issues.
