extends Node
## Stages the syringe cases for tests/syringe_test.gd (checks) and tests/screenshot.gd --syringe (pictures):
## the local surgeon holds a syringe with its needle in the case's target and works the plunger with wheel notches.
## Over a vial or the dish the needle just rests there; on the patient Use tool presses it in and stays held.
## The wheel works the plunger either way.

const SURGERY := preload("res://scenes/surgery.tscn")
const VIAL := "vial_cefazolin"
const DRUG := "cefazolin"
## Every case: what the needle is in, the syringe, ml of the drug in it at the start, and the wheel notches to work
## (> 0 pulls the plunger out). A vial starts with what the syringe didn't take from it, the dish with "dish" ml.
const CASES: Array[Dictionary] = [
	{"name": "vial_pull", "target": "vial", "syringe": "syringe_10", "ml": 0.0, "notches": 6},
	{"name": "vial_push", "target": "vial", "syringe": "syringe_10", "ml": 6.0, "notches": -6},
	{"name": "dish_push", "target": "dish", "syringe": "syringe_50", "ml": 40.0, "notches": -40},
	{"name": "dish_pull", "target": "dish", "syringe": "syringe_50", "ml": 0.0, "notches": 40, "dish": 60.0},
	{"name": "vein_push", "target": "vein", "syringe": "syringe_10", "ml": 6.0, "notches": -6},
	{"name": "vein_pull", "target": "vein", "syringe": "syringe_10", "ml": 3.0, "notches": 6},
	{"name": "skin_push", "target": "skin", "syringe": "syringe_10", "ml": 6.0, "notches": -6},
	{"name": "fat_push", "target": "fat", "syringe": "syringe_10", "ml": 6.0, "notches": -6},
	{"name": "muscle_push", "target": "muscle", "syringe": "syringe_10", "ml": 6.0, "notches": -6},
	{"name": "skin_pull", "target": "skin", "syringe": "syringe_10", "ml": 3.0, "notches": 6},
	{"name": "fat_pull", "target": "fat", "syringe": "syringe_10", "ml": 3.0, "notches": 6},
	{"name": "muscle_pull", "target": "muscle", "syringe": "syringe_10", "ml": 3.0, "notches": 6},
	{"name": "air_pull", "target": "air", "syringe": "syringe_10", "ml": 3.0, "notches": 6},
]
## Site uv of a cut through the skin (fat shows) and one through the fat (muscle shows), and of whole skin.
const FAT_UV := Vector2(0.3, 0.3)
const MUSCLE_UV := Vector2(0.5, 0.62)
const SKIN_UV := Vector2(0.75, 0.4)
## How far from the target (meters, across the floor) the surgeon stands to work on it.
const STAND_OFF := 0.45

var surgery: Surgery
var syringe: SurgicalTool
## The case's vial or kidney dish, null for the others.
var container: SurgicalTool


## A solo appendectomy with nothing rolled, both cuts made and held open, ready for stage().
func start() -> void:
	Net.leave()
	Net.scenario_id = "appendectomy"
	Net.session_seed = 42
	Net.roster = {1: {"name": "Tester", "quirks": [{"id": "normal_dude", "variant": ""}], "ready": true}}
	Net.patient_quirks = []
	Net.run_modifiers = []
	surgery = SURGERY.instantiate()
	add_child(surgery)
	await frames(10)
	var patient := surgery.patient
	var tissue := patient.body.tissue
	for cut: Array in [[FAT_UV, 0.3], [MUSCLE_UV, 0.6]]:
		var mid: Vector2 = cut[0]
		patient.cut(10 + tissue.grips().size(), mid - Vector2(0.1, 0.0), mid + Vector2(0.1, 0.0), cut[1], 1.0, false, 0.1)
		# Held open with two pins, like forceps on either edge.
		for pull: float in [-0.025, 0.025]:
			var key := 900 + tissue.grips().size()
			var edge := mid + Vector2(0.0, signf(pull) * 0.03)
			tissue.grip(key, edge)
			tissue.move_grip(key, tissue.pos[tissue.nearest(edge)] + Vector3(0, 0.004, pull))
	await frames(60)


## Puts a fresh syringe holding the case's start in the active hand, its needle in the case's target.
func stage(case: Dictionary) -> void:
	var tools := surgery.tools
	var me := surgery.local_surgeon
	for old: SurgicalTool in [syringe, container]:
		if old:
			tools.consume(old)
	container = null
	syringe = _spawn(case.syringe, me.global_position + Vector3.UP)
	match case.target:
		"vial":
			container = _spawn(VIAL, _clear_spot())
			tools.transfer(container, syringe, case.ml)
		"dish":
			container = _spawn("kidney_dish", _clear_spot())
			_fill(container, case.get("dish", 0.0))
			_fill(syringe, case.ml)
		_:
			_fill(syringe, case.ml)
	await frames(30)
	var aim := _aim_point(case.target)
	_stand_by(aim)
	tools._req_grab(syringe.uid, me.active)
	await frames(2)
	var hand := me.hands[me.active]
	var press := InputEventAction.new()
	press.action = "use_tool"
	press.pressed = not case.target in ["vial", "dish", "air"]
	me._unhandled_input(press)
	# The hand rests the needle on whatever is under the aim; a few rounds let the arm settle on it.
	for i in 40:
		hand.local_target = me.to_local(aim - hand.tip_offset(syringe.def.length) + Vector3.UP * 0.04)
		await get_tree().physics_frame


## One wheel notch, as the mouse sends it: down pulls the plunger out, up pushes it in.
func notch(pull: bool) -> void:
	var wheel := InputEventMouseButton.new()
	wheel.button_index = MOUSE_BUTTON_WHEEL_DOWN if pull else MOUSE_BUTTON_WHEEL_UP
	wheel.pressed = true
	surgery.local_surgeon._unhandled_input(wheel)
	await frames(2)


## Takes the needle out: the hand goes up and away over the floor.
func withdraw() -> void:
	var me := surgery.local_surgeon
	var release := InputEventAction.new()
	release.action = "use_tool"
	me._unhandled_input(release)
	me.hands[me.active].local_target = Vector3(0.15, 1.1, -0.2)
	await frames(20)


func needle_target() -> Dictionary:
	return ToolActions.needle_target(syringe, surgery.patient)


func frames(count: int) -> void:
	for i in count:
		await get_tree().physics_frame


func _spawn(id: String, at: Vector3) -> SurgicalTool:
	surgery.tools.spawn(id, at)
	return surgery.tools.tools.values()[-1]


func _fill(tool: SurgicalTool, ml: float) -> void:
	if ml > 0.0:
		surgery.tools.add_liquid(tool, ml, {DRUG: ml * Db.tool(VIAL).concentration})


## The far end of the instrument tray, clear of the tools laid out on it.
func _clear_spot() -> Vector3:
	var room := surgery.room
	return Vector3(room.tray_area().get_center().x, room.tray_top() + 0.05, room.tray_area().end.y - 0.12)


func _aim_point(target: String) -> Vector3:
	var body := surgery.patient.body
	match target:
		"vial", "dish":
			return ToolManager.middle(container)
		"vein":
			var vein: MeshInstance3D = body._veins[0]
			var line: PackedVector3Array = vein.get_meta("line")
			return vein.to_global(line[line.size() / 2])
		"fat":
			return body.uv_to_world(FAT_UV)
		"muscle":
			return body.uv_to_world(MUSCLE_UV)
		"skin":
			return body.uv_to_world(SKIN_UV)
	# Out over the floor beside the table, nothing under it within reach.
	var me := surgery.local_surgeon
	return me.to_global(Vector3(0.2, 1.0, -0.15))


## Walks the surgeon to stand facing the aim from outside the table, close enough to reach it.
func _stand_by(aim: Vector3) -> void:
	var me := surgery.local_surgeon
	var patient := surgery.patient.global_position
	var away := (aim - patient) * Vector3(1, 0, 1)
	if absf(away.z) < 0.15:
		away = Vector3(0, 0, 1)
	# Off the table's long side, on the aim's side of it.
	away = Vector3(0, 0, signf(away.z))
	var spot := Vector3(aim.x, 0.0, patient.z) + away * (absf(aim.z - patient.z) + STAND_OFF) * Vector3(0, 0, 1)
	if aim.distance_to(patient) > 1.2:
		# The tray: from the side that faces the room's middle.
		spot = aim * Vector3(1, 0, 1) + (Vector3.ZERO - aim).slide(Vector3.UP).normalized() * STAND_OFF
	spot.y = me.global_position.y
	me.global_position = spot
	var facing := (aim - spot) * Vector3(1, 0, 1)
	me.rotation.y = atan2(-facing.x, -facing.z)
