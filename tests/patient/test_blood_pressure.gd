extends GutTest
## Blood pressure as a player meets it: a stimulant pushes systolic pressure above 140 mmHg, closures leak while it
## stays there or while heparin acts, cautery holds regardless, and a patient with an aneurysm bursts a vessel.

const TAGS = ["smoke"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")

var driver: Driver


func before_each() -> void:
	driver = Driver.new()
	add_child(driver)


func after_each() -> void:
	await driver.stop()
	driver.queue_free()


func test_adrenaline_bursts_an_aneurysm_that_stays_calm_without_it() -> void:
	await driver.start("appendectomy", false, 1, [{"id": "aneurysm", "variant": ""}])
	var patient := driver.patient
	var before := patient.wounds.size()
	await driver.seconds(20.0)
	assert_lt(patient.vitals.systolic, Patient.HIGH_PRESSURE, "a calm patient's pressure stays below the limit")
	assert_eq(patient.wounds.size(), before, "no vessel gives way at normal pressure")
	await driver.player_gives_drug("vial_adrenaline", driver.dose_ml("vial_adrenaline"), "vein")
	var burst := await driver.wait_until(func() -> bool: return patient.flags.has("revealed_aneurysm"), 60.0)
	assert_true(burst, "adrenaline raises pressure until the aneurysm bursts (systolic %d)\n%s" % [patient.vitals.systolic, driver.recent()])
	var new_wounds := patient.wounds.slice(before)
	assert_eq(new_wounds.size(), 1, "one vessel gives way")
	if not new_wounds.is_empty():
		assert_true((new_wounds[0] as Wound).is_internal(), "the burst vessel bleeds inside the site")


func test_closures_leak_under_high_pressure_and_heparin_but_cautery_holds() -> void:
	await driver.start("appendectomy")
	var patient := driver.patient
	SurgeryState.patient_is_numb(patient)
	var closed := SurgeryState.skin_is_cut(patient, Vector2(0.3, 0.35), Vector2(0.3, 0.65), 0.3)
	var seared := SurgeryState.skin_is_cut(patient, Vector2(0.7, 0.35), Vector2(0.7, 0.65), 0.3)
	SurgeryState.wound_is_closed(patient, closed)
	SurgeryState.wound_is_cauterized(seared)
	await driver.seconds(3.0)
	var seared_rate := seared.bleeding
	assert_lt(closed.bleeding, 0.001, "a sewn wound is dry at normal pressure")

	await driver.player_gives_drug("vial_adrenaline", driver.dose_ml("vial_adrenaline"), "vein")
	var high := await driver.wait_until(func() -> bool: return patient.vitals.systolic > 150.0, 30.0)
	assert_true(high, "adrenaline raises systolic pressure above 150 mmHg (now %d)" % patient.vitals.systolic)
	assert_gt(closed.bleeding, 0.01, "the sewn wound leaks under high pressure")
	assert_almost_eq(seared.bleeding, seared_rate, seared_rate * 0.05, "the cauterized wound holds under high pressure")

	var settled := await driver.wait_until(func() -> bool: return patient.vitals.systolic < 130.0, 150.0)
	assert_true(settled, "pressure falls back once adrenaline wears off (now %d)" % patient.vitals.systolic)
	assert_lt(closed.bleeding, 0.001, "the sewn wound seals again at normal pressure")

	await driver.player_gives_drug("vial_heparin", driver.dose_ml("vial_heparin"), "vein")
	var thinned := await driver.wait_until(func() -> bool: return closed.bleeding > 0.01, 30.0)
	assert_true(thinned, "the sewn wound leaks while heparin acts (systolic %d)" % patient.vitals.systolic)
	# Heparin thins all bleeding (bleed_mult); the seal itself is what a closure loses and cautery keeps.
	assert_lt(seared.bleeding, seared_rate * 1.7, "the cauterized wound bleeds no more than heparin's thinning")
