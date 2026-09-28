extends Node
## Headless smoke test: loads every scenario, uses every tool on the patient, fires every event and drug,
## turns the patient and builds the report. Any script error shows up in the output.
## Run: godot --headless --path . res://tests/smoke_test.tscn

const SURGERY := preload("res://scenes/surgery.tscn")


func _ready() -> void:
	var only := ""
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--scenario="):
			only = arg.get_slice("=", 1)
	for scenario in Db.scenarios:
		if only and scenario.id != only:
			continue
		await _run(scenario)
	print("smoke_test: done")
	get_tree().quit()


func _run(scenario: ScenarioDef) -> void:
	print("--- ", scenario.id)
	var rng := RandomNumberGenerator.new()
	rng.seed = scenario.order
	Net.leave()
	Net.scenario_id = scenario.id
	Net.session_seed = rng.randi()
	Net.roster = {1: {"name": "Tester", "quirks": QuirkRoller.roll_surgeon(rng), "ready": true}}
	Net.patient_quirks = QuirkRoller.roll_patient(scenario, rng)
	var surgery: Surgery = SURGERY.instantiate()
	add_child(surgery)
	await _frames(5)
	assert(surgery.running, "surgery did not start")
	var me := surgery.local_surgeon
	var site := surgery.patient.body.site.global_position
	for tool: SurgicalTool in surgery.tools.tools.values().duplicate():
		if tool.state != SurgicalTool.State.FREE:
			continue
		me.hands[1].engaged = false
		surgery.tools._req_grab(tool.uid, 1)
		await _frames(2)
		var hand := me.hands[1]
		hand.attached = false
		hand.local_target = me.to_local(site + Vector3(0, 0.12, 0))
		hand.target = site + Vector3(0, 0.12, 0)
		for pressure in [2, 3]:
			hand.pressure = pressure
			hand.engaged = true
			for i in 20:
				hand.local_target += Vector3(0.002, 0, 0.001)
				await get_tree().physics_frame
			hand.engaged = false
			await _frames(2)
		if OS.get_cmdline_user_args().has("--verbose"):
			var probe := surgery.patient.body.probe(tool.tip_position())
			print("    %-20s zone=%-6s uv=%s wounds=%d" % [tool.def.id, probe.zone, probe.uv, surgery.patient.wounds.size()])
		surgery.tools._req_release(1, Vector3.ZERO)
		await _frames(2)
	for id: String in Db.events.get_sections():
		surgery.director.fire(id, surgery)
	for drug: String in Db.drugs:
		surgery.patient.administer(drug, "direct")
	surgery.patient.iv_set = true
	surgery.patient.administer("saline", "iv")
	for i in 4:
		surgery.patient.shock(1.0)
	surgery.lab.request("full", surgery)
	surgery.nurse.request(1, "scalpel", surgery)
	surgery._req_turn()
	surgery._qte_result(3, false)
	await _frames(30)
	surgery.hud.open_manual()
	surgery.hud.open_card()
	surgery.hud.open_nurse()
	surgery.hud.close_overlay()
	surgery._finish(true, "")
	await _frames(5)
	print("    wounds=%d score=%d flags=%s" % [surgery.patient.wounds.size(), surgery.scoring.points, surgery.patient.flags.keys()])
	surgery.queue_free()
	await _frames(3)


func _frames(count: int) -> void:
	for i in count:
		await get_tree().physics_frame
