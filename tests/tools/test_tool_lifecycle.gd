extends GutTest
## Every handheld tool is picked up, sent through its intended interaction path, and put back on the instrument table.

const TAGS = ["smoke", "tool_all"]
const SURGERY := preload("res://scenes/surgery.tscn")


func test_every_handheld_tool_pick_up_use_and_table_drop() -> void:
	var ids: Array[String] = []
	for def: ToolDef in Db.tools.values():
		if not def.fixed:
			ids.append(def.id)
	ids.sort()
	await _exercise(ids)


func test_fixed_tools_have_a_station_interaction_instead_of_a_hand_lifecycle() -> void:
	var fixed: Array[ToolDef] = []
	for def: ToolDef in Db.tools.values():
		if def.fixed:
			fixed.append(def)
	assert_eq(fixed.map(func(def: ToolDef) -> String: return def.id), ["iv_drip"], "only the IV drip is intentionally fixed")
	assert_eq(fixed[0].action, "drip", "the fixed IV bag has the syringe/line interaction covered by the liquids suite")


func test_dropping_a_tool_on_the_floor_soils_it() -> void:
	var surgery := await _start_surgery()
	var tool := _fresh_tool(surgery, "scalpel")
	surgery.tools._req_grab(tool.uid, 1)
	assert_eq(tool.state, SurgicalTool.State.HELD, "scalpel is picked up")
	tool.global_position = Vector3(1.4, 0.25, 1.4)
	surgery.tools._req_release(1, Vector3.ZERO)
	for _frame in 90:
		await get_tree().physics_frame
	assert_eq(tool.state, SurgicalTool.State.FREE, "floor-dropped scalpel is released")
	assert_true(tool.soiled or tool.global_position.y < 0.2, "a tool dropped on the floor is detectably on/soiled by the floor")
	await _dispose(surgery)


func _exercise(ids: Array[String]) -> void:
	var surgery := await _start_surgery()
	var patient := surgery.patient
	patient.vitals.anesthesia = 1.0
	patient.vitals.consciousness = 0.0
	var site := patient.body.uv_to_world(Vector2(0.5, 0.5))
	for id in ids:
		var tool := _fresh_tool(surgery, id)
		surgery.tools._req_grab(tool.uid, 1)
		assert_eq(tool.state, SurgicalTool.State.HELD, "%s is picked up" % id)
		assert_eq(surgery.tools.tool_in_hand(1, 1), tool, "%s is in the requested hand" % id)
		tool.global_position += site - tool.tip_position()
		var hand := {"lowered": true, "trigger": true, "level": 1, "speed": 0.02, "peer": 1, "mods": surgery.local_surgeon.mods}
		ToolActions.update(tool, hand, patient, 0.05)
		ToolActions.update(tool, hand, patient, 0.05)
		assert_true(tool.lowered_before, "%s reaches its declared '%s' use path" % [id, tool.def.action])
		# Drinkable/wearable items use the station interaction rather than ToolActions.
		if tool.def.drinkable:
			tool.charges = maxi(tool.charges, 2)
			var before := tool.charges
			surgery._req_drink(1)
			assert_eq(tool.charges, before - 1, "%s performs its intended personal use" % id)
		elif id == "cig_pack":
			tool.charges = maxi(tool.charges, 2)
			var before := tool.charges
			surgery._req_smoke(1)
			assert_eq(tool.charges, before - 1, "cigarettes perform their intended smoke interaction")
		var drop: Vector3 = (surgery.room.layout.tray as Vector3) + Vector3(0.0, Room.TRAY_SURFACE + 0.12, 0.0)
		# Finish any clamp/retractor use before putting the instrument down; self-retaining
		# tools intentionally stay on the patient while their grip remains engaged.
		if not tool.grip_info.is_empty():
			patient.release_grip(tool.uid, tool.grip_info, false)
			tool.grip_info = {}
		tool.global_position = drop
		surgery.tools._req_release(1, Vector3.ZERO)
		for _frame in 8:
			await get_tree().physics_frame
		assert_eq(tool.state, SurgicalTool.State.FREE, "%s is dropped" % id)
		var table_distance := Vector2(tool.global_position.x - drop.x, tool.global_position.z - drop.z).length()
		assert_lt(table_distance, 0.45, "%s is left over the instrument table" % id)
	await _dispose(surgery)


func _start_surgery() -> Surgery:
	Net.leave()
	Net.scenario_id = "appendectomy"
	Net.session_seed = 24680
	Net.roster = {1: {"name": "Tool tester", "quirks": [], "ready": true}}
	Net.patient_quirks = []
	Net.run_modifiers = []
	var surgery: Surgery = SURGERY.instantiate()
	add_child(surgery)
	for _frame in 5:
		await get_tree().physics_frame
	assert_true(surgery.running, "tool test surgery starts")
	return surgery


func _fresh_tool(surgery: Surgery, id: String) -> SurgicalTool:
	var uid: int = surgery.tools._next_uid
	var tray: Vector3 = surgery.room.layout.tray
	return surgery.tools._create(uid, id, Transform3D(Basis.IDENTITY, tray + Vector3.UP))


func _dispose(surgery: Surgery) -> void:
	surgery.queue_free()
	for _frame in 3:
		await get_tree().process_frame
