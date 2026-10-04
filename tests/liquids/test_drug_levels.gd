extends GutTest
## Drugs add up in the body (DrugLevels): what counts is how much is in, not how many injections it came in. A drug
## works once its level reaches DrugDef.DOSE_EFFECTIVE and is an overdose from DrugDef.DOSE_OVERDOSE, for a patient and
## a surgeon alike.

const TAGS = ["smoke", "liquids"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const DRUG := "atropine"


func test_ten_small_injections_add_up_to_one_big_one() -> void:
	var def := Db.drug(DRUG)
	var small := DrugLevels.new()
	var big := DrugLevels.new()
	var wear := func(_def: DrugDef) -> float: return 1.0
	big.give(def, 1.0, def.onset)
	for second in 30:
		if second < 10:
			small.give(def, 0.1, def.onset)
		for i in 10:
			small.update(0.1, wear)
			big.update(0.1, wear)
	assert_almost_eq(small.level(DRUG), big.level(DRUG), 0.001, "ten tenths of a dose a second apart are as much in the blood as one dose (%.4f, %.4f)" % [small.level(DRUG), big.level(DRUG)])
	assert_almost_eq(big.level(DRUG), 1.0 - 30.0 / def.duration, 0.002, "a dose wears off by one right dose every %.0f s" % def.duration)


func test_a_drug_works_from_an_effective_level_and_overdoses_past_a_safe_one() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	var patient := driver.patient
	var def := Db.drug(DRUG)
	var tenth := def.dose * patient.weight_kg * 0.1
	var soaked := def.onset * DrugDef.DIRECT_ONSET * 2.0
	for i in 4:
		patient.administer(DRUG, "direct", tenth)
	await driver.seconds(soaked)
	assert_false(patient.flags.has("drug_" + DRUG), "four tenths of a dose don't do the job (level %.2f)" % patient.drugs.level(DRUG))
	var faint := patient._drug_effects(0.0).hr
	assert_true(faint > 0.0 and faint < def.effect("hr"), "they still have a faint effect (%.1f of %.1f bpm)" % [faint, def.effect("hr")])
	for i in 2:
		patient.administer(DRUG, "direct", tenth)
	await driver.seconds(soaked)
	assert_true(patient.flags.has("drug_" + DRUG), "two more tenths make it work (level %.2f)" % patient.drugs.level(DRUG))
	assert_false(patient.flags.has("overdose"), "that's no overdose")
	for i in 25:
		patient.administer(DRUG, "direct", tenth)
	await driver.seconds(soaked)
	assert_true(patient.flags.has("overdose"), "twenty five more tenths are an overdose (level %.2f, from %.1f)" % [patient.drugs.level(DRUG), DrugDef.DOSE_OVERDOSE])
	await driver.stop()


func test_a_surgeons_sedative_adds_up_too() -> void:
	var status := SurgeonStatus.new(Modifiers.new())
	var right := Db.drug("diazepam").dose * status.weight_kg
	var events := PackedStringArray()
	for i in 25:
		status.administer("diazepam", right * 0.1)
		for j in 5:
			events.append_array(status.update(0.1, {}))
	assert_true(events.has("knocked_out") and status.is_out(), "twenty five tenths of a dose knock a surgeon out like 2.5 doses at once (%.2f in)" % status.drugs.level("diazepam"))
