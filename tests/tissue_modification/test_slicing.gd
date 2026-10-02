extends "res://tests/support/slicing_suite.gd"
## Fast, headless assertions for progressive scalpel depth and circular skin-graft removal.

const TAGS = ["smoke", "tussue_modification", "tool_scalpel"]


func test_progressive_depth_and_skin_graft_removal() -> void:
	await run_cases(false, "res://build/test-artifacts/screenshots/slicing", true, true)
