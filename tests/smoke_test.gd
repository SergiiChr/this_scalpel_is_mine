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
		me.hands[1].lowered = false
		surgery.tools._req_grab(tool.uid, 1)
		await _frames(2)
		var hand := me.hands[1]
		hand.attached = false
		hand.local_target = me.to_local(site + Vector3(0, 0.12, 0))
		hand.target = site + Vector3(0, 0.12, 0)
		# Lowered, worked at medium then full effort with the tool action held, moving along a blade's edge.
		hand.lowered = true
		for level in [2, 3]:
			hand.level = level
			hand.trigger = true
			for i in 20:
				hand.local_target += Vector3(0.0005, 0, 0.002)
				await get_tree().physics_frame
			hand.trigger = false
			await _frames(2)
		hand.lowered = false
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
	await _effect_checks(surgery)
	await _iodine_checks(surgery)
	await _syringe_checks(surgery)
	_nurse_checks(surgery)


## Every tool effect plays on the body, tools pick up blood and wash clean.
func _effect_checks(surgery: Surgery) -> void:
	var at := surgery.patient.body.uv_to_world(Vector2(0.5, 0.5))
	for kind in ["smoke", "dust", "spatter", "spark", "bead"]:
		surgery._effect(kind, at)
	await _frames(20)
	if surgery.get_node("Effects").get_child_count() == 0:
		print("FAIL: tool effects left nothing on screen")
	var me := surgery.local_surgeon
	var free: Array = surgery.tools.tools.values().filter(func(t: SurgicalTool) -> bool: return t.state == SurgicalTool.State.FREE and me.blocked_reason(t.def).is_empty())
	if not free.is_empty():
		var tool: SurgicalTool = free[0]
		surgery.tools._req_grab(tool.uid, 1)
		surgery.tools.add_blood(tool, 0.6)
		if tool.blood < 0.5:
			print("FAIL: working in blood didn't bloody the tool: ", tool.blood)
		await _frames(30)
		if me.hands[1].blood <= 0.0:
			print("FAIL: a bloody tool didn't bloody the glove holding it")
		surgery.tools._req_wash(1)
		if tool.blood > 0.0:
			print("FAIL: washing didn't take the blood off")
		surgery.tools._req_release(1, Vector3.ZERO)
		surgery.tools._req_wash(1)
		await _frames(2)
		if me.hands[1].blood > 0.0:
			print("FAIL: washing empty hands didn't clean the glove")
	surgery.patient.body.blood.splashed.emit(1.0)
	await _frames(2)
	if surgery.hud._lens_blood <= 0.0:
		print("FAIL: blood splashed on the view didn't show")
	await _control_checks(surgery)


## The wheel sets a lowered tool's effort level, and the on-screen controls follow a held hand key.
func _control_checks(surgery: Surgery) -> void:
	var me := surgery.local_surgeon
	var blades: Array = surgery.tools.tools.values().filter(func(t: SurgicalTool) -> bool: return t.state == SurgicalTool.State.FREE and t.def.action == "cut" and me.blocked_reason(t.def).is_empty())
	if blades.is_empty():
		return
	surgery.tools._req_grab((blades[0] as SurgicalTool).uid, me.active)
	await _frames(2)
	var hand := me.hands[me.active]
	var looking := Hud.control_lines(me)
	hand.lowered = true
	var wheel := InputEventAction.new()
	wheel.action = "zoom_in"
	wheel.pressed = true
	me._unhandled_input(wheel)
	if hand.level != 1:
		print("FAIL: the wheel didn't raise a lowered blade's depth: ", hand.level)
	hand.lowered = false
	Input.action_press("move_right_hand")
	if Hud.control_lines(me) == looking:
		print("FAIL: the controls shown didn't change while holding a hand key")
	Input.action_release("move_right_hand")
	surgery.tools._req_release(me.active, Vector3.ZERO)
	await _frames(2)


func _frames(count: int) -> void:
	for i in count:
		await get_tree().physics_frame


## The rolled tray holds the starter kit; iodine goes bottle to dish, soaks a pad held in forceps and sanitizes the skin.
func _iodine_checks(surgery: Surgery) -> void:
	var tools := surgery.tools
	var rolled := surgery.scenario.roll_tools(RandomNumberGenerator.new())
	if surgery.scenario.missing_tool_chance == 0.0 and Db.starter_kit.any(func(id: String) -> bool: return not rolled.has(id)):
		print("FAIL: the starter kit isn't all on the tray: ", rolled)
	var spot: Vector3 = surgery.room.tray_spots()[12]
	var made: Array[SurgicalTool] = []
	for id in ["forceps", "cotton_pad", "iodine_dish"]:
		tools.spawn(id, spot)
		made.append(tools.tools.values()[-1])
	var forceps := made[0]
	var pad := made[1]
	var dish := made[2]
	await _frames(10)
	if forceps.def.id != "forceps" or pad.def.id != "cotton_pad" or dish.def.id != "iodine_dish":
		print("FAIL: spawned the wrong tools for the iodine check")
		return
	var me := surgery.local_surgeon
	if not me.blocked_reason(forceps.def).is_empty():
		return
	tools._req_grab(forceps.uid, 1)
	tools.carry(pad, forceps)
	await _frames(3)
	if pad.state != SurgicalTool.State.CARRIED or pad.global_position.distance_to(forceps.tip_position()) > 0.05:
		print("FAIL: forceps didn't pick up the cotton pad")
	tools.set_fill(dish, 1.0)
	var dish_middle := dish.global_transform * Vector3(0, 0, -dish.def.length * 0.5)
	ToolActions._wipe(pad, "none", Vector2.ZERO, dish_middle, surgery.patient, 1.0, false)
	if pad.fill < 0.9 or dish.fill > 0.9:
		print("FAIL: the pad didn't soak up iodine from the dish: pad=%.2f dish=%.2f" % [pad.fill, dish.fill])
	surgery.patient._sanitized.fill(0.0)
	var uv := Vector2(0.5, 0.5)
	var wiped := surgery.patient.body.uv_to_world(uv)
	# A dish left beside the site must not turn the wipe into a dip.
	dish.global_position = wiped
	ToolActions._wipe(pad, "site", uv, wiped, surgery.patient, 0.5, false)
	if surgery.patient.sanitized_fraction() <= 0.0 or pad.fill >= 0.99:
		print("FAIL: the soaked pad didn't sanitize the skin")
	# A second of wiping, one physics frame at a time: no single frame may take a big bite out of the frame budget.
	tools.set_fill(pad, 1.0)
	var worst_ms := 0.0
	for i in 60:
		var at := Vector2(0.3 + i * 0.006, 0.5)
		var started := Time.get_ticks_usec()
		ToolActions._wipe(pad, "site", at, surgery.patient.body.uv_to_world(at), surgery.patient, 1.0 / 60.0, false)
		surgery.patient.body.wound_map.flush()
		worst_ms = maxf(worst_ms, (Time.get_ticks_usec() - started) / 1000.0)
	print("    iodine wipe: worst frame %.2f ms" % worst_ms)
	if worst_ms > 3.0:
		print("FAIL: wiping iodine takes %.2f ms in one frame (stutters)" % worst_ms)
	tools._req_release(1, Vector3.ZERO)
	await _frames(3)
	if pad.state != SurgicalTool.State.FREE:
		print("FAIL: the pad stayed on forceps that were let go")


## A syringe draws from a vial, a roughly right dose works and too little doesn't, drugs mix, the floor breaks it.
func _syringe_checks(surgery: Surgery) -> void:
	var tools := surgery.tools
	var patient := surgery.patient
	if patient.weight_kg < 15.0 or patient.weight_kg > 150.0:
		print("FAIL: odd patient weight %.0f kg" % patient.weight_kg)
	var spot: Vector3 = surgery.room.tray_spots()[13]
	var made: Array[SurgicalTool] = []
	for id in ["vial_propofol", "vial_morphine", "syringe_50"]:
		tools.spawn(id, spot)
		made.append(tools.tools.values()[-1])
	var vial := made[0]
	var syringe := made[2]
	await _frames(3)
	var hand := {"lowered": true, "trigger": false, "level": 3, "speed": 0.0, "peer": 1, "mods": Modifiers.new()}
	var vial_middle := vial.global_transform * Vector3(0, 0, -vial.def.length * 0.5)
	syringe.global_transform = Transform3D(Basis.IDENTITY, vial_middle + Vector3(0, 0, syringe.def.length))
	ToolActions.update(syringe, hand, patient, 1.0)
	var drawn := syringe.def.volume * ToolActions.PLUNGER_RATE
	if absf(syringe.ml - drawn) > 0.01 or absf(vial.ml - (vial.def.volume - drawn)) > 0.01 or syringe.label().ends_with("(empty)"):
		print("FAIL: the syringe didn't draw from the vial: syringe=%.2f ml vial=%.2f ml" % [syringe.ml, vial.ml])
	# The right dose, pushed into the patient, counts once the needle comes out.
	var right_ml := Db.drug("propofol").dose * patient.weight_kg / vial.def.concentration
	tools.transfer(vial, syringe, right_ml - syringe.ml)
	patient.flags.erase("drug_propofol")
	var site := patient.body.uv_to_world(Vector2(0.5, 0.5))
	syringe.global_transform = Transform3D(Basis.IDENTITY, site + Vector3(0, 0, syringe.def.length))
	ToolActions.update(syringe, hand, patient, 4.0)
	hand.lowered = false
	ToolActions.update(syringe, hand, patient, 0.1)
	if syringe.ml > 0.0 or not patient.flags.has("drug_propofol"):
		print("FAIL: the right dose of propofol didn't count: left=%.2f ml flags=%s" % [syringe.ml, patient.flags.keys()])
	# A third of the dose doesn't do the job.
	patient.flags.erase("drug_propofol")
	tools.transfer(vial, syringe, right_ml * 0.3)
	syringe.injecting = tools.transfer(syringe, null, syringe.ml)
	ToolActions.finish_injection(syringe, patient)
	if patient.flags.has("drug_propofol"):
		print("FAIL: a third of the dose counted as a full one")
	# Two vials into one syringe make a mix.
	tools.transfer(vial, syringe, 1.0)
	tools.transfer(made[1], syringe, 1.0)
	if not (syringe.contents.has("propofol") and syringe.contents.has("morphine")) or absf(syringe.ml - 2.0) > 0.01:
		print("FAIL: drugs from two vials didn't mix: ", syringe.contents)
	# Dropped on the floor, it shatters.
	syringe.global_position = surgery.room.spawn_transform(1).origin + Vector3(0, 0.6, 0)
	syringe.linear_velocity = Vector3.ZERO
	syringe.set_meta("falling", true)
	await _frames(90)
	if syringe.state != SurgicalTool.State.CONSUMED:
		print("FAIL: a syringe dropped on the floor didn't break")


## One order at a time: the board shows it on its way, the cooldown starts after the delivery.
func _nurse_checks(surgery: Surgery) -> void:
	var nurse := surgery.nurse
	nurse.tick(1000.0, surgery)
	nurse.cooldown_left = 0.0
	nurse.request(1, "gauze", surgery)
	if nurse.order().is_empty() or not Room.nurse_board_text({"order": nurse.order()}).contains("Gauze"):
		print("FAIL: the nurse board doesn't show the order on its way")
	nurse.tick(1000.0, surgery)
	if not nurse.order().is_empty() or nurse.cooldown_left <= 0.0:
		print("FAIL: the nurse cooldown didn't start after the delivery")
	nurse.request(1, "gauze", surgery)
	if not nurse.order().is_empty():
		print("FAIL: the nurse took an order during her cooldown")
