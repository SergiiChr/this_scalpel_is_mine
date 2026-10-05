extends Node
## Renders a few views of a scenario with staged damage, for checking the look without playing.
## Needs a real renderer: xvfb-run godot --path . --rendering-method gl_compatibility res://tests/support/screenshot.tscn -- --scenario=appendectomy --out=/tmp/shots
## --materials instead renders the material board (every material family and skin tone under the surgical lamp) and
## both hands in every grip with the arm stretched out and folded up, for checking the look against the same views.
## --syringe renders every case of tests/support/syringe_bench.gd in the needle view (the last zoom step): the needle in, halfway
## through the wheel notches and done, and the first one held up to read (41_syringe_held_up). Then the IV catheter on
## the vein and beside it: aimed, in, the line from the stand and the dressing close up (42_*), then a sedated and a
## knocked out surgeon (43_* to 46_*, --only=sedation). --only=<case> renders one.
## Without those, --only=monitor stops after the monitor views and --only=site after the site close ups.

const SURGERY := preload("res://scenes/surgery.tscn")
const Bench := preload("res://tests/support/syringe_bench.gd")


func _ready() -> void:
	var scenario_id := "appendectomy"
	var out := "user://screenshots"
	var only := ""
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--scenario="):
			scenario_id = arg.get_slice("=", 1)
		elif arg.begins_with("--out="):
			out = arg.get_slice("=", 1)
		elif arg.begins_with("--only="):
			only = arg.get_slice("=", 1)
	DirAccess.make_dir_recursive_absolute(out)
	if OS.get_cmdline_user_args().has("--menus"):
		await _menus(out)
		return
	if OS.get_cmdline_user_args().has("--syringe"):
		await _syringe(out, only)
		return
	var rng := RandomNumberGenerator.new()
	rng.seed = 3
	Net.leave()
	Net.scenario_id = scenario_id
	Net.session_seed = 42
	Net.roster = {1: {"name": "Tester", "quirks": [{"id": "normal_dude", "variant": ""}], "ready": true}}
	Net.patient_quirks = []
	Net.run_modifiers = []
	var surgery: Surgery = SURGERY.instantiate()
	add_child(surgery)
	await _frames(10)
	if OS.get_cmdline_user_args().has("--anatomy"):
		await _anatomy(surgery, out)
		return
	if OS.get_cmdline_user_args().has("--materials"):
		await _materials(surgery, out)
		return
	var patient := surgery.patient
	patient.cut(1, Vector2(0.25, 0.6), Vector2(0.5, 0.55), 1.0, 1.0, false, 0.1)
	patient.cut(1, Vector2(0.5, 0.55), Vector2(0.72, 0.62), 1.0, 1.0, false, 0.1)
	patient.cut(2, Vector2(0.3, 0.3), Vector2(0.55, 0.25), 0.5, 0.5, false, 0.5)
	# Retract the long incision with two pins, like forceps holding the edges apart.
	var tissue := patient.body.tissue
	for pull: Array in [[Vector2(0.48, 0.52), Vector3(0, 0.004, -0.025)], [Vector2(0.48, 0.6), Vector3(0, 0.004, 0.025)]]:
		var key := 900 + tissue.grips().size()
		tissue.grip(key, pull[0])
		tissue.move_grip(key, tissue.pos[tissue.nearest(pull[0])] + (pull[1] as Vector3))
	var def := Db.tool("cautery")
	patient.cauterize_at("site", Vector2(0.75, 0.3), Db.tool("lighter"), 0.5)
	patient.cauterize_at("site", Vector2(0.8, 0.35), def, 0.5)
	patient.bruise(Vector2(0.2, 0.25), 0.1, 0.8)
	patient.mark(Vector2(0.2, 0.8), Vector2(0.8, 0.82))
	patient.paint(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, Vector2(0.45, 0.62), Vector2(0.45, 0.62), 0.08, 1.0, WoundMap.Mode.MAX)
	patient.swab_at("site", Vector2(0.6, 0.8), Db.tool("cotton_pad"), 1.0, "iodine")
	await _frames(20)
	var me := surgery.local_surgeon
	var camera := Camera3D.new()
	add_child(camera)
	camera.global_transform = get_viewport().get_camera_3d().global_transform
	camera.current = true
	await _shot(out, "01_first_person")
	# The monitor face on, with lab results, then alarming.
	var monitor := surgery.room.monitor
	surgery.room.monitor.show_lab(Lab.result("full", patient))
	camera.global_position = monitor.to_global(Vector3(0.0, 0.0, 0.45))
	camera.look_at(monitor.global_position)
	await _frames(240)
	await _shot(out, "10_monitor")
	var saved := patient.vitals.to_dict()
	for i in 60:
		patient.vitals.spo2 = 84.0
		patient.vitals.systolic = 72.0
		await _frames(1)
	await _shot(out, "10b_monitor_alarm")
	patient.vitals.from_dict(saved)
	if only == "monitor":
		get_tree().quit()
		return
	camera.current = false
	me.camera().current = true
	me.pitch = -1.0
	await _shot(out, "02_looking_down")
	# What a player sees zoomed all the way in on the site.
	me.zoom = Surgeon.ZOOM_FOV.size() - 1
	me.pitch = -0.8
	await _frames(40)
	await _shot(out, "02b_zoomed")
	me.zoom = 0
	await _frames(40)
	camera.current = true
	var site := patient.body.site.global_position
	camera.global_position = site + Vector3(0.0, 0.35, 0.25)
	camera.look_at(site)
	await _shot(out, "03_site_closeup")
	var incision := patient.body.uv_to_world(Vector2(0.45, 0.5))
	camera.global_position = incision + Vector3(0.0, 0.16, 0.1)
	camera.look_at(incision)
	await _shot(out, "03b_tissue_layers")
	if only == "site":
		get_tree().quit()
		return
	camera.global_position = Vector3(2.3, 2.5, 1.9)
	camera.look_at(Vector3(0, 0.9, 0))
	await _shot(out, "04_room")
	# The feet end of the table, with the instrument tray past it.
	camera.global_position = Vector3(-1.0, 1.5, 1.4)
	camera.look_at(Vector3(-1.4, 0.9, 0))
	await _shot(out, "04b_table_foot")
	# Iodine in the dish and one soaked pad.
	for tool: SurgicalTool in surgery.tools.tools.values():
		if tool.def.id in ["iodine_dish", "cotton_pad"]:
			surgery.tools.set_fill(tool, 1.0)
	var tray: Vector3 = surgery.room.layout.tray + Vector3(0, 0.95, 0)
	camera.global_position = tray + Vector3(0.45, 0.45, 0.0)
	camera.look_at(tray)
	await _shot(out, "07_tray")
	# The board over the bell with an order on its way.
	if surgery.room.layout.has("bell"):
		surgery.nurse.request(1, "gauze", surgery)
		await _frames(40)
		var bell: Vector3 = surgery.room.layout.bell
		camera.global_position = bell + Vector3(0.3, 1.5, -1.3)
		camera.look_at(bell + Vector3(0, 1.3, 0))
		await _shot(out, "07b_bell")
	var scalpel: SurgicalTool = null
	for tool: SurgicalTool in surgery.tools.tools.values():
		if tool.def.action == "cut" and tool.state == SurgicalTool.State.FREE:
			scalpel = tool
	var forceps: SurgicalTool = null
	for tool: SurgicalTool in surgery.tools.tools.values():
		if tool.def.action == "clamp" and tool.state == SurgicalTool.State.FREE:
			forceps = tool
	if scalpel:
		surgery.tools._req_grab(scalpel.uid, 1)
	if forceps:
		surgery.tools._req_grab(forceps.uid, 0)
	me.hands[1].local_target = me.to_local(site + Vector3(0.05, 0.1, 0.05))
	me.hands[0].local_target = me.to_local(site + Vector3(-0.08, 0.12, 0.05))
	me.pitch = -0.75
	await _frames(10)
	me.camera().current = true
	await _shot(out, "08_hands")
	# Later in a bloody surgery: gloves soaked, scrubs stained, a spurt just hit the view.
	for hand in me.hands:
		hand.set_blood(0.8)
	me._stains = 0.6
	me._scrubs.set_shader_parameter("stains", 0.6)
	surgery.hud._on_blood_splashed(0.8)
	await _frames(2)
	await _shot(out, "08c_bloody")
	surgery.hud._lens_blood = 0.0
	surgery.hud._post.set_shader_parameter("lens_blood", 0.0)
	# Hands working over the thighs: nothing may sink into the legs or the table.
	var spot := me.global_position
	me.global_position = patient.global_position + Vector3(-0.45, -Room.TABLE_HEIGHT, 0.62)
	var legs := patient.global_position + Vector3(-0.5, 0.0, 0.0)
	me.hands[1].local_target = me.to_local(legs + Vector3(0.05, 0.05, 0.09))
	me.hands[0].local_target = me.to_local(legs + Vector3(-0.08, 0.05, -0.05))
	me.pitch = -0.9
	await _frames(20)
	await _shot(out, "08b_hands_on_legs")
	me.global_position = spot
	camera.current = true
	camera.global_position = me.global_position + Vector3(0.9, 1.7, 0.6)
	camera.look_at(me.global_position + Vector3(0, 1.1, -0.4))
	for child in me.find_children("*", "Node3D", false, false):
		child.visible = true
	for face in me.find_children("Face", "Node3D", true, false):
		face.get_parent().set("visible", true)
	await _shot(out, "09_surgeon")
	var cart := surgery.room.xray
	cart.global_position = patient.global_position + Vector3(-0.3, -Room.TABLE_HEIGHT, 1.1)
	cart._req_expose()
	await _frames(int(XrayCart.EXPOSE_TIME * 60) + 30)
	await get_tree().create_timer(6.5).timeout
	surgery.hud.open_xray(cart)
	await _shot(out, "14_xray_print")
	surgery.hud.close_overlay()
	surgery.hud.open_manual()
	await _shot(out, "05_manual")
	for title: String in ["17. Chronic conditions", "Aneurysm"]:
		for button: Button in surgery.hud.find_children("*", "Button", true, false):
			if button.has_meta("page") and (button.get_meta("page") as ManualPage).title == title:
				button.pressed.emit()
	await _shot(out, "05b_manual_condition")
	surgery.hud.open_card()
	await _shot(out, "06_card")
	get_tree().quit()


## --anatomy: the site opened wide (chest, belly) or cut to the bone (limbs), a top organ held aside,
## a tourniquet on the thigh and a syringe held up to read.
func _anatomy(surgery: Surgery, out: String) -> void:
	var smoke := preload("res://tests/scenarios/test_gameplay_regression.gd")
	var patient := surgery.patient
	var body := patient.body
	var camera := Camera3D.new()
	add_child(camera)
	camera.current = true
	var site := body.site.global_position
	if body.organs.is_empty():
		patient.cut(5, Vector2(0.15, 0.51), Vector2(0.85, 0.51), 1.0, 1.0, false, 0.1)
		for pull: Array in [[Vector2(0.5, 0.46), -1.0], [Vector2(0.5, 0.56), 1.0]]:
			body.tissue.grip(950 + int(pull[1]), pull[0])
		for step in 30:
			for pull: Array in [[Vector2(0.5, 0.46), -1.0], [Vector2(0.5, 0.56), 1.0]]:
				body.tissue.move_grip(950 + int(pull[1]), body.tissue.rest[body.tissue.nearest(pull[0])] + Vector3(0, 0.004, float(pull[1]) * 0.001 * (step + 1)))
			body.tissue._substep()
	else:
		smoke.open_wide(patient)
	await _frames(30)
	camera.global_position = site + body.site.global_basis.y * 0.42 + Vector3(0.0, 0.0, 0.12)
	camera.look_at(site)
	await _shot(out, "20_open_from_above")
	var me := surgery.local_surgeon
	me.camera().current = true
	me.pitch = -1.05
	me.zoom = 1
	await _frames(30)
	await _shot(out, "21_open_first_person")
	var top := body.organs.find_custom(func(o: RigidBody3D) -> bool: return int(o.get_meta("layer", 0)) == 0)
	if top >= 0:
		body.hold_organ(top, body.organs[top].position + Vector3(0, 0.04, 0) + Vector3(body.organs[top].position.x, 0, body.organs[top].position.z).normalized() * 0.1)
		for i in 20:
			body.settle_organs(1.0 / Engine.physics_ticks_per_second)
			await get_tree().physics_frame
		camera.current = true
		await _shot(out, "22_top_organ_aside")
		body.release_organ(top)
	camera.current = true
	var thigh := body.root().to_global(Vector3(-0.75, 0.0, 0.1))
	var ring := body.limb_ring(thigh + Vector3.UP * 0.06)
	if not ring.is_empty():
		surgery.tools.spawn("tourniquet", thigh)
		await _frames(2)
		var tourniquet: SurgicalTool = surgery.tools.tools.values()[-1]
		tourniquet.wrap_around(ring.center, ring.axis, ring.radius)
		camera.global_position = thigh + Vector3(0.1, 0.3, 0.35)
		camera.look_at(thigh)
		await _shot(out, "23_tourniquet")
	for tool: SurgicalTool in surgery.tools.tools.values():
		if tool.def.action == "syringe" and tool.state == SurgicalTool.State.FREE:
			surgery.tools.add_liquid(tool, tool.def.volume * 0.35)
			surgery.tools._req_grab(tool.uid, 1)
			me.active = 1
			me.camera().current = true
			me.pitch = -0.3
			me.zoom = 0
			Input.action_press("inspect")
			await _frames(30)
			await _shot(out, "24_syringe_held_up")
			Input.action_release("inspect")
			break
	get_tree().quit()


## A row of samples on a stand over the site, lit like the site: the four skin tones, glove rubber, scrubs and gown
## cloth, steel tools, wet tissue and blood. Then both hands in each grip at the arm's limits.
func _materials(surgery: Surgery, out: String) -> void:
	var site := surgery.patient.body.site.global_position + Vector3(0, 0.12, 0)
	var board := Node3D.new()
	add_child(board)
	var samples: Array[Material] = []
	for tone in Materials.SKIN_TONES:
		samples.append(Materials.body_skin(tone))
	samples.append_array([Materials.family_unique("cloth", Materials.SCRUBS[0], 0.9), Materials.family_unique("cloth", Materials.PATIENT_GOWN, 0.9), Materials.flesh(), Materials.blood_pool()])
	for i in samples.size():
		var ball := MeshInstance3D.new()
		var sphere := SphereMesh.new()
		sphere.radius = 0.025
		sphere.height = 0.05
		ball.mesh = sphere
		ball.material_override = samples[i]
		board.add_child(ball)
		ball.global_position = site + Vector3(-0.21 + i * 0.06, 0.0, -0.05)
	var glove := ModelSlot.instantiate("surgeon", "glove", board)
	glove.global_position = site + Vector3(-0.2, -0.01, 0.06)
	for i in 3:
		var holder := Node3D.new()
		board.add_child(holder)
		ToolModel.build(Db.tool(["scalpel", "forceps", "needle"][i]), holder)
		holder.global_transform = Transform3D(Basis(Vector3.UP, PI / 2) * Basis(Vector3.RIGHT, -0.2), site + Vector3(0.0 + i * 0.07, 0.0, 0.06))
	await _frames(20)
	var camera := Camera3D.new()
	add_child(camera)
	camera.current = true
	# About where a surgeon's eyes are, then close up.
	camera.global_position = site + Vector3(0.0, 0.35, 0.45)
	camera.look_at(site)
	await _shot(out, "30_materials")
	camera.global_position = site + Vector3(-0.1, 0.12, 0.2)
	camera.look_at(site + Vector3(-0.1, 0.0, 0.0))
	await _shot(out, "31_materials_close")
	board.queue_free()
	var me := surgery.local_surgeon
	me.visible = false
	for grip: String in SurgeonHand.GRIPS:
		var def: ToolDef = Db.tools.values().filter(func(d: ToolDef) -> bool: return d.grip == grip).front()
		var body := Node3D.new()
		add_child(body)
		# Standing on the floor at the table's side, facing the site.
		body.global_position = Vector3(site.x, 0.0, site.z + 0.55)
		var hands: Array[SurgeonHand] = []
		var tools: Array[Node3D] = []
		for index in 2:
			var hand := SurgeonHand.new()
			body.add_child(hand)
			hand.build(index, Materials.family_unique("cloth", Materials.SCRUBS[0], 0.9))
			hand.holding = true
			hand.grip = grip
			hand.fit = Db.grip_fit(def, index)
			hands.append(hand)
			var holder := Node3D.new()
			add_child(holder)
			ToolModel.build(def, holder)
			tools.append(holder)
		for limit: String in ["stretched", "folded"]:
			for hand in hands:
				var side := -1.0 if hand.index == 0 else 1.0
				var shoulder := body.to_global(Vector3(0.19 * side, 1.4, -0.08))
				var reach := Vector3(0.2 * side, -0.3, -0.6) if limit == "stretched" else Vector3(0.06 * side, -0.18, -0.17)
				hand.target = shoulder + reach
				hand.snap_pose(shoulder)
				tools[hand.index].global_transform = hand.grip_transform()
			# From across the table, a little above the hands.
			var between := hands[0].global_position.lerp(hands[1].global_position, 0.5)
			camera.global_position = between + Vector3(0.0, 0.3, -0.6)
			camera.look_at(between)
			await _shot(out, "32_grip_%s_%s" % [grip, limit])
		body.queue_free()
		for tool in tools:
			tool.queue_free()
	get_tree().quit()


func _syringe(out: String, only: String) -> void:
	var bench := Bench.new()
	add_child(bench)
	await bench.start()
	var me := bench.surgery.local_surgeon
	for case: Dictionary in bench.CASES:
		if only and case.name != only:
			continue
		await bench.stage(case)
		me.zoom = Surgeon.ZOOM_FOV.size() - 1
		await bench.frames(40)
		await _shot(out, "40_%s_1_needle_in" % case.name)
		var notches: int = case.notches
		for i in absi(notches):
			if i == absi(notches) / 2:
				await _shot(out, "40_%s_2_halfway" % case.name)
			await bench.notch(notches > 0)
		await bench.frames(10)
		await _shot(out, "40_%s_3_done" % case.name)
		me.zoom = 0
		if case.name == "vial_pull":
			# Held up to read, the printed scale toward the eyes.
			Input.action_press("inspect")
			await bench.frames(30)
			await _shot(out, "41_syringe_held_up")
			Input.action_release("inspect")
	for case: Dictionary in bench.CATHETER_CASES:
		if only and case.name != only:
			continue
		await bench.stage_catheter(case.miss)
		me.zoom = Surgeon.ZOOM_FOV.size() - 1
		await bench.frames(40)
		await _shot(out, "42_%s_1_aimed" % case.name)
		await bench.press()
		# Once it's in, the catheter is used up and the view eases back: look again from where the needle view was.
		var camera := Camera3D.new()
		add_child(camera)
		camera.fov = Surgeon.ZOOM_FOV[-1]
		camera.global_transform = me._needle_view
		camera.current = true
		# Past the needle view easing out (it would set the hands solid again), then faded as in the needle view.
		await bench.frames(30)
		for hand in me.hands:
			hand.set_see_through(Surgeon.NEEDLE_SEE_THROUGH)
		await _shot(out, "42_%s_2_in" % case.name)
		for hand in me.hands:
			hand.set_see_through(0.0)
		# The tubing from the stand's drip chamber down to the arm.
		var stand := bench.surgery.tools.drip_bag().tip_position()
		var arm := bench.vein_point()
		var middle := stand.lerp(arm, 0.5)
		camera.fov = 60.0
		camera.global_transform = Transform3D(Basis.IDENTITY, middle + (arm - stand).cross(Vector3.UP).normalized() * 1.1 + Vector3.UP * 0.4).looking_at(middle)
		await _shot(out, "42_%s_3_line" % case.name)
		# Close up on the catheter taped to the arm: film, hub, tape and the tubing leaving it.
		var dressing: Node3D = bench.surgery.room.iv_line._dressing
		var over := dressing.global_transform * Vector3(-0.03, 0.13, 0.07)
		camera.fov = 35.0
		camera.global_transform = Transform3D(Basis.IDENTITY, over).looking_at(dressing.global_transform * Vector3(-0.03, 0.0, 0.0), dressing.global_basis.x)
		for hand in me.hands:
			hand.visible = false
		await _shot(out, "42_%s_4_dressing" % case.name)
		for hand in me.hands:
			hand.visible = true
		camera.queue_free()
		me.camera().current = true
		me.zoom = 0
	if not only or only == "sedation":
		await _sedation(bench, out)
	get_tree().quit()


## A sedated surgeon at the table: afterimages behind a moving hand and a blurred view (43), too much and the view
## darkens (44), twice the dose and they lie on the floor, looking at the table (45). Then the partner knocked out,
## seen from the room (46).
func _sedation(bench: Bench, out: String) -> void:
	var surgery: Surgery = bench.surgery
	var me := surgery.local_surgeon
	var right := 0.2 * me.status.weight_kg
	bench.place_partner(Bench.PARTNER_PARK, 0.0)
	Input.action_release("crouch")
	me.global_transform = surgery.room.spawn_transform(0)
	me.pitch = -0.8
	for i in 2:
		me.hands[i].local_target = Vector3(-0.17 if i == 0 else 0.17, 1.18, -0.45)
	me.status.drugs.clear()
	me.status.administer("diazepam", right)
	await bench.frames(200)
	# The hand keeps sweeping across while the shot is taken: rendering here is slow, a hand at rest leaves no trail.
	var sweep := func() -> void: me.hands[1].local_target.x = 0.17 + 0.15 * sin(Time.get_ticks_msec() * 0.006)
	get_tree().physics_frame.connect(sweep)
	await bench.frames(30)
	await _shot(out, "43_sedated_trail")
	get_tree().physics_frame.disconnect(sweep)
	me.status.drugs.clear()
	me.status.administer("diazepam", right * 1.8)
	await bench.frames(200)
	await _shot(out, "44_overdose")
	me.status.administer("diazepam", right)
	await bench.frames(240)
	await _shot(out, "45_knocked_out")
	me.status.administer("flumazenil", 0.01 * me.status.weight_kg)
	await bench.frames(200)
	var partner := bench.partner
	bench.place_partner(surgery.patient.global_position * Vector3(1, 0, 1) + Vector3(0.0, me.global_position.y, 1.2), 0.0)
	partner._fall_side = 1.0
	await bench.frames(60)
	for i in 2:
		partner.hands[i].target = partner.to_global((Surgeon.LYING_HAND + Vector3(-0.25 * i, 0, 0)) * Vector3(1, 1, 1))
	var camera := Camera3D.new()
	add_child(camera)
	camera.global_position = partner.to_global(Vector3(-0.8, 1.7, -0.8))
	camera.look_at(partner.to_global(Vector3(-0.8, 0.2, 0.0)))
	camera.current = true
	await bench.frames(10)
	await _shot(out, "46_partner_down")
	camera.queue_free()
	me.camera().current = true


func _menus(out: String) -> void:
	for table: Dictionary in [Db.patient_quirks, Db.surgeon_quirks]:
		for quirk: QuirkDef in table.values():
			if not Progress.is_unlocked(quirk):
				Progress.unlocked_quirks.append(quirk.unlock_key())
	var menu: Control = load("res://scenes/ui/main_menu.tscn").instantiate()
	add_child(menu)
	await _shot(out, "10_menu_scenarios")
	menu.call("_show_codex")
	await _shot(out, "11_menu_codex")
	menu.call("_show_settings")
	await _shot(out, "12_menu_settings")
	menu.queue_free()
	Net.roster = {1: {"name": "Doctor", "quirks": [{"id": "shaky_hands", "variant": ""}, {"id": "hand_size", "variant": ""}, {"id": "divine_knowledge", "variant": ""}], "ready": true}}
	Net.scenario_id = "appendectomy"
	Net.run_modifiers = ["chart_error", "understaffed"]
	var lobby: Control = load("res://scenes/ui/lobby.tscn").instantiate()
	add_child(lobby)
	await _shot(out, "13_lobby")
	get_tree().quit()


func _shot(out: String, file: String) -> void:
	await _frames(6)
	get_viewport().get_texture().get_image().save_png(out.path_join(file + ".png"))


func _frames(count: int) -> void:
	for i in count:
		await get_tree().process_frame
