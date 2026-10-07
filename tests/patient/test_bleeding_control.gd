extends GutTest
## Stopping a bleed with what the nurse brings, as a player does it: cautery, a hemostat, tranexamic acid and gauze.

const TAGS = ["tissue_modification"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
## A tear as long as the one test_colon_cancer leaves bleeding (meters).
const TEAR := 0.077


## Skin torn by overstretching (Patient.tear()) bleeds hard, and the tools stop it. The disabled scenarios that end
## with a tear still bleeding (test_scenario_flows.gd) get there through the driver, not the game: its one cautery
## pass comes before the tear opens, and its gauze pass presses beside a short tear rather than on it.
func test_a_skin_tear_can_be_stopped() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	var patient := driver.patient
	SurgeryState.patient_is_asleep(patient)
	SurgeryState.skin_is_torn(patient, Vector2(0.45, 0.5), Vector2.RIGHT, driver.body.meters_to_uv(TEAR))
	await driver.seconds(2.0)
	assert_gt(patient.vitals.bleed_rate, 0.3, "the tear bleeds: %s" % driver.bleeders())
	await driver.player_stops_bleeding(0.3)
	await driver.seconds(10.0)
	assert_lte(patient.vitals.bleed_rate, 0.3, "the bleeding is under control: %s" % driver.bleeders())
	await driver.stop()
	driver.queue_free()
