extends Node
## Renders a few views of a scenario with staged damage, for checking the look without playing.
## Needs a real renderer: xvfb-run godot --path . --rendering-method gl_compatibility res://tests/screenshot.tscn -- --scenario=appendectomy --out=/tmp/shots

const SURGERY := preload("res://scenes/surgery.tscn")


func _ready() -> void:
	var scenario_id := "appendectomy"
	var out := "user://screenshots"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--scenario="):
			scenario_id = arg.get_slice("=", 1)
		elif arg.begins_with("--out="):
			out = arg.get_slice("=", 1)
	DirAccess.make_dir_recursive_absolute(out)
	if OS.get_cmdline_user_args().has("--menus"):
		await _menus(out)
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
	patient.cauterize_at("site", Vector2(0.75, 0.3), 0.0, Db.tool("lighter"), 0.5)
	patient.cauterize_at("site", Vector2(0.8, 0.35), 0.0, def, 0.5)
	patient.bruise(Vector2(0.2, 0.25), 0.1, 0.8)
	patient.mark(Vector2(0.2, 0.8), Vector2(0.8, 0.82))
	patient.paint(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, Vector2(0.45, 0.62), Vector2(0.45, 0.62), 0.08, 1.0, WoundMap.Mode.MAX)
	patient.swab_at("site", Vector2(0.6, 0.8), Db.tool("iodine_swab"), 1.0)
	await _frames(20)
	var me := surgery.local_surgeon
	var camera := Camera3D.new()
	add_child(camera)
	camera.global_transform = get_viewport().get_camera_3d().global_transform
	camera.current = true
	await _shot(out, "01_first_person")
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
	camera.global_position = Vector3(2.3, 2.5, 1.9)
	camera.look_at(Vector3(0, 0.9, 0))
	await _shot(out, "04_room")
	var tray: Vector3 = surgery.room.layout.tray + Vector3(0, 0.95, 0)
	camera.global_position = tray + Vector3(0.45, 0.45, 0.0)
	camera.look_at(tray)
	await _shot(out, "07_tray")
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
	surgery.hud.open_card()
	await _shot(out, "06_card")
	get_tree().quit()


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
	Net.roster = {1: {"name": "Doctor", "quirks": [{"id": "shaky_hands", "variant": ""}, {"id": "hand_size", "variant": "big"}, {"id": "divine_knowledge", "variant": ""}], "ready": true}}
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
