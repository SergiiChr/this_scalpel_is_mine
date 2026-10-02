extends GutTest
## Every main-menu scenario is loaded and its complete objective sequence is exercised in order.

const TAGS = ["scenario"]
const SURGERY := preload("res://scenes/surgery.tscn")


func test_every_main_menu_scenario_can_be_completed() -> void:
	for scenario: ScenarioDef in Db.scenarios:
		await _walkthrough(scenario)


func _walkthrough(scenario: ScenarioDef) -> void:
	Net.leave()
	Net.scenario_id = scenario.id
	Net.session_seed = 1000 + scenario.order
	Net.roster = {1: {"name": "Walkthrough", "quirks": [], "ready": true}}
	Net.patient_quirks = []
	Net.run_modifiers = []
	var surgery: Surgery = SURGERY.instantiate()
	add_child(surgery)
	for _frame in 5:
		await get_tree().physics_frame
	assert_true(surgery.running, "%s starts" % scenario.id)
	surgery.running = false
	for index in surgery.objectives.steps.size():
		var step: Dictionary = surgery.objectives.steps[index]
		_satisfy(step, surgery)
		var seconds: float = step.get("seconds", 60.0 if step.type in ["calm", "listen"] else 3.0) + 0.1
		surgery.objectives.tick(seconds, surgery)
		assert_true(surgery.objectives.states[index].done, "%s completes: %s" % [scenario.id, step.label])
	assert_true(surgery.objectives.all_done(), "%s completes every required objective" % scenario.id)
	surgery.queue_free()
	for _frame in 3:
		await get_tree().process_frame


func _satisfy(step: Dictionary, surgery: Surgery) -> void:
	var patient := surgery.patient
	match step.type:
		"sanitize":
			patient._sanitized.fill(1.0)
		"iv":
			patient.iv_set = true
			patient.iv_in_vein = true
		"anesthesia":
			patient.vitals.anesthesia = step.get("level", 0.7)
		"local_block":
			patient.vitals.local_block = step.get("level", 0.5)
		"mark":
			patient.marked_uv = 10.0
		"incise":
			# Several parallel strokes keep this valid on narrow limb sites whose physical
			# length is shorter than a scenario's requested cumulative incision length.
			for stroke in 3:
				var key := 900000 + surgery.objectives.current_index() * 10 + stroke
				var across := 0.35 + stroke * 0.15
				patient.cut(key, Vector2(0.01, across), Vector2(0.99, across), 1.0, 1.0, false, 0.1)
		"extract":
			var matching := patient.targets.filter(func(target: CavityTarget) -> bool: return target.kind == step.target)
			assert_gt(matching.size(), 0, "%s contains target %s" % [surgery.scenario.id, step.target])
			for target: CavityTarget in matching:
				target.extracted = true
		"close":
			for wound: Wound in patient.wounds:
				wound.bins.fill(1.0)
				wound.muscle.fill(1.0)
		"close_internal":
			for wound: Wound in patient.wounds:
				if wound.is_internal():
					wound.bins.fill(1.0)
					wound.cauterized = 1.0
		"stop_bleeding":
			patient.vitals.bleed_rate = 0.0
		"stabilize":
			patient.vitals.rhythm = Vitals.Rhythm.SINUS
			patient.vitals.spo2 = 99.0
			patient.vitals.systolic = 120.0
		"calm":
			patient.vitals.panic = 0.0
		"inject":
			if step.has("drug"):
				patient.flags["drug_" + step.drug] = 1
			else:
				patient.flags[step.flag] = 1
		"defib":
			patient.flags.revived = true
			patient.vitals.rhythm = Vitals.Rhythm.SINUS
		"tourniquet":
			patient.tourniquet_on = true
		"clamp":
			assert_gt(patient.wounds.size(), 0, "%s has a wound to clamp" % surgery.scenario.id)
			patient.wounds[0].clamped = 1.0
		"transfuse":
			patient.transfused_ml = 500.0
			patient.vitals.blood_ml = patient.vitals.max_blood_ml
		"flip":
			patient.body.set_orientation(step.get("orientation", 2))
		"align":
			var holder: SurgicalTool = surgery.tools.tools.values().filter(
				func(tool: SurgicalTool) -> bool: return not tool.def.fixed
			).front()
			holder.holder = 1
			for target: CavityTarget in patient.targets:
				if target.is_fragment():
					target.uv = target.rest_uv
					target.gripped_by = holder.uid
		"debride":
			patient._debrided.fill(1)
		"graft":
			patient._grafted.fill(1)
		"listen":
			patient.flags.euthanized = true
		"comfort":
			patient.flags.comfort_time = step.get("seconds", 20.0)
		"wait":
			pass
		_:
			fail_test("Unknown objective type %s in %s" % [step.type, surgery.scenario.id])
