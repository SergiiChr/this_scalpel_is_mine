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
	# First, while the site is still whole and the tray untouched.
	await _table_checks(surgery, scenario.order == 1)
	await _anatomy_checks(surgery)
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
	surgery.patient.iv_in_vein = true
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
	_muscle_first_checks(patient)
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


## A cut through the muscle: the skin won't close over it and a tight stitch tears, until the muscle is sewn.
func _muscle_first_checks(patient: Patient) -> void:
	var needle := Db.tool("needle")
	patient.cut(424242, Vector2(0.3, 0.2), Vector2(0.7, 0.2), 1.0, 1.0, false, 0.1)
	var wound: Wound = patient._stroke_wounds[424242]
	if not wound.through_muscle():
		return
	for bin in wound.bins.size():
		for i in 20:
			patient.close_at(wound.bin_position(bin), needle, 0.1, 1.0, 2)
	if wound.closure() > 0.0:
		print("FAIL: the skin closed over open muscle (closure %.2f)" % wound.closure())
	var tears: float = patient.flags.get("tears", 0.0)
	for i in 20:
		patient.close_at(wound.midpoint(), needle, 0.1, 1.0, 3)
	if patient.flags.get("tears", 0.0) <= tears:
		print("FAIL: a tight stitch over open muscle didn't tear")
	for bin in wound.bins.size():
		for i in 20:
			patient.close_muscle_at(wound.bin_position(bin), needle, 0.1)
	if patient.body.tissue.muscle_open_near(wound.midpoint(), Patient.MUSCLE_REACH):
		print("FAIL: sewing inside the wound didn't close the muscle")
	for bin in wound.bins.size():
		for i in 20:
			patient.close_at(wound.bin_position(bin), needle, 0.1, 1.0, 2)
	if wound.closure() < 0.9:
		print("FAIL: the skin didn't close over sewn muscle (closure %.2f)" % wound.closure())


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
	# The catheter pressed on the patient earlier may already have put a line in somewhere else (it depends on where
	# the shaky tip landed): take it out, so the line runs to the back of the hand like a pre-op one.
	if patient.iv_set:
		patient._iv_removed()
	patient.set_iv(patient.body.root().to_global(Patient.PREOP_IV_POINT), true)
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
		var line := room.iv_line
		print("FAIL: the IV line doesn't hang low enough to trip on (attached %s, iv set %s, ends %s, %d points)" % [line.is_attached(), patient.iv_set, line._last_ends, low.size()])
	await _effect_checks(surgery)
	await _iodine_checks(surgery)
	await _syringe_checks(surgery)
	await _tourniquet_checks(surgery)
	_nurse_checks(surgery)
	await _smoking_checks(surgery)


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
		# With every quirk on, a sweaty glove or a cough can make it slip meanwhile: then it's picked up again.
		if me.held_tool(1) != tool:
			surgery.tools._req_grab(tool.uid, 1)
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


## Controls: RMB picks up and puts down, LMB lowers and works the tool, the wheel sets its level, Shift steps the
## zoom through three levels, and the controls shown follow a held hand key.
func _control_checks(surgery: Surgery) -> void:
	var me := surgery.local_surgeon
	var blades: Array = surgery.tools.tools.values().filter(func(t: SurgicalTool) -> bool: return t.state == SurgicalTool.State.FREE and t.def.action == "cut" and me.blocked_reason(t.def).is_empty())
	if blades.is_empty():
		return
	var blade: SurgicalTool = blades[0]
	var hand := me.hands[me.active]
	me.hovered = null
	hand.local_target = me.to_local(blade.global_position - hand.tip_offset(0.05))
	hand.target = me.to_global(hand.local_target)
	hand.global_position = hand.target
	me._unhandled_input(_action("grab", true))
	await _frames(2)
	if me.held_tool(me.active) != blade:
		print("FAIL: RMB (grab) didn't pick up the tool under the hand")
		return
	var looking := Hud.control_lines(me)
	me._unhandled_input(_action("level_up", true))
	if hand.level != 1:
		print("FAIL: the wheel didn't raise a blade's depth: ", hand.level)
	me._unhandled_input(_action("use_tool", true))
	if not (hand.lowered and hand.trigger):
		print("FAIL: LMB (use) didn't lower and work the tool")
	me._unhandled_input(_action("use_tool", false))
	if hand.lowered or hand.trigger:
		print("FAIL: letting go of LMB left the tool working")
	var zooms: Array[int] = []
	for i in Surgeon.ZOOM_FOV.size():
		me._unhandled_input(_action("zoom", true))
		zooms.append(me.zoom)
	if Surgeon.ZOOM_FOV.size() != 3 or zooms != [1, 2, 0]:
		print("FAIL: Shift doesn't step through three zoom levels: ", zooms)
	Input.action_press("move_right_hand")
	if Hud.control_lines(me) == looking:
		print("FAIL: the controls shown didn't change while holding a hand key")
	Input.action_release("move_right_hand")
	me._unhandled_input(_action("grab", true))
	await _frames(2)
	if me.held_tool(me.active) != null:
		print("FAIL: RMB (grab) didn't put the tool down")


static func _action(action: String, pressed: bool) -> InputEventAction:
	var event := InputEventAction.new()
	event.action = action
	event.pressed = pressed
	return event


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
	# Best of three runs, so a busy machine doesn't fail it.
	var worst_ms := INF
	for run in 3:
		tools.set_fill(pad, 1.0)
		var run_worst := 0.0
		for i in 60:
			var at := Vector2(0.3 + i * 0.006, 0.5)
			var started := Time.get_ticks_usec()
			ToolActions._wipe(pad, "site", at, surgery.patient.body.uv_to_world(at), surgery.patient, 1.0 / 60.0, false)
			surgery.patient.body.wound_map.flush()
			run_worst = maxf(run_worst, (Time.get_ticks_usec() - started) / 1000.0)
		worst_ms = minf(worst_ms, run_worst)
	print("    iodine wipe: worst frame %.2f ms" % worst_ms)
	if worst_ms > 4.0:
		print("FAIL: wiping iodine takes %.2f ms in one frame (stutters)" % worst_ms)
	tools._req_release(1, Vector3.ZERO)
	await _frames(3)
	# Let go over an opened chest or belly, the pad falls in: that's fine, it isn't on the forceps.
	if pad.state == SurgicalTool.State.CARRIED:
		print("FAIL: the pad stayed on forceps that were let go")


## A syringe draws from a vial, a roughly right dose works and too little doesn't, drugs mix, the floor breaks it.
func _syringe_checks(surgery: Surgery) -> void:
	var tools := surgery.tools
	var patient := surgery.patient
	if patient.weight_kg < 15.0 or patient.weight_kg > 150.0:
		print("FAIL: odd patient weight %.0f kg" % patient.weight_kg)
	var spot: Vector3 = surgery.room.tray_spots()[17]
	var made: Array[SurgicalTool] = []
	for id in ["vial_propofol", "vial_morphine", "syringe_50"]:
		tools.spawn(id, spot)
		made.append(tools.tools.values()[-1])
	var vial := made[0]
	var syringe := made[2]
	await _frames(3)
	# Three wheel notches with the needle in the vial draw 3 ml.
	var vial_middle := vial.global_transform * Vector3(0, 0, -vial.def.length * 0.5)
	syringe.global_transform = Transform3D(Basis.IDENTITY, vial_middle + Vector3(0, 0, syringe.def.length))
	for i in 3:
		ToolActions.plunge(syringe, ToolActions.PLUNGER_STEP, patient)
	var drawn := 3.0 * ToolActions.PLUNGER_STEP
	if absf(syringe.ml - drawn) > 0.01 or absf(vial.ml - (vial.def.volume - drawn)) > 0.01 or syringe.label().ends_with("(empty)"):
		print("FAIL: the syringe didn't draw from the vial: syringe=%.2f ml vial=%.2f ml" % [syringe.ml, vial.ml])
	# The right dose, pushed into the patient, counts once the needle comes out.
	var right_ml := Db.drug("propofol").dose * patient.weight_kg / vial.def.concentration
	tools.transfer(vial, syringe, right_ml - syringe.ml)
	patient.flags.erase("drug_propofol")
	var site := patient.body.uv_to_world(Vector2(0.5, 0.5))
	syringe.global_transform = Transform3D(Basis.IDENTITY, site + Vector3(0, 0, syringe.def.length))
	# Tools dropped on the site earlier (the kidney dish) would catch the needle: put them back on the tray.
	var in_way := tools.nearest_container(site)
	while in_way:
		in_way.global_position = spot + Vector3.UP * 0.1
		in_way = tools.nearest_container(site)
	while syringe.ml > 0.0:
		ToolActions.plunge(syringe, -ToolActions.PLUNGER_STEP, patient)
	syringe.global_position += Vector3.UP * 0.3
	var hand := {"lowered": false, "trigger": false, "level": 0, "speed": 0.0, "peer": 1, "mods": Modifiers.new()}
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
	await _inspect_checks(surgery, syringe, spot)
	# Dropped on the floor, it shatters.
	syringe.global_position = surgery.room.spawn_transform(1).origin + Vector3(0, 0.6, 0)
	syringe.linear_velocity = Vector3.ZERO
	syringe.sleeping = false
	# Let the physics server receive the teleported, awake body before marking
	# it as a live drop; otherwise stale tray contacts can settle it immediately.
	await _frames(1)
	syringe.set_meta("falling", true)
	await _frames(90)
	if syringe.state != SurgicalTool.State.CONSUMED:
		print("FAIL: a syringe dropped on the floor didn't break")


## Holding Inspect brings a syringe up in front of the eyes, across the view with the hand behind it,
## and the liquid and plunger show exactly how many ml are in it against the graduation.
func _inspect_checks(surgery: Surgery, syringe: SurgicalTool, put_back: Vector3) -> void:
	var me := surgery.local_surgeon
	if not me.blocked_reason(syringe.def).is_empty():
		return
	var hand := me.active
	surgery.tools._req_grab(syringe.uid, hand)
	await _frames(2)
	if me.held_tool(hand) != syringe:
		print("FAIL: couldn't pick up the syringe to look at it")
		return
	Input.action_press("inspect")
	await _frames(10)
	var camera := me.camera()
	var barrel_ends: Array[Vector3] = [syringe.global_transform * Vector3.ZERO, syringe.tip_position()]
	for end in barrel_ends:
		if not camera.is_position_in_frustum(end) or camera.global_position.distance_to(end) > 0.45:
			print("FAIL: the syringe held up to look at isn't in view close up: %s" % end)
	# The graduation runs all the way round the barrel; the hand has to be behind it, not in front of it.
	var barrel := syringe.global_transform * Vector3(0, 0, -syringe.def.length * 0.35)
	var glove := me.hands[hand]._glove.global_transform * Vector3(0.05, 0.0, 0.0)
	if camera.global_position.distance_to(glove) < camera.global_position.distance_to(barrel) + 0.01:
		print("FAIL: the hand holding the syringe up is in front of the barrel")
	var across := absf(syringe.global_basis.z.normalized().dot(camera.global_basis.x.normalized()))
	if across < 0.9:
		print("FAIL: the syringe isn't held across the view (%.2f)" % across)
	# The liquid runs from the needle end to the plunger, a tick every tenth of the volume.
	var level := syringe.find_child("Level", true, false) as MeshInstance3D
	var plunger := syringe.find_child("Plunger", true, false) as Node3D
	# The full Level part spans the ticks from empty to full; how much of it shows is how full the syringe reads.
	var travel := level.get_aabb().size.z
	var liquid := level.get_aabb().size.z * level.scale.z if level.visible else 0.0
	var shown_ml := liquid / travel * syringe.def.volume
	if absf(shown_ml - syringe.ml) > syringe.def.volume * 0.02:
		print("FAIL: the syringe reads %.2f ml against its ticks but holds %.2f ml" % [shown_ml, syringe.ml])
	# The plunger's stopper starts at the needle end (its rest) and sits right behind the liquid and any air.
	if absf(plunger.position.z - liquid - travel * syringe.air / syringe.def.volume) > travel * 0.02:
		print("FAIL: the plunger doesn't sit right behind the liquid (pulled back %.4f m, liquid %.4f m)" % [plunger.position.z, liquid])
	Input.action_release("inspect")
	await _frames(2)
	# Put down on the tray, not into whatever is open under the hand.
	surgery.tools._req_release(hand, Vector3.ZERO)
	syringe.global_position = put_back + Vector3.UP * 0.05
	syringe.remove_meta("falling")
	await _frames(2)


## A tourniquet pressed onto a thigh wraps around it: a band snug around the leg, not lying on top of it.
## Taking it off loosens it again.
func _tourniquet_checks(surgery: Surgery) -> void:
	var tools := surgery.tools
	var patient := surgery.patient
	var me := surgery.local_surgeon
	tools.spawn("tourniquet", surgery.room.tray_spots()[7])
	var tourniquet: SurgicalTool = tools.tools.values()[-1]
	await _frames(3)
	if not me.blocked_reason(tourniquet.def).is_empty():
		return
	var space := patient.get_world_3d().direct_space_state
	# Straight above the thigh, whichever way up the patient lies.
	var above := patient.body.root().to_global(Vector3(-0.75, 0.0, 0.1)) + Vector3.UP * 0.4
	var down := PhysicsRayQueryParameters3D.create(above, above + Vector3.DOWN * 0.8, PatientBody.SURFACE_LAYER)
	var skin := space.intersect_ray(down)
	if skin.is_empty():
		print("FAIL: no thigh to put the tourniquet on")
		return
	var top: Vector3 = skin.position
	patient.tourniquet_on = false
	tools._req_grab(tourniquet.uid, 1)
	# Pointing straight down onto the top of the thigh.
	tourniquet.global_transform = Transform3D(Basis(Vector3.RIGHT, Vector3.FORWARD, Vector3.UP), top + Vector3.UP * tourniquet.def.length)
	var hand := {"lowered": true, "trigger": true, "level": 0, "speed": 0.0, "peer": 1, "mods": Modifiers.new()}
	var tip := tourniquet.tip_position()
	ToolActions.update(tourniquet, hand, patient, 1.0 / 60.0)
	await _frames(2)
	if not patient.tourniquet_on or tourniquet.band == null:
		print("FAIL: the tourniquet didn't go on the thigh at %s (on=%s, part %s, ring %s)" % [tip, patient.tourniquet_on, patient.body.part_at(tip, 0.08), patient.body.limb_ring(tip)])
		tools._req_release(1, Vector3.ZERO)
		return
	var torus := tourniquet.band.mesh as TorusMesh
	var center := tourniquet.band.global_position
	var axis := tourniquet.band.global_basis.y.normalized()
	if absf(axis.dot(patient.body.root().global_basis.x.normalized())) < 0.95:
		print("FAIL: the tourniquet band doesn't run around the leg (axis %s)" % axis)
	if center.y > top.y - 0.03:
		print("FAIL: the tourniquet lies on top of the leg instead of around it (center %.3f, skin on top %.3f)" % [center.y, top.y])
	# Snug: the band's inside passes just over the top of the thigh.
	if absf(center.y + torus.inner_radius - top.y) > 0.015:
		print("FAIL: the tourniquet band isn't snug on the thigh (inside at %.3f, skin at %.3f)" % [center.y + torus.inner_radius, top.y])
	if tourniquet.state != SurgicalTool.State.STANDING or tools.tool_in_hand(1, 1) != null:
		print("FAIL: the hand still holds the tourniquet once it's on")
	tools._req_grab(tourniquet.uid, 1)
	await _frames(2)
	if patient.tourniquet_on or tourniquet.band != null:
		print("FAIL: taking the tourniquet off didn't loosen it")
	tools._req_release(1, Vector3.ZERO)
	await _frames(2)


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


## The smoking spot is offered only to a hand holding cigarettes; a smoke uses one, stops stress and speeds you up.
func _smoking_checks(surgery: Surgery) -> void:
	var me := surgery.local_surgeon
	var spot := surgery.room.find_child("SmokeACigarette", false, false) as Interactable
	surgery.tools._req_release(me.active, Vector3.ZERO)
	if spot == null or spot.offered_to(me):
		print("FAIL: the smoking spot is missing or offered to an empty hand")
		return
	var before := surgery.tools.tools.size()
	surgery.tools.spawn("cig_pack", me.global_position + Vector3(0.0, 1.0, 0.0))
	var pack: SurgicalTool = surgery.tools.tools.values()[before]
	surgery.tools._req_grab(pack.uid, me.active)
	await _frames(2)
	if not spot.offered_to(me):
		print("FAIL: the smoking spot isn't offered to a hand holding cigarettes")
	var speed := me.status.move_speed()
	spot.interact(me)
	await _frames(2)
	var stress := me.status.stress
	me.status.add_stress(0.3)
	if pack.charges != pack.def.charges - 1 or me.status.stress > stress or me.status.move_speed() <= speed:
		print("FAIL: smoking didn't use a cigarette, stop stress and speed you up (%d left)" % pack.charges)
	me.status.smoke_left = 0.0
	surgery.tools._req_release(me.active, Vector3.ZERO)


## The lowest point of a node's meshes, world space.
static func lowest_point(node: Node3D) -> float:
	var lowest := INF
	for child in node.find_children("*", "MeshInstance3D", true, false):
		var mesh := child as MeshInstance3D
		if not mesh.is_visible_in_tree():
			continue
		var xform := mesh.global_transform
		for v in mesh.mesh.get_faces():
			lowest = minf(lowest, (xform * v).y)
	return lowest


## Height of the room (table, tray, floor) under p, ignoring tools and the patient.
static func support_below(node: Node3D, p: Vector3) -> float:
	var query := PhysicsRayQueryParameters3D.create(p + Vector3.UP * 0.3, p + Vector3.DOWN * 3.0, 1)
	var hit := node.get_world_3d().direct_space_state.intersect_ray(query)
	return hit.position.y if not hit.is_empty() else -INF


## Tools lie on the tray, never in it: at the start, lowered onto it with full effort (the tip only presses into
## skin), and once put down. The glove stays out of it too. all: every kind of tool, otherwise one.
func _table_checks(surgery: Surgery, all: bool) -> void:
	await _frames(60)
	var tools := surgery.tools
	for tool: SurgicalTool in tools.tools.values():
		if tool.state == SurgicalTool.State.FREE:
			var below := support_below(tool, tool.global_transform * tool.bounds.get_center())
			if lowest_point(tool) < below - 0.003:
				print("FAIL: the %s sinks %.1f mm into what it lies on (at %s, touching %s, sleeping %s, basis %s)" % [tool.def.id, (below - lowest_point(tool)) * 1000.0, tool.global_position, tool.get_colliding_bodies().map(func(b: Node) -> String: return str(b.name)), tool.sleeping, tool.global_basis])
	var me := surgery.local_surgeon
	var hand := me.hands[1]
	var tray: Vector3 = surgery.room.layout.tray + Vector3(0.0, 0.93, 0.1)
	# Standing at the tray, facing it.
	var standing := me.global_transform
	me.global_position = Vector3(tray.x + 0.45, me.global_position.y, tray.z)
	me.look_at(Vector3(tray.x, me.global_position.y, tray.z))
	await _frames(2)
	var seen := {}
	for tool: SurgicalTool in tools.tools.values().duplicate():
		if tool.state != SurgicalTool.State.FREE or seen.has(tool.def.id) or not me.blocked_reason(tool.def).is_empty():
			continue
		seen[tool.def.id] = true
		tools._req_grab(tool.uid, 1)
		await _frames(2)
		hand.attached = false
		hand.local_target = me.to_local(tray + Vector3(0.0, 0.1, 0.0))
		hand.lowered = true
		hand.level = 3
		await _frames(20)
		var top := support_below(tool, tool.tip_position())
		if lowest_point(tool) < top - 0.003:
			print("FAIL: the %s lowered onto the tray goes %.1f mm into it" % [tool.def.id, (top - lowest_point(tool)) * 1000.0])
		for point: Array in hand.bone_points():
			var p: Vector3 = point[0]
			if p.y - float(point[1]) < support_below(tool, p) - 0.003:
				print("FAIL: the glove holding the %s goes into the tray" % tool.def.id)
				break
		hand.lowered = false
		hand.level = 0
		tools._req_release(1, Vector3.ZERO)
		# Time to settle: a tool can land on its edge and roll over first.
		await _frames(150)
		# Lying on the tray itself (one tilted across another tool touches both, and that's a different contact).
		var on_tray := tool.get_colliding_bodies().all(func(b: Node) -> bool: return not b is SurgicalTool)
		if tool.state == SurgicalTool.State.FREE and on_tray:
			var below := support_below(tool, tool.global_transform * tool.bounds.get_center())
			if lowest_point(tool) < below - 0.003:
				print("FAIL: the %s put down sinks %.1f mm into what it lies on (at %s, touching %s, sleeping %s, basis %s, bounds %s, shapes %s)" % [tool.def.id, (below - lowest_point(tool)) * 1000.0, tool.global_position, tool.get_colliding_bodies().map(func(b: Node) -> String: return str(b.name)), tool.sleeping, tool.global_basis, tool.bounds, tool.find_children("*", "CollisionShape3D", false, false).map(func(c: Node) -> String: return "%s %s" % [(c as CollisionShape3D).shape.get("size"), (c as CollisionShape3D).position])])
		if not all:
			break
	me.global_transform = standing
	await _frames(2)


## Opens the site as wide as it goes, like a clamshell: an H-shaped incision through the muscle, both flaps folded
## back over the sides they're still attached to, slowly, by forceps all along both edges that keep holding them.
## Returns how many springs tore on the way.
static func open_wide(patient: Patient) -> int:
	var middle := 0.51
	for cut: Array in [[Vector2(0.03, middle), Vector2(0.97, middle)], [Vector2(0.03, 0.02), Vector2(0.03, 0.98)], [Vector2(0.97, 0.02), Vector2(0.97, 0.98)]]:
		patient.cut(777000 + roundi(cut[0].y * 100.0), cut[0], cut[1], 1.0, 1.0, false, 0.1)
	var tissue := patient.body.tissue
	var snapped := tissue.snapped.size()
	# Thin skin (a patient quirk) is meant to tear on a fold like this; this is about ordinary skin.
	var break_mult := tissue.break_mult
	tissue.break_mult = maxf(break_mult, 1.0)
	var grips: Array = []
	# The flap turns up and over about the side of the site it's still attached to: one straight hinge along that
	# side, where it lies on the body on average (the body's flank drops away more under some of it than the rest).
	var hinges: Array[Vector3] = []
	for row: int in [0, tissue.res_y]:
		var sum := Vector3.ZERO
		var count := 0
		for i in tissue.res_x + 1:
			var k := tissue.index(i, row)
			if tissue.off[k] == 0:
				sum += tissue.rest[k]
				count += 1
		hinges.append(sum / maxi(count, 1))
	# Forceps about every 25 mm along each edge.
	var spacing := maxi(1, roundi(0.025 / (patient.body.site_size.x / tissue.res_x)))
	for i in range(1, tissue.res_x, spacing):
		for edge: int in [floori(middle * tissue.res_y), ceili(middle * tissue.res_y)]:
			var k := tissue.index(i, edge)
			var key := 88000 + grips.size()
			tissue.grip(key, tissue.uv_of(k))
			var side := -1.0 if edge < middle * tissue.res_y else 1.0
			var line := hinges[0 if side < 0.0 else 1]
			var hinge := Vector3(tissue.rest[k].x, line.y, line.z)
			grips.append([key, hinge, tissue.rest[k] - hinge, side])
	for step in 240:
		var angle := PI * 0.85 * (step + 1) / 240.0
		for grip: Array in grips:
			tissue.move_grip(grip[0], (grip[1] as Vector3) + (grip[2] as Vector3).rotated(Vector3.RIGHT, float(grip[3]) * angle))
		tissue._substep()
	for i in 60:
		tissue._substep()
	tissue.break_mult = break_mult
	return tissue.snapped.size() - snapped


## True where skin, fat or muscle is drawn over the point uv: the body model outside the simulated region, or a
## triangle of one of the simulated layers, where the sim has pulled it now.
static func soft_tissue_over(body: PatientBody, uv: Vector2) -> bool:
	var tissue := body.tissue
	var region := tissue.region()
	var grid := uv * Vector2(tissue.res_x, tissue.res_y)
	var corners := 0.0
	for c: Vector2i in [Vector2i(0, 0), Vector2i(1, 0), Vector2i(0, 1), Vector2i(1, 1)]:
		var at := (Vector2i(grid.floor()) + c).clamp(Vector2i.ZERO, Vector2i(tissue.res_x, tissue.res_y))
		corners += region[tissue.index(at.x, at.y)]
	if corners < 2.0:
		return true
	var point := Vector2((uv.x - 0.5) * body.site_size.x, (uv.y - 0.5) * body.site_size.y)
	for layer in 3:
		var tris := tissue.triangles(PatientBody.LAYER_DEPTH[layer])
		for t in range(0, tris.size(), 3):
			var p: Array[Vector2] = []
			for k in 3:
				var v := body.layer_point(layer, tris[t + k])
				p.append(Vector2(v.x, v.z))
			if Geometry2D.point_is_inside_triangle(point, p[0], p[1], p[2]):
				return true
	return false


## Points (uv) spread over what an organ covers seen from above: its middle and toward the ends of its box.
static func organ_footprint(body: PatientBody, organ: RigidBody3D) -> Array[Vector2]:
	var shape: CollisionShape3D = organ.get_child(organ.get_child_count() - 1)
	var size: Vector3 = (shape.shape as BoxShape3D).size if shape.shape is BoxShape3D else Vector3.ONE * 0.02
	var out: Array[Vector2] = []
	for offset: Vector3 in [Vector3.ZERO, Vector3(0.35, 0, 0), Vector3(-0.35, 0, 0), Vector3(0, 0, 0.35), Vector3(0, 0, -0.35)]:
		var local := organ.transform * (shape.position + offset * size)
		out.append(Vector2(local.x / body.site_size.x + 0.5, local.z / body.site_size.y + 0.5))
	return out


## What a ray straight down onto uv meets first inside the body: an organ index, -2 for a bone, -1 for nothing.
static func first_inside(body: PatientBody, uv: Vector2) -> int:
	var from := body.site.to_global(Vector3((uv.x - 0.5) * body.site_size.x, 0.1, (uv.y - 0.5) * body.site_size.y))
	var query := PhysicsRayQueryParameters3D.create(from, from - body.site.global_basis.y * 0.4, PatientBody.CAVITY_LAYER)
	var hit := body.get_world_3d().direct_space_state.intersect_ray(query)
	if hit.is_empty():
		return -1
	return -2 if (hit.collider as Object).has_meta("bone") else body.organs.find(hit.collider as RigidBody3D)


## Chest and belly: opened wide, the top layer of organs and the ribs show with no skin, fat or muscle over them.
## Taking hold of a top organ and moving it aside shows the one under it. The heart beats and the lungs breathe.
func _anatomy_checks(surgery: Surgery) -> void:
	var patient := surgery.patient
	var body := patient.body
	var anatomy: Dictionary = Db.patient_sites.get(surgery.scenario.site, {}).get("anatomy", {})
	if anatomy.has("organs"):
		if body.organs.filter(func(o: RigidBody3D) -> bool: return o.has_meta("kind") and int(o.get_meta("layer")) == 1).is_empty():
			print("FAIL: the %s has no organs under the top layer" % surgery.scenario.site)
		var torn := open_wide(patient)
		if torn > 0:
			print("FAIL: folding the flaps of the %s back tore %d springs" % [surgery.scenario.site, torn])
		await _frames(2)
		# --coverage draws what's still covered: one row per uv.x (feet first), # where soft tissue is in the way.
		if OS.get_cmdline_user_args().has("--coverage"):
			for j in 25:
				var line := ""
				for i in 25:
					line += "#" if soft_tissue_over(body, Vector2(j / 24.0, i / 24.0)) else "."
				print("    ", line)
		for organ in body.organs:
			if int(organ.get_meta("layer", 0)) != 0:
				continue
			var hidden := organ_footprint(body, organ).filter(func(uv: Vector2) -> bool: return soft_tissue_over(body, uv))
			if not hidden.is_empty():
				print("FAIL: the open %s still hides the %s under soft tissue at %s" % [surgery.scenario.site, organ.get_meta("kind", organ.name), hidden])
		for bone in body.bones:
			var middle := (bone.get_child(bone.get_child_count() / 2) as Node3D).position
			var uv := Vector2(middle.x / body.site_size.x + 0.5, middle.z / body.site_size.y + 0.5)
			if uv.x > 0.05 and uv.x < 0.95 and soft_tissue_over(body, uv):
				print("FAIL: the open %s still hides %s under soft tissue at %s" % [surgery.scenario.site, bone.name, uv])
		await _move_top_organ(surgery)
	if anatomy.has("organs") or anatomy.has("bones"):
		await _bone_checks(surgery)
	await _organ_motion_checks(surgery)


## Grips the top organ over a lower one with forceps, moves it aside, and the lower one shows.
func _move_top_organ(surgery: Surgery) -> void:
	var patient := surgery.patient
	var body := patient.body
	for lower in body.organs:
		if int(lower.get_meta("layer", 0)) != 1:
			continue
		for uv in organ_footprint(body, lower):
			var top := first_inside(body, uv)
			if top < 0 or top == body.organs.find(lower):
				continue
			var organ := body.organs[top]
			var organ_uv := Vector2(organ.position.x / body.site_size.x + 0.5, organ.position.z / body.site_size.y + 0.5)
			var depth := body.surface_height(organ_uv) - organ.position.y
			var grip := patient.grip(99001, "cavity", organ_uv, depth)
			if grip.get("type") != "organ" or grip.organ != top:
				var at := body.uv_to_world(organ_uv, depth)
				var shape: CollisionShape3D = organ.get_child(organ.get_child_count() - 1)
				print("FAIL: forceps in the open %s didn't take hold of the %s: %s (at %s, organ %s, box %s at %s, organ_at %d)" % [surgery.scenario.site, organ.get_meta("kind"), grip, at, organ.global_position, (shape.shape as BoxShape3D).size, shape.global_position, body.organ_at(at, 0.02)])
				return
			var home := organ.position
			var aside := body.site.to_global(organ.position + Vector3(0.0, 0.03, 0.0) + Vector3(organ.position.x, 0, organ.position.z).normalized() * 0.12)
			for i in 10:
				grip = patient.update_grip(99001, grip, aside, 1.0, 1.0 / 60.0, 0.0)
				await get_tree().physics_frame
			var now := first_inside(body, uv)
			if now == top:
				print("FAIL: moving the %s aside didn't uncover what's under it" % organ.get_meta("kind"))
			patient.release_grip(99001, grip, false)
			# Put back where it was, out of the way of what's checked next: by now the patient may be past drifting it
			# back (Patient only settles organs while it's alive).
			organ.position = home
			organ.linear_velocity = Vector3.ZERO
			await _frames(30)
			return
	print("FAIL: nothing in the open %s lies under the top layer of organs" % surgery.scenario.site)


## Chest and limbs: a deep cut opens down to the bone. It's right under the muscle, a tool rests on it,
## and the blade grates on it.
func _bone_checks(surgery: Surgery) -> void:
	var patient := surgery.patient
	var body := patient.body
	if body.bones.is_empty():
		if not patient.targets.any(func(t: CavityTarget) -> bool: return t.kind in ["bone", "sternum"]):
			print("FAIL: no bones under the %s" % surgery.scenario.site)
		return
	# The bone closest to the middle of the site.
	var bone: StaticBody3D = null
	var middle := Vector3.INF
	for candidate in body.bones:
		var at := (candidate.get_child(candidate.get_child_count() / 2) as Node3D).position
		if Vector2(at.x, at.z).length() < Vector2(middle.x, middle.z).length():
			bone = candidate
			middle = at
	var uv := Vector2(middle.x / body.site_size.x + 0.5, middle.z / body.site_size.y + 0.5)
	var across := Vector2(0.0, 0.08) if bone.name.begins_with("Rib") else Vector2(0.12, 0.0)
	patient.cut(99100, uv - across, uv + across, 1.0, 1.0, false, 0.1)
	for i in 60:
		body.tissue._substep()
	var first := first_inside(body, uv)
	if first != -2:
		var what: Node3D = body.organs[first] if first >= 0 else null
		var q := PhysicsPointQueryParameters3D.new()
		q.position = body.site.to_global(middle)
		q.collision_mask = PatientBody.CAVITY_LAYER
		print("    point query: ", body.get_world_3d().direct_space_state.intersect_point(q).map(func(h: Dictionary) -> String: return str((h.collider as Node).name)), " shape ", (bone.get_child(1) as CollisionShape3D).global_transform, " site ", body.site.global_transform)
		print("FAIL: the %s under a deep cut at %s isn't the first thing inside: %s at %s, bone at %s" % [bone.name, uv, what.name if what else "nothing", what.position if what else Vector3.ZERO, middle])
	var top := PatientBody.SKIN_THICKNESS + body.fat_thickness + PatientBody.MUSCLE_THICKNESS
	var depth := body.surface_height(uv) - middle.y
	if depth < top or depth > body.cavity_depth():
		print("FAIL: the %s sits %.3f m under the skin, not under the muscle inside the cavity" % [bone.name, depth])
	var scraped: float = patient.flags.get("bone_scraped", 0.0)
	# Down to just over the bone's top, where a blade at full effort stops in an opening.
	var thickness := ((bone.get_child(1) as CollisionShape3D).shape as CapsuleShape3D).radius
	patient.cut_cavity(uv, depth - thickness - 0.002, 1.0, false, 0.5)
	if patient.flags.get("bone_scraped", 0.0) <= scraped:
		print("FAIL: cutting down on the %s didn't reach the bone" % bone.name)


## The heart beats with the pulse, the lungs swell with each breath, and a heart in asystole lies still.
func _organ_motion_checks(surgery: Surgery) -> void:
	var body := surgery.patient.body
	var vitals := surgery.patient.vitals
	for motion in ["beat", "breath"]:
		var index := body.organs.find_custom(func(o: RigidBody3D) -> bool: return o.get_meta("motion", "") == motion)
		if index < 0:
			continue
		var sizes: Array[float] = []
		for i in 90:
			body.animator.animate(vitals, true, 1.0 / 30.0)
			sizes.append(body.organ_motion(index))
		if sizes.max() - sizes.min() < 0.04:
			print("FAIL: the %s doesn't move with the %s (%.3f)" % [body.organs[index].get_meta("kind"), motion, sizes.max() - sizes.min()])
		if motion == "beat":
			var rhythm := vitals.rhythm
			vitals.rhythm = Vitals.Rhythm.ASYSTOLE
			body.animator.animate(vitals, true, 0.3)
			if body.organ_motion(index) != 1.0:
				print("FAIL: the heart still beats in asystole")
			vitals.rhythm = rhythm
