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
	var wear := func(_def: DrugDef, _level: float) -> float: return 1.0
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


func test_thresholds_for_anesthetics_bags_lethal_drugs_and_dangerous_pairs() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	var patient := driver.patient
	var toasts: Array[String] = []
	driver.surgery.hud._toasts.child_entered_tree.connect(func(toast: Node) -> void: toasts.append((toast as Label).text))
	var right := func(id: String) -> float: return Db.drug(id).dose * patient.weight_kg
	# An anesthetic tops up to the right dose: a second one deepens it for a while, then it's back to one dose.
	patient.drugs = DrugLevels.new()
	patient.flags.erase("overdose")
	patient.administer("propofol", "vein", right.call("propofol"))
	_run(patient, 20.0)
	patient.administer("propofol", "vein", right.call("propofol"))
	_run(patient, 20.0)
	var deeper := patient.drugs.level("propofol")
	_run(patient, Db.drug("propofol").duration * 1.2)
	var held := patient.drugs.level("propofol")
	assert_true(deeper > 1.5 and absf(held - 1.0) < 0.02 and not patient.flags.has("overdose"), "a second dose of propofol deepens it (%.2f), then it holds at one dose (%.2f), no overdose" % [deeper, held])
	# One dose of 2.5 times the right one is an overdose, though some wears off while it soaks in.
	patient.drugs = DrugLevels.new()
	patient.administer(DRUG, "vein", right.call(DRUG) * DrugDef.DOSE_OVERDOSE)
	_run(patient, 20.0)
	assert_true(patient.flags.has("overdose"), "a single dose of %.1f times the right one is an overdose" % DrugDef.DOSE_OVERDOSE)
	# Bags have no dose to overdo: three blood bags in a row are just a lot of blood.
	patient.drugs = DrugLevels.new()
	patient.flags.erase("overdose")
	for i in 3:
		patient.administer("blood_o_neg", "vein")
	_run(patient, 20.0)
	assert_false(patient.flags.has("overdose"), "three blood bags in a row are no overdose")
	# Two drugs that react, working from the same moment, react once.
	patient.drugs = DrugLevels.new()
	toasts.clear()
	patient.administer("adrenaline", "direct", right.call("adrenaline"))
	patient.administer("cocaine", "direct", right.call("cocaine") if Db.drug("cocaine").dose > 0.0 else -1.0)
	_run(patient, 20.0)
	await driver.frames(2)
	var spikes := toasts.filter(func(toast: String) -> bool: return toast.contains("spikes")).size()
	assert_eq(spikes, 1, "adrenaline and cocaine working together react once (%s)" % [toasts])
	# A lethal drug that worked ends it, though the right dose wears off below working before the end.
	patient.drugs = DrugLevels.new()
	patient.vitals.rhythm = Vitals.Rhythm.SINUS
	var reasons: Array[String] = []
	patient.died.connect(func(reason: String) -> void: reasons.append(reason))
	patient.administer("pentobarbital", "vein", right.call("pentobarbital"))
	for i in int(Db.drug("pentobarbital").duration * 10.0):
		if not patient.alive:
			break
		patient._simulate(0.1)
	assert_eq(reasons, ["Passed away peacefully."] as Array[String], "the right dose of pentobarbital lets the patient pass away peacefully")
	await driver.stop()


func test_drawing_from_the_iv_bag_takes_what_was_pushed_in_first() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	var tools := driver.surgery.tools
	var bag := tools.drip_bag()
	var amount := 6.0 * Db.tool("vial_" + DRUG).concentration
	tools.add_liquid(bag, 6.0, {DRUG: amount})
	bag.bolus = 6.0
	var syringe := SurgeryState.tool_is_on_tray(driver.surgery, "syringe_10")
	ToolActions._draw_from_bag(bag, syringe, 3.0)
	assert_almost_eq(float(syringe.contents.get(DRUG, 0.0)), amount * 0.5, 0.0001, "3 ml drawn back by the port bring half the drug pushed in")
	assert_almost_eq(bag.bolus, 3.0, 0.0001, "the other 3 ml of it are still to run down the line")
	assert_almost_eq(float(bag.contents.get(DRUG, 0.0)), amount * 0.5, 0.0001, "with the other half of the drug")
	await driver.stop()


## `seconds` of the patient's drugs soaking in and wearing off, in steps like the patient's own.
static func _run(patient: Patient, seconds: float) -> void:
	for i in int(seconds * 10.0):
		patient._drug_effects(0.1)
