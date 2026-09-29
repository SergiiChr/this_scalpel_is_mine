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
	Net.run_modifiers = Array(Db.run_modifiers.get_sections())
	var surgery: Surgery = SURGERY.instantiate()
	add_child(surgery)
	await _frames(5)
	assert(surgery.running, "surgery did not start")
	_check_defib_cart(surgery)
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
	await _new_mechanics(surgery)
	await _feedback_checks(surgery)
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


func _new_mechanics(surgery: Surgery) -> void:
	var patient := surgery.patient
	var needle := Db.tool("needle")
	for wound in patient.wounds:
		if not wound.is_internal() and wound.points.size() > 1:
			for pressure in [1, 2, 3]:
				for i in 30:
					patient.close_at(wound.midpoint(), needle, 0.1, 1.0, pressure)
	for organ in patient.body.organs:
		organ.position += Vector3(0.05, 0.0, 0.0)
	patient._handle_organs(6.0)
	surgery.tools._req_pass(1)
	surgery.correct_chart()
	surgery.hud.open_card()
	surgery.hud.close_overlay()
	var cart := surgery.room.xray
	if cart:
		cart._req_push()
		await _frames(10)
		cart._req_push()
		cart.global_position = patient.global_position + Vector3(0.0, -Room.TABLE_HEIGHT, 1.0)
		cart._req_expose()
		await _frames(int(XrayCart.EXPOSE_TIME * 60) + 10)
		assert(not cart.print_data.is_empty(), "x-ray print missing")
		surgery.hud.open_xray(cart)
		await _frames(3)
		surgery.hud.close_overlay()


## Before anything gets moved: the defibrillator waits on its cart.
func _check_defib_cart(surgery: Surgery) -> void:
	if not surgery.room.layout.has("defib_cart"):
		return
	var cart: Vector3 = surgery.room.layout.defib_cart
	var on_cart := surgery.tools.tools.values().any(func(t: SurgicalTool) -> bool:
		return t.def.id == "defibrillator" and Vector2(t.global_position.x - cart.x, t.global_position.z - cart.z).length() < 0.4)
	if not on_cart:
		print("FAIL: no defibrillator on the defib cart")


## Deliveries, floor dirt and the IV line. Prints FAIL: lines instead of asserting, so one run shows them all.
func _feedback_checks(surgery: Surgery) -> void:
	var room := surgery.room
	var tools := surgery.tools
	# A nurse delivery ends up lying on the delivery tray.
	if room.layout.has("delivery_tray"):
		var before := tools.tools.size()
		tools.spawn("gauze", room.delivery_spot())
		await _frames(90)
		var delivered: SurgicalTool = tools.tools.values()[before] if tools.tools.size() > before else null
		var tray: Vector3 = room.layout.delivery_tray
		if delivered == null or delivered.global_position.y < 0.85 or Vector2(delivered.global_position.x - tray.x, delivered.global_position.z - tray.z).length() > 0.35:
			print("FAIL: delivery didn't land on the delivery tray: ", delivered.global_position if delivered else "nothing spawned")
	# Floor dirt: the sanitizer refuses a soiled tool until it's been washed.
	var me := surgery.local_surgeon
	var free: Array = tools.tools.values().filter(func(t: SurgicalTool) -> bool: return t.state == SurgicalTool.State.FREE and me.blocked_reason(t.def).is_empty())
	var tool: SurgicalTool = free[0] if not free.is_empty() else null
	if tool:
		tools._req_grab(tool.uid, 1)
		tools._set_sterile(tool.uid, false)
		tools._set_soiled(tool.uid, true)
		tools._req_sterilize(1)
		if tool.sterile:
			print("FAIL: sanitizer made a soiled tool sterile")
		tools._req_wash(1)
		tools._req_sterilize(1)
		if tool.soiled or not tool.sterile:
			print("FAIL: washing then sanitizing didn't clean the tool")
		tools._req_release(1, Vector3.ZERO)
	# Walking into the IV tubing at full speed rips the line out.
	var patient := surgery.patient
	patient.set_iv(patient.body.root().to_global(Patient.PREOP_IV_POINT))
	await _frames(5)
	var low: PackedVector3Array = room.iv_line._points
	var crossing := Vector3.INF
	for p in low:
		if p.y < IvLine.TRIP_HEIGHT and (crossing == Vector3.INF or p.y < crossing.y):
			crossing = p
	if crossing != Vector3.INF:
		var from := Vector3(crossing.x - 0.5, 0.0, crossing.z)
		for i in 34:
			me.global_position = from + Vector3(i * 0.03, 0.0, 0.0)
			await get_tree().physics_frame
		if patient.iv_set:
			print("FAIL: walking through the IV line didn't pull it out")
	else:
		print("FAIL: the IV line doesn't hang low enough to trip on")


func _frames(count: int) -> void:
	for i in count:
		await get_tree().physics_frame
