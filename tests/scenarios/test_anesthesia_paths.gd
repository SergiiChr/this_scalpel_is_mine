extends GutTest
## Other ways of keeping the patient from feeling the surgery than the one a scenario's flow takes, played through
## like a player: each still ends the surgery with the game's own success report.

const TAGS = ["scenario"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const Broken := preload("res://tests/support/broken.gd")


## A patient allergic to lidocaine: the manual (17_conditions/allergy.txt) says to use "General anesthesia, or topical
## cocaine" instead. Followed with general anesthesia, the cut gets closed and stops bleeding, but the surgery never
## ends: the "Numb the area" step (ObjectiveChecks "local_block") only counts Vitals.local_block, which only lidocaine
## and cocaine give, and cocaine can't be ordered. Hand stitch has no time limit, so nothing ever ends it.
## Hand stitch child has allergy in its quirk pool too.
## To fix: let the step count general anesthesia too (or make it "the patient doesn't feel the site"), or have the
## manual and the nurse offer a local anesthetic that works.
func test_hand_stitch_lidocaine_allergy_under_general_anesthesia() -> void:
	if not Broken.reproduce(self, "with a lidocaine allergy, general anesthesia (the manual's alternative) never completes \"Numb the area\", the surgery never ends."):
		return
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("hand_stitch", false, 1, [{"id": "allergy", "variant": "lidocaine"}])
	var surgery := driver.surgery
	await driver.player_sets_iv()
	await driver.player_sanitizes_site(0.5)
	await driver.player_anesthetizes()
	await driver.player_closes_wounds()
	await driver.player_stops_bleeding(0.2)
	await driver.wait_until(func() -> bool: return surgery.finished, 120.0)
	assert_true(surgery.report.get("success", false), "hand stitch ends in success under general anesthesia: %s" % [surgery.objectives.snapshot()])
	await driver.stop()
	driver.queue_free()
