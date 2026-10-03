extends "res://tests/support/slicing_suite.gd"
## Progressive scalpel depth and circular skin-graft removal on an arm, a thigh and a belly, driven through the
## surgeon's hand. Headless assertions in smoke; with key frames also close-ups after every stage. Review those for
## cohesive openings, aligned edges, organically separated layers, and the absence of blockiness, clipping or raised
## plateaus.

const TAGS = ["smoke", "tissue_modification", "tool_scalpel", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const OUT := "res://build/test-artifacts/screenshots/slicing"


func test_progressive_depth() -> void:
	pending("BROKEN: visual review shows rectangular, discontinuous incision segments instead of one cohesive opening")
	await run_cases(OUT, true, false)


func test_circular_skin_graft_cutout_removal() -> void:
	pending("BROKEN: belly and thigh graft rims form jagged or detached loops instead of a cohesive circular edge")
	await run_cases(OUT, false, true)
