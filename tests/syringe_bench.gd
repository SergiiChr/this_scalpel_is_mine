extends Node
## Stages the syringe cases for tests/syringe_test.gd (checks) and tests/screenshot.gd --syringe (pictures):
## the local surgeon holds a syringe with its needle in the case's target and works the plunger with wheel notches.
## Over a vial, the dish or the IV drip the needle just rests there; on the patient or a surgeon Use tool presses it
## in and stays held. The wheel works the plunger either way. stage_catheter() puts an IV catheter on the forearm
## vein, or beside it. A partner (a puppet surgeon, peer 2) stands out of the way until a case needs them.

const SURGERY := preload("res://scenes/surgery.tscn")
const VIAL := "vial_cefazolin"
const DRUG := "cefazolin"
## Every case: what the needle is in, the syringe, ml of the drug in it at the start, and the wheel notches to work
## (> 0 pulls the plunger out). A vial starts with what the syringe didn't take from it, the dish with "dish" ml,
## the IV drip on its stand full, with a working line in the arm. The syringe holds the drug of `vial` (VIAL unless
## given). Surgeon targets: the surgeon's own other hand, the partner's hand, the partner's body, and the hand of the
## partner knocked out on the floor (the surgeon crouches beside them).
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
	{"name": "drip_push", "target": "drip", "syringe": "syringe_10", "ml": 6.0, "notches": -6},
	{"name": "drip_pull", "target": "drip", "syringe": "syringe_10", "ml": 0.0, "notches": 6},
	{"name": "doctor_hand_push", "target": "doctor_hand", "syringe": "syringe_10", "ml": 3.0, "notches": -3, "vial": "vial_diazepam"},
	{"name": "doctor_body_push", "target": "doctor_body", "syringe": "syringe_10", "ml": 3.0, "notches": -3, "vial": "vial_diazepam"},
	{"name": "doctor_down_push", "target": "doctor_down", "syringe": "syringe_3", "ml": 2.0, "notches": -2, "vial": "vial_flumazenil"},
	{"name": "own_hand_pull", "target": "own_hand", "syringe": "syringe_10", "ml": 3.0, "notches": 3, "vial": "vial_diazepam"},
	{"name": "own_hand_push", "target": "own_hand", "syringe": "syringe_10", "ml": 3.0, "notches": -3, "vial": "vial_diazepam"},
]
const SURGEON_TARGETS: PackedStringArray = ["own_hand", "doctor_hand", "doctor_body", "doctor_down"]
## Where the partner waits while no case needs them: a corner, hands down.
const PARTNER_PARK := Vector3(2.0, 0.0, -1.7)
## IV catheter cases: on the vein, and 2.5 cm across the forearm from it (on the arm, off the vein).
const CATHETER_CASES: Array[Dictionary] = [{"name": "catheter_vein", "miss": 0.0}, {"name": "catheter_miss", "miss": 0.025}]
## Site uv of a cut through the skin (fat shows) and one through the fat (muscle shows), and of whole skin.
const FAT_UV := Vector2(0.3, 0.3)
const MUSCLE_UV := Vector2(0.5, 0.62)
const SKIN_UV := Vector2(0.75, 0.4)
## How far from the target (meters, across the floor) the surgeon stands to work on it.
const STAND_OFF := 0.45

var surgery: Surgery
var syringe: SurgicalTool
## The case's vial, kidney dish or the IV drip, null for the others.
var container: SurgicalTool
var catheter: SurgicalTool
var partner: Surgeon


## A solo appendectomy with nothing rolled, no random events, the patient asleep, both cuts made and held open, ready
## for stage(). Awake (or woken by an event), a patient in pain thrashes and can knock the syringe out of the hand.
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
	Net.roster[2] = {"name": "Partner", "quirks": [{"id": "normal_dude", "variant": ""}], "ready": true}
	partner = surgery._spawn_surgeon(2, 1)
	place_partner(PARTNER_PARK, 0.0)
	var patient := surgery.patient
	surgery.director._pool = PackedStringArray()
	patient.administer("propofol", "direct", Db.drug("propofol").dose * patient.weight_kg)
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
		if old and not old.def.fixed:
			tools.consume(old)
	container = null
	place_partner(PARTNER_PARK, 0.0)
	Input.action_release("crouch")
	syringe = _spawn(case.syringe, me.global_position + Vector3.UP)
	# Straight into the hand: left to fall, it can reach the floor and shatter first.
	tools._req_grab(syringe.uid, me.active)
	var vial: String = case.get("vial", VIAL)
	match case.target:
		"vial":
			container = _spawn(VIAL, _clear_spot())
			tools.transfer(container, syringe, case.ml)
		"dish":
			container = _spawn("kidney_dish", _clear_spot())
			_fill(container, case.get("dish", 0.0))
			_fill(syringe, case.ml)
		"drip":
			container = tools.drip_bag()
			_fill(syringe, case.ml)
		_:
			_fill(syringe, case.ml, vial)
	await frames(30)
	if case.target in SURGEON_TARGETS:
		await _face_partner(case.target)
	var aim := _aim_point(case.target)
	if not case.target in SURGEON_TARGETS:
		_stand_by(aim)
	if case.target == "drip" and not surgery.patient.iv_working():
		# Only once the surgeon is in place: stepping over to the stand would count as walking through the tubing.
		await frames(5)
		surgery.patient._iv_removed.rpc()
		surgery.patient.set_iv(vein_point(), true)
	await frames(2)
	var hand := me.hands[me.active]
	# The hand rests the needle on whatever is under the aim; a few rounds let the arm settle on it. Only then Use
	# tool presses it in: a needle in the patient sticks, and moved on from there it would tear out.
	for i in 40:
		hand.local_target = me.to_local(aim - hand.tip_offset(syringe.def.length) + Vector3.UP * 0.04)
		await get_tree().physics_frame
	var press := InputEventAction.new()
	press.action = "use_tool"
	press.pressed = not case.target in ["vial", "dish", "air"]
	me._unhandled_input(press)
	await frames(10)


## Puts the partner standing at `at` facing `yaw`, hands hanging at their sides. A puppet goes where it's told.
func place_partner(at: Vector3, yaw: float) -> void:
	partner._fall_side = 0.0
	partner._down = 0.0
	partner.global_position = at
	partner._net_position = at
	partner.rotation.y = yaw
	partner._net_yaw = yaw
	for i in 2:
		partner.hands[i].target = partner.to_global(Vector3(0.3 if i == 1 else -0.3, 0.95, -0.05))


## Surgeon targets: away from the table, the surgeon with their back to it. Their own other hand held out in front,
## or the partner facing them, a hand held out between them; or the partner knocked out on the floor beside the
## table, the surgeon crouched by their hand.
func _face_partner(target: String) -> void:
	var me := surgery.local_surgeon
	var patient := surgery.patient.global_position * Vector3(1, 0, 1)
	var floor_y := me.global_position.y
	if target == "doctor_down":
		place_partner(patient + Vector3(0.0, floor_y, 1.2), 0.0)
		partner._fall_side = 1.0
		await frames(60)
		for i in 2:
			partner.hands[i].target = partner.to_global((Surgeon.LYING_HAND + Vector3(-0.25 * i, 0, 0)) * Vector3(partner._fall_side, 1, 1))
		var glove := partner.hands[0].target
		me.global_position = Vector3(glove.x, floor_y, glove.z - 0.3)
		me.rotation.y = PI
		me.hands[1 - me.active].local_target = Vector3(-0.3, 1.0, -0.1)
		Input.action_press("crouch")
		await frames(30)
		return
	me.global_position = patient + Vector3(0.0, floor_y, 1.1)
	me.rotation.y = PI
	me.hands[1 - me.active].local_target = Vector3(-0.08, 1.05, -0.4) if target == "own_hand" else Vector3(-0.3, 1.0, -0.1)
	if target != "own_hand":
		place_partner(me.to_global(Vector3(0.0, 0.0, -0.75)), 0.0)
	if target == "doctor_hand":
		partner.hands[1].target = partner.to_global(Vector3(0.05, 1.05, -0.35))
	await frames(10)


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


## Puts an IV catheter in the active hand over the forearm vein, `miss` meters across the arm from it, with no line in
## yet. press() then pushes it in.
func stage_catheter(miss: float) -> void:
	var tools := surgery.tools
	var me := surgery.local_surgeon
	for old: SurgicalTool in [syringe, container]:
		if old and not old.def.fixed:
			tools.consume(old)
	syringe = null
	container = null
	surgery.patient._iv_removed.rpc()
	# Use tool let go, or the new catheter would go in wherever the hand passes over the arm.
	var release := InputEventAction.new()
	release.action = "use_tool"
	me._unhandled_input(release)
	catheter = _spawn("iv_catheter", me.global_position + Vector3.UP)
	await frames(30)
	var vein: MeshInstance3D = surgery.patient.body._veins[0]
	var line: PackedVector3Array = vein.get_meta("line")
	var middle := line.size() / 2
	var across := vein.global_basis * (line[middle + 1] - line[middle - 1])
	var aim := vein_point() + across.cross(Vector3.UP).normalized() * miss
	_stand_by(aim)
	tools._req_grab(catheter.uid, me.active)
	await frames(2)
	var hand := me.hands[me.active]
	for i in 40:
		hand.local_target = me.to_local(aim - hand.tip_offset(catheter.def.length) + Vector3.UP * 0.04)
		await get_tree().physics_frame


## Use tool: the held needle goes in where it rests.
func press() -> void:
	var event := InputEventAction.new()
	event.action = "use_tool"
	event.pressed = true
	surgery.local_surgeon._unhandled_input(event)
	await frames(5)


## The middle of the forearm vein the cases use (world space).
func vein_point() -> Vector3:
	var vein: MeshInstance3D = surgery.patient.body._veins[0]
	var line: PackedVector3Array = vein.get_meta("line")
	return vein.to_global(line[line.size() / 2])


func needle_target() -> Dictionary:
	return ToolActions.needle_target(syringe, surgery.patient)


func frames(count: int) -> void:
	for i in count:
		await get_tree().physics_frame


func _spawn(id: String, at: Vector3) -> SurgicalTool:
	surgery.tools.spawn(id, at)
	return surgery.tools.tools.values()[-1]


func _fill(tool: SurgicalTool, ml: float, vial: String = VIAL) -> void:
	if ml > 0.0:
		surgery.tools.add_liquid(tool, ml, {Db.tool(vial).drug: ml * Db.tool(vial).concentration})


## The clear strip down the middle of the instrument tray.
func _clear_spot() -> Vector3:
	var rest := surgery.room.tray_zone("")
	return Vector3(rest.end.x + 0.07, rest.position.y + 0.05, rest.get_center().z)


func _aim_point(target: String) -> Vector3:
	var body := surgery.patient.body
	match target:
		"vial", "dish", "drip":
			return ToolManager.middle(container)
		"vein":
			return vein_point()
		"fat":
			return body.uv_to_world(FAT_UV)
		"muscle":
			return body.uv_to_world(MUSCLE_UV)
		"skin":
			return body.uv_to_world(SKIN_UV)
		"own_hand":
			var me := surgery.local_surgeon
			return me.hands[1 - me.active].global_position
		"doctor_hand":
			return partner.hands[1].global_position
		"doctor_body":
			return partner.to_global(Vector3(0.0, 1.0, -0.1))
		"doctor_down":
			return partner.hands[0].global_position
	# Out over the floor beside the table, nothing under it within reach.
	var me := surgery.local_surgeon
	return me.to_global(Vector3(0.2, 1.0, -0.15))


## Walks the surgeon to stand facing the aim from outside the table, close enough to reach it.
func _stand_by(aim: Vector3) -> void:
	var me := surgery.local_surgeon
	# Stepping over counts as walking through the tubing, which would rip a line out and jolt the hand: take it out.
	surgery.patient._iv_removed.rpc()
	var patient := surgery.patient.global_position
	var away := (aim - patient) * Vector3(1, 0, 1)
	if absf(away.z) < 0.15:
		away = Vector3(0, 0, 1)
	# Off the table's long side, on the aim's side of it.
	away = Vector3(0, 0, signf(away.z))
	var spot := Vector3(aim.x, 0.0, patient.z) + away * (absf(aim.z - patient.z) + STAND_OFF) * Vector3(0, 0, 1)
	if aim.distance_to(patient) > 1.2:
		# The tray or the IV stand: from the side that faces the room's middle.
		spot = aim * Vector3(1, 0, 1) + (Vector3.ZERO - aim).slide(Vector3.UP).normalized() * STAND_OFF
	spot.y = me.global_position.y
	me.global_position = spot
	var facing := (aim - spot) * Vector3(1, 0, 1)
	me.rotation.y = atan2(-facing.x, -facing.z)
