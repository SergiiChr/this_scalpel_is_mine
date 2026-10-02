extends "res://tests/support/slicing_suite.gd"
## Deliberate player-driven close-up key frames for the three tissue stacks. Review the PNGs for cohesive openings,
## aligned edges, organically separated layers, and the absence of blockiness, clipping or raised plateaus.

const TAGS = ["tussue_modification", "visual_confirmation", "tool_scalpel"]
const OUT := "res://build/test-artifacts/screenshots/slicing"


func test_close_up_progressive_depth_keyframes() -> void:
	await _hide_gut_overlay()
	await run_cases(true, OUT, true, false)
	pending("BROKEN: visual review shows rectangular, discontinuous incision segments instead of one cohesive opening")


func test_circular_skin_graft_cutout_removal_keyframes() -> void:
	await _hide_gut_overlay()
	await run_cases(true, OUT, false, true)
	pending("BROKEN: belly and thigh graft rims form jagged or detached loops instead of a cohesive circular edge")


func _hide_gut_overlay() -> void:
	var gut_runner := get_tree().root.get_node_or_null("GutRunner")
	if gut_runner:
		gut_runner.get_node("GutLayer/GutScene").visible = false
	await get_tree().process_frame
