extends "res://tests/support/scenario_flow.gd"
## Every main-menu scenario played through its positive flow, like a player (see scenario_flow.gd): each required
## objective done in order, and the game itself ends the surgery with a successful report.
## In a run with key frames the major scenarios also save the untouched site, then the site right after every
## objective. Review them for continuity, clipping, mesh intersections, material consistency and tool contact.

const TAGS = ["scenario", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
## CI intermittently stalls when a second operation shares the first one's engine/rendering state.
## Keep each complete flow in its own process; the runner still discovers every case in this script.
const ISOLATE_CASES = true


func test_every_scenario_has_a_flow() -> void:
	for scenario: ScenarioDef in Db.scenarios + Db.disabled_scenarios:
		assert_true(has_method("test_" + scenario.id), "scenario %s has a positive flow case" % scenario.id)


func test_hand_stitch() -> void:
	await play("hand_stitch")


func test_hand_stitch_child() -> void:
	await play("hand_stitch_child")


func test_appendectomy() -> void:
	pending("BROKEN: no key frames, they check the frame budget and the worst frame takes 28 ms of game work (budget 16 ms) while sewing the muscle.")
	await play("appendectomy")


func test_bullet_muscle() -> void:
	await play("bullet_muscle")


func test_sidewalk_stab() -> void:
	await play("sidewalk_stab")


func test_ambulance_bullet() -> void:
	await play("ambulance_bullet")


func test_open_fracture() -> void:
	pending("BROKEN: no key frames, they check the frame budget and the worst frame takes 18 ms of game work (budget 16 ms) while sewing.")
	await play("open_fracture")


func test_slit_throat() -> void:
	pending("BROKEN: blood bags swapped onto the IV don't bring the blood back (2.9 of 5 l, 0 ml transfused after four bags), \"Replace lost blood\" never completes.")


func test_knife_back() -> void:
	await play("knife_back", true)


func test_bullet_stomach() -> void:
	pending("BROKEN: forceps take hold as soon as they're pressed, while still coming down into the opening, so they get a vessel or the skin edge instead of the bowel and the bullet.")


func test_broken_ribs() -> void:
	pending("BROKEN: oxygen stays at 90 and blood pressure at 76 after the bag swap, \"Oxygen back above 94\" never completes.")


func test_lung_fluid() -> void:
	await play("lung_fluid")


func test_gangrene_amputation() -> void:
	pending("BROKEN: the bone saw held on the bone for 60 s doesn't get through it.")


func test_burn_graft() -> void:
	pending("BROKEN: ten graft sheets cover only 61% of the burns, 70% needed.")


func test_nose_job() -> void:
	pending("BROKEN: the needle doesn't reach the last muscle and skin stitches on the nose (tip in the air), the patient arrests before it's closed.")


func test_oscar_figurine() -> void:
	pending("BROKEN: forceps take hold as soon as they're pressed, while still coming down into the opening (tip 8 mm above the site, figurine 70 mm deep), so they clamp a vessel instead.")


func test_blocked_artery() -> void:
	pending("BROKEN: forceps take hold of nothing over the clot (they close before reaching it), \"Remove the clot\" never completes.")


func test_heart_attack() -> void:
	await play("heart_attack", true)


func test_colon_cancer() -> void:
	# Also, with key frames the worst frame takes 32 ms of game work (budget 16 ms) while holding the bowel aside and sewing.
	pending("BROKEN: run beside another test script (--jobs 2), a 7.7 cm tear keeps bleeding 2.4 ml/s and \"Control the bleeding\" never completes; run alone it passes. Likely wall-clock driven (tremor uses Time.get_ticks_msec()), not yet debugged.")


func test_bullet_near_heart() -> void:
	pending("BROKEN: forceps take hold as soon as they're pressed, while still coming down into the opening, so they get a vessel instead of the lung and the bullet; the patient bleeds out.")


func test_leg_extension() -> void:
	pending("BROKEN: cautery, a hemostat, tranexamic acid and gauze leave three tears bleeding 0.4 ml/s each, the patient arrests.")


func test_brain_tumor() -> void:
	pending("BROKEN: cautery, tranexamic acid and gauze leave tears bleeding, \"Control the bleeding\" never completes.")


func test_euthanasia() -> void:
	await play("euthanasia")
