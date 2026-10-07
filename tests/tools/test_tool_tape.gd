extends GutTest
## Surgical tape as a player uses it: ordered from the nurse and pressed along a cut until it's closed.

const TAGS = ["tool_surgical_tape"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const Broken := preload("res://tests/support/broken.gd")


## A roll has 15 charges (tools.cfg) and every wound bin it closes costs one (ToolActions "suture"), but the hand
## stitch cut has 29 bins (Wound.BIN_LENGTH_UV). The tape closes the first half and then silently does nothing: no toast
## says it's used up and the HUD doesn't show charges. The needle, for comparison, closes the same cut in about 22 s.
## To fix: enough charges for the cuts tape is meant for (or charges per centimeter), and say when a roll runs out.
func test_tape_closes_the_hand_stitch_cut() -> void:
	if not Broken.reproduce(self, "one roll of tape closes only 15 of the hand stitch cut's 29 bins, then does nothing without saying why."):
		return
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("hand_stitch")
	var surgery := driver.surgery
	await driver.player_sanitizes_site(0.5)
	await driver.player_numbs_site()
	await driver.player_closes_wounds("surgical_tape")
	assert_gte(driver.patient.skin_closure(), 0.9, "the cut is closed with tape")
	await driver.player_stops_bleeding(0.2)
	await driver.wait_until(func() -> bool: return surgery.finished, 60.0)
	assert_true(surgery.report.get("success", false), "hand stitch ends in success: %s" % [surgery.objectives.snapshot()])
	await driver.stop()
	driver.queue_free()
