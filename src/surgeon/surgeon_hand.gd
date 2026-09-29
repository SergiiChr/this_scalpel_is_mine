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

var index := 0
## Where the hand wants to be, world space, before tremor and lift.
var target := Vector3.ZERO
## Target relative to the surgeon, used while not attached so the hand moves with the body.
var local_target := Vector3.ZERO
var tilt := -1.1
var twist := 0.0
var engaged := false
var pressure := 2
var lifted := false
var attached := false
var tremor := Vector3.ZERO
var speed := 0.0
## Remote copies receive the final position already including lift and tremor.
var puppet := false

var _lift := 0.0
## Recent [msec, position] samples. Speed over a short window ignores tremor and network jitter.
var _history: Array = []
## Set by the surgeon each frame; drives how far the fingers curl.
var holding := false
var _curl := 0.2
var _posed_curl := -1.0
var _glove: Node3D
var _glove_rig: BoneRig
## The glove point (in glove model space) that holds the tool: the hollow of the curled fingers.
const GRIP_POINT := Vector3(0.07, -0.028, 0.0)
var _upper: Node3D
var _fore: Node3D
var _pusher: AnimatableBody3D
## Fraction of the forearm hidden at the elbow end (local player only, keeps the view clear).
var _forearm_start := 0.0


func build(hand_index: int, scrubs: Color) -> void:
	index = hand_index
	name = "LeftHand" if index == 0 else "RightHand"
	var sleeve := {"tint": Materials.toon(scrubs, 0.35)}
	_glove = ModelSlot.instantiate("surgeon", "glove", self)
	# The glove is modeled wrist at the origin, fingers along +X, palm facing -Y, thumb toward -Z.
	# It follows the forearm (see _place_glove()), so the wrist never bends backwards whatever the tool's tilt;
	# the tool passes through the curled fingers at its own angle, like a pen.
	_glove.top_level = true
	_glove_rig = BoneRig.find(_glove)
	_upper = ModelSlot.instantiate("surgeon", "upper_arm", self, sleeve)
	_fore = ModelSlot.instantiate("surgeon", "forearm", self, sleeve)
	for segment in [_upper, _fore]:
		segment.top_level = true
	_pusher = AnimatableBody3D.new()
	_pusher.collision_layer = PatientBody.CAVITY_LAYER
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
	var yaw := (get_parent() as Node3D).global_rotation.y
	var rot := Basis(Vector3.UP, yaw) * Basis(Vector3.RIGHT, tilt) * Basis(Vector3.FORWARD, twist)
	return Transform3D(rot, global_position)


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


func _track_speed() -> void:
	var now := Time.get_ticks_msec()
	_history.append([now, global_position])
	while _history.size() > 2 and now - int(_history[0][0]) > SPEED_WINDOW_MSEC:
		_history.pop_front()
	var oldest: Array = _history[0]
	var span := maxf((now - int(oldest[0])) * 0.001, 0.016)
	speed = global_position.distance_to(oldest[1]) / span


## Relaxed when empty, wrapped around a held tool, squeezed while using it.
func _animate_fingers(delta: float) -> void:
	var target := 1.0 if holding and engaged else 0.75 if holding else 0.15
	_curl = move_toward(_curl, target, delta * 4.0)
	# Posing 15 bones only matters while the curl changes, which is a fraction of the time.
	if _glove_rig == null or _curl == _posed_curl:
		return
	_posed_curl = _curl
	for finger: String in FINGERS:
		for joint in 3:
			var bone := "%s%d" % [finger, joint + 1]
			# Each joint bends its bone toward the palm (the glove's -Y); the thumb folds in less.
			var bend := _curl * JOINT_BEND[joint] * (0.55 if finger == "Thumb" else 1.0)
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


## Glove along the forearm, palm down, the grip point on the tool. Returns the wrist, where the forearm ends.
## The left glove is the right one mirrored (thumb on the other side).
func _place_glove(elbow: Vector3, owner_basis: Basis) -> Vector3:
	var along := (global_position - elbow).normalized()
	var up := (Vector3.UP - along * Vector3.UP.dot(along)).normalized()
	if up.length_squared() < 0.5:
		up = owner_basis.z
	var side := along.cross(up)
	var frame := Basis(along, up, side if index == 1 else -side)
	var wrist := global_position - frame * GRIP_POINT
	_glove.global_transform = Transform3D(frame, wrist)
	return wrist


static func _place_segment(mesh: Node3D, a: Vector3, b: Vector3) -> void:
	var y := (b - a)
	var length := y.length()
	if length < 0.001:
		return
	y /= length
	var x := y.cross(Vector3.FORWARD if absf(y.dot(Vector3.FORWARD)) < 0.9 else Vector3.RIGHT).normalized()
	var z := x.cross(y)
	mesh.global_transform = Transform3D(Basis(x, y * length, z), (a + b) * 0.5)
