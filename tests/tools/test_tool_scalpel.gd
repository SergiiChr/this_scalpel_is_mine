extends GutTest
## Focused scalpel contract: request, pick up, make a measured light cut, and put it back on the table.

const TAGS = ["smoke", "tool_scalpel", "tussue_modification"]
const SURGERY := preload("res://scenes/surgery.tscn")


func test_scalpel_pickup_five_centimeter_cut_and_table_drop() -> void:
	Net.leave()
	Net.scenario_id = "appendectomy"
	Net.session_seed = 5150
	Net.roster = {1: {"name": "Scalpel tester", "quirks": [], "ready": true}}
	Net.patient_quirks = []
	Net.run_modifiers = []
	var surgery: Surgery = SURGERY.instantiate()
	add_child(surgery)
	await _frames(5)
	var tool: SurgicalTool = surgery.tools.tools.values().filter(
		func(candidate: SurgicalTool) -> bool: return candidate.def.id == "scalpel"
	).front()
	surgery.tools._req_grab(tool.uid, 1)
	assert_eq(surgery.tools.tool_in_hand(1, 1), tool, "player_requests_item(scalpel) puts it in hand")

	var patient := surgery.patient
	var uv_length := 0.05 / patient.body.uv_to_meters(1.0)
	var start := Vector2(0.25, 0.5)
	var finish := start + Vector2(uv_length, 0.0)
	patient.cut(tool.uid * 1000, start, finish, 0.25, tool.def.sharpness, not tool.sterile, 0.1)
	var wound: Wound = patient._stroke_wounds[tool.uid * 1000]
	assert_almost_eq(patient.body.uv_to_meters(wound.length_uv()), 0.05, 0.003, "a light scalpel stroke makes a 5 cm cut")
	assert_true(wound.made_by_surgeon and wound.depth < Wound.MUSCLE_DEPTH, "the light cut is attributed to the surgeon and stays superficial")

	var tray: Vector3 = surgery.room.layout.tray
	tool.global_position = tray + Vector3(0.0, Room.TRAY_SURFACE + 0.12, 0.0)
	surgery.tools._req_release(1, Vector3.ZERO)
	await _frames(8)
	assert_eq(tool.state, SurgicalTool.State.FREE, "the scalpel is dropped")
	assert_lt(Vector2(tool.global_position.x - tray.x, tool.global_position.z - tray.z).length(), 0.45, "the scalpel is left on the instrument table")
	surgery.queue_free()
	await _frames(3)


func _frames(count: int) -> void:
	for _frame in count:
		await get_tree().physics_frame
