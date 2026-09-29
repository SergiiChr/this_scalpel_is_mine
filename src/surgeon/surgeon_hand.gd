class_name SurgeonHand
extends Node3D
## One hand and its arm. The arm is solved with analytic two-bone IK from shoulder to hand every frame.
## Position is owned by the surgeon's peer and synced to everyone; the host uses it to drive tools.

const UPPER_ARM := 0.34
const FOREARM := 0.34
const TILT_RANGE := Vector2(-1.5, -0.2)
const LIFT_HEIGHT := 0.12
const SPEED_WINDOW_MSEC := 250
const FINGERS: PackedStringArray = ["Index", "Middle", "Ring", "Pinky", "Thumb"]
## Radians each finger joint bends at full curl, knuckle first.
const JOINT_BEND: PackedFloat32Array = [0.9, 1.2, 0.8]

## How a hand holds each kind of tool (ToolDef.grip), in the tool's frame (grip at the origin, tip toward -Z).
## basis: the glove's axes (fingers, back of the hand, pinky side) in tool space, for the right hand.
## at: the glove point (glove model space) that sits on `on`, a point of the tool.
## curl: how far each finger closes (index, middle, ring, pinky, thumb) while holding.
const GRIPS: Dictionary = {
	# Between thumb and index, fingers running toward the tip and a little flatter than the tool, wrist behind it.
	"pencil": {
		"basis": Basis(Vector3(0.0, 0.42, -0.91), Vector3(0.0, 0.91, 0.42), Vector3(1.0, 0.0, 0.0)),
		"at": Vector3(0.095, -0.03, -0.012), "on": Vector3(0.0, 0.0, 0.0),
		"curl": [0.45, 0.6, 0.75, 0.85, 0.45],
	},
	# Thumb and ring finger through the rings at the back, index laid along the shaft.
	"rings": {
		"basis": Basis(Vector3(0.0, 0.2, -0.98), Vector3(0.0, 0.98, 0.2), Vector3(1.0, 0.0, 0.0)),
		"at": Vector3(0.1, -0.024, 0.0), "on": Vector3(0.0, 0.0, 0.055),
		"curl": [0.2, 0.7, 0.8, 0.85, 0.6],
	},
	# Wrapped around a handle that runs across the palm, thumb toward the tip, knuckles on top.
	"fist": {
		"basis": Basis(Vector3(0.0, -1.0, 0.0), Vector3(1.0, 0.0, 0.0), Vector3(0.0, 0.0, 1.0)),
		"at": Vector3(0.06, -0.026, 0.0), "on": Vector3(0.0, 0.0, 0.0),
		"curl": [1.0, 1.0, 1.0, 1.0, 0.8],
	},
	# Pinched at the back edge between thumb and fingertips, palm down over it.
	"flat": {
		"basis": Basis(Vector3(0.0, 0.25, -0.97), Vector3(0.0, 0.97, 0.25), Vector3(1.0, 0.0, 0.0)),
		"at": Vector3(0.1, -0.03, 0.0), "on": Vector3(0.0, 0.004, 0.0),
		"curl": [0.55, 0.55, 0.6, 0.65, 0.5],
	},
}

var index := 0
## Where the hand wants to be, world space, before tremor and lift.
var target := Vector3.ZERO
## Target relative to the surgeon, used while not attached so the hand moves with the body.
var local_target := Vector3.ZERO
var tilt := -1.1
var twist := 0.0
## Use tool held: the tool rests on its spot instead of hovering over it.
var lowered := false
## Use tool held, for tools with a single action (ToolActions.TRIGGER_NAMES): on press, while held, on release.
var trigger := false
## Effort level 0..3 from the wheel (cut depth, stitch tension, heat, plunger...), see ToolActions.LEVEL_NAMES.
var level := 0
var lifted := false
var attached := false
## Held up in front of the eyes, turned across the view with its markings toward them (reading a syringe).
var inspecting := false
var tremor := Vector3.ZERO
var speed := 0.0
## Remote copies receive the final position already including lift and tremor.
var puppet := false
## How bloody the glove is (0..1). Every peer soaks it from the held tool's synced blood, so it matches everywhere.
var blood := 0.0

var _lift := 0.0
## Recent [msec, position] samples. Speed over a short window ignores tremor and network jitter.
var _history: Array = []
## Set by the surgeon each frame; drives how far the fingers curl.
var holding := false
## ToolDef.grip of the tool in this hand, set with `holding`. Empty hands follow the forearm, open.
var grip := "pencil"
var _curl := 0.2
## [curl, grip, holding] the fingers were last posed for.
var _posed: Array = []
var _glove: Node3D
var _glove_rig: BoneRig
var _glove_materials: Array[ShaderMaterial] = []
## The toon shader's coat runs back from a tip at -Z, the glove's fingers point along +X.
const COAT_FRAME := Transform3D(Basis(Vector3.FORWARD, Vector3.UP, Vector3.RIGHT), Vector3.ZERO)
## Wrist to fingertips along the glove's X.
const GLOVE_LENGTH := 0.19
## About how thick a finger is around its bones, and the palm above and below its bone.
const FINGER_RADIUS := 0.009
const PALM_HALF_THICKNESS := 0.014
## Glove blood gained per second while the held tool is bloodier than the glove.
const SOAK_RATE := 0.15
## Inside the glove's cuff, behind the wrist (glove model space): where a holding hand's forearm ends.
const CUFF_POINT := Vector3(-0.06, 0.0, 0.0)
## Most a held tool's angle in the fingers gives way to keep the wrist straight (radians).
const MAX_TOOL_TIP := 0.6
## Empty hand: the glove point (glove model space) at the hand's position, the hollow of the fingers.
const GRIP_POINT := Vector3(0.07, -0.028, 0.0)
var _upper: Node3D
var _fore: Node3D
var _pusher: AnimatableBody3D
## Fraction of the forearm hidden at the elbow end (local player only, keeps the view clear).
var _forearm_start := 0.0


func build(hand_index: int, scrubs: ShaderMaterial) -> void:
	index = hand_index
	name = "LeftHand" if index == 0 else "RightHand"
	var sleeve := {"tint": scrubs}
	_glove = ModelSlot.instantiate("surgeon", "glove", self)
	_glove_materials = ModelSlot.own_materials(_glove)
	for mat in _glove_materials:
		mat.set_shader_parameter("coat_length", GLOVE_LENGTH)
	# The glove is modeled wrist at the origin, fingers along +X, palm facing -Y, thumb toward -Z.
	# Empty, it follows the forearm (see _place_glove()). Holding a tool, it sits on the tool by its grip (GRIPS).
	_glove.top_level = true
	_glove_rig = BoneRig.find(_glove)
	_upper = ModelSlot.instantiate("surgeon", "upper_arm", self, sleeve)
	_fore = ModelSlot.instantiate("surgeon", "forearm", self, sleeve)
	for segment in [_upper, _fore]:
		segment.top_level = true
	_pusher = AnimatableBody3D.new()
	_pusher.collision_layer = PatientBody.PUSHER_LAYER
	_pusher.collision_mask = 0
	var shape := CollisionShape3D.new()
	var sphere := SphereShape3D.new()
	sphere.radius = 0.025
	shape.shape = sphere
	_pusher.add_child(shape)
	_pusher.top_level = true
	add_child(_pusher)


## The local player sees forearms and gloves only; the upper arm would sit right under the camera.
func hide_upper_arm() -> void:
	_upper.visible = false
	_forearm_start = 0.2
	var cuff := _fore.find_child("Cuff", true, false) as Node3D
	if cuff:
		cuff.visible = false


## Final world position: target plus lift and tremor.
func effective_position() -> Vector3:
	return target if puppet else target + Vector3(0, _lift, 0) + tremor


func grip_transform() -> Transform3D:
	if inspecting:
		return Transform3D(inspect_basis(), global_position)
	var yaw := (get_parent() as Node3D).global_rotation.y
	var rot := Basis(Vector3.UP, yaw) * Basis(Vector3.RIGHT, tilt) * Basis(Vector3.FORWARD, twist)
	return Transform3D(rot, global_position)


## A tool held up to look at: its tip across the view toward the other hand's side, and its top (+Y, where the back
## of the hand is in every grip) turned away from the eyes, so the hand is behind the tool and doesn't hide it.
func inspect_basis() -> Basis:
	var eyes := (get_parent() as Surgeon).camera().global_basis.orthonormalized()
	var along := eyes.x * (1.0 if index == 1 else -1.0)
	var top := -eyes.z
	return Basis(top.cross(along), top, along)


func tip_offset(tool_length: float) -> Vector3:
	return grip_transform().basis * Vector3(0, 0, -tool_length)


func update_pose(shoulder: Vector3, delta: float) -> void:
	_lift = move_toward(_lift, LIFT_HEIGHT if lifted else 0.0, delta * 0.8)
	global_position = effective_position()
	_track_speed()
	global_basis = grip_transform().basis
	_pusher.global_position = global_position
	_solve_arm(shoulder)
	_animate_fingers(delta)
	glove_drop = _glove_lowest() - global_position.y


## How far below the hand's position the glove reaches as it's posed now (negative: below). The surgeon keeps that
## clear of tables and trays, not only the hand's middle.
var glove_drop := -0.03


func _glove_lowest() -> float:
	var lowest := global_position.y - 0.03
	for point: Array in bone_points():
		lowest = minf(lowest, (point[0] as Vector3).y - float(point[1]))
	return lowest


## Points along the glove's bones as it's posed now (world space), each with how thick the glove is around it:
## [[position, radius], ...]. Inside the fingers and the palm, so nothing solid should be at them.
func bone_points() -> Array:
	var points: Array = []
	if _glove_rig == null:
		return points
	var skeleton := _glove_rig.skeleton
	for i in skeleton.get_bone_count():
		var at := skeleton.global_transform * skeleton.get_bone_global_pose(i).origin
		var children := skeleton.get_bone_children(i)
		var to := skeleton.global_transform * skeleton.get_bone_global_pose(children[0]).origin if not children.is_empty() else at
		var radius := PALM_HALF_THICKNESS if skeleton.get_bone_name(i) == "Hand" else FINGER_RADIUS
		for t: float in [0.0, 0.5, 1.0]:
			points.append([at.lerp(to, t), radius])
	return points
	if blood > 0.0:
		for mat in _glove_materials:
			mat.set_shader_parameter("coat_inverse", Projection(COAT_FRAME * _glove.global_transform.affine_inverse()))


## Blood works its way from a bloody tool onto the fingers, then the palm. It never drips off on its own.
func soak(tool_blood: float, delta: float) -> void:
	if tool_blood * 0.8 > blood:
		set_blood(minf(blood + SOAK_RATE * delta, tool_blood * 0.8))


func set_blood(amount: float) -> void:
	blood = amount
	for mat in _glove_materials:
		mat.set_shader_parameter("coat", amount)
		mat.set_shader_parameter("coat_reach", GLOVE_LENGTH * (0.3 + amount))


func _track_speed() -> void:
	var now := Time.get_ticks_msec()
	_history.append([now, global_position])
	while _history.size() > 2 and now - int(_history[0][0]) > SPEED_WINDOW_MSEC:
		_history.pop_front()
	var oldest: Array = _history[0]
	var span := maxf((now - int(oldest[0])) * 0.001, 0.016)
	speed = global_position.distance_to(oldest[1]) / span


## Relaxed when empty, closed around a held tool as its grip says, squeezed a little tighter while using it.
func _animate_fingers(delta: float) -> void:
	var target := (1.1 if lowered or trigger else 1.0) if holding else 0.15
	_curl = move_toward(_curl, target, delta * 4.0)
	# Posing 15 bones only matters while the curl changes, which is a fraction of the time.
	var pose: Array = [_curl, grip, holding]
	if _glove_rig == null or pose == _posed:
		return
	_posed = pose
	var amounts: Array = GRIPS.get(grip, GRIPS.pencil).curl if holding else [1.0, 1.0, 1.0, 1.0, 1.0]
	for f in FINGERS.size():
		var finger := FINGERS[f]
		for joint in 3:
			var bone := "%s%d" % [finger, joint + 1]
			# Each joint bends its bone toward the palm (the glove's -Y); the thumb folds in less.
			var bend := minf(_curl * float(amounts[f]), 1.0) * JOINT_BEND[joint] * (0.55 if finger == "Thumb" else 1.0)
			var axis := _glove_rig.direction(bone).cross(Vector3.DOWN).normalized()
			_glove_rig.rotate(bone, Basis(axis, bend))


func _solve_arm(shoulder: Vector3) -> void:
	var to_hand := global_position - shoulder
	var dist := clampf(to_hand.length(), 0.05, UPPER_ARM + FOREARM - 0.001)
	var dir := to_hand.normalized()
	var along := (UPPER_ARM * UPPER_ARM - FOREARM * FOREARM + dist * dist) / (2.0 * dist)
	var height := sqrt(maxf(UPPER_ARM * UPPER_ARM - along * along, 0.0))
	var side := -1.0 if index == 0 else 1.0
	var owner_basis := (get_parent() as Node3D).global_basis
	var pole := (owner_basis.x * side * 0.6 + Vector3.DOWN).normalized()
	pole = (pole - dir * pole.dot(dir)).normalized()
	var elbow := shoulder + dir * along + pole * height
	_place_segment(_upper, shoulder, elbow)
	var wrist := _place_glove(elbow, owner_basis)
	_place_segment(_fore, elbow.lerp(wrist, _forearm_start), wrist)


## Places the glove and returns the wrist, where the forearm ends. The left glove is the right one mirrored.
## Holding a tool, the glove sits on it by its grip. Empty, it points along the forearm, palm down.
func _place_glove(elbow: Vector3, owner_basis: Basis) -> Vector3:
	if holding:
		var style: Dictionary = GRIPS.get(grip, GRIPS.pencil)
		var tool_frame := grip_transform()
		var mirror := Vector3(-1, 1, 1) if index == 0 else Vector3.ONE
		var contact := tool_frame * ((style.on as Vector3) * mirror)
		var frame := tool_frame.basis * Basis.from_scale(mirror) * (style.basis as Basis)
		frame = _turn_to_forearm(frame, tool_frame.basis * Vector3.FORWARD, contact, elbow, grip != "fist")
		var wrist := contact - frame * (style.at as Vector3)
		_glove.global_transform = Transform3D(frame, wrist)
		# The forearm runs into the glove's loose cuff, so the cuff never shows as an open tube end.
		return _glove.global_transform * CUFF_POINT
	var along := (global_position - elbow).normalized()
	var up := (Vector3.UP - along * Vector3.UP.dot(along)).normalized()
	if up.length_squared() < 0.5:
		up = owner_basis.z
	var side := along.cross(up)
	var frame := Basis(along, up, side if index == 1 else -side)
	var wrist := global_position - frame * GRIP_POINT
	_glove.global_transform = Transform3D(frame, wrist)
	return wrist


## Turns a held glove about the tool so the wrist faces the elbow, without letting go:
## first it rolls around the tool's own axis (the hand wraps the same way, just from another side),
## then, for grips that allow it, it tips the tool up to MAX_TOOL_TIP against the fingers, like changing a pen angle.
func _turn_to_forearm(frame: Basis, axis: Vector3, contact: Vector3, elbow: Vector3, can_tip: bool) -> Basis:
	var to_elbow := (elbow - contact).normalized()
	var wrist_dir := -frame.x.normalized()
	var roll := _signed_angle(wrist_dir - axis * wrist_dir.dot(axis), to_elbow - axis * to_elbow.dot(axis), axis)
	frame = Basis(axis, roll) * frame
	if can_tip:
		wrist_dir = -frame.x.normalized()
		var pivot := wrist_dir.cross(to_elbow)
		if pivot.length() > 0.001:
			frame = Basis(pivot.normalized(), minf(wrist_dir.angle_to(to_elbow), MAX_TOOL_TIP)) * frame
	return frame


static func _signed_angle(from: Vector3, to: Vector3, axis: Vector3) -> float:
	if from.length() < 0.0001 or to.length() < 0.0001:
		return 0.0
	return from.signed_angle_to(to, axis)


static func _place_segment(mesh: Node3D, a: Vector3, b: Vector3) -> void:
	var y := (b - a)
	var length := y.length()
	if length < 0.001:
		return
	y /= length
	var x := y.cross(Vector3.FORWARD if absf(y.dot(Vector3.FORWARD)) < 0.9 else Vector3.RIGHT).normalized()
	var z := x.cross(y)
	mesh.global_transform = Transform3D(Basis(x, y * length, z), (a + b) * 0.5)
