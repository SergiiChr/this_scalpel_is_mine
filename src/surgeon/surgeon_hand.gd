class_name SurgeonHand
extends Node3D
## One hand and its arm. The arm is solved with analytic two-bone IK from shoulder to hand every frame.
## Position is owned by the surgeon's peer and synced to everyone; the host uses it to drive tools.

const UPPER_ARM := 0.34
const FOREARM := 0.34
const TILT_RANGE := Vector2(-1.5, -0.2)
const LIFT_HEIGHT := 0.12

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
var _last_position := Vector3.ZERO
var _glove: MeshInstance3D
var _upper: MeshInstance3D
var _fore: MeshInstance3D
var _pusher: AnimatableBody3D


func build(hand_index: int, sleeve: Color, glove: Color) -> void:
	index = hand_index
	name = "LeftHand" if index == 0 else "RightHand"
	_glove = Shapes.box(self, Vector3(0.07, 0.03, 0.09), glove, Vector3(0, 0, 0.02), 0.05)
	_upper = Shapes.cylinder(self, 0.045, 1.0, sleeve)
	_fore = Shapes.cylinder(self, 0.035, 1.0, glove)
	for mesh in [_upper, _fore]:
		mesh.top_level = true
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
	speed = global_position.distance_to(_last_position) / maxf(delta, 0.0001)
	_last_position = global_position
	global_basis = grip_transform().basis
	_pusher.global_position = global_position
	_solve_arm(shoulder)


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
	_place_segment(_fore, elbow, global_position)


static func _place_segment(mesh: MeshInstance3D, a: Vector3, b: Vector3) -> void:
	var y := (b - a)
	var length := y.length()
	if length < 0.001:
		return
	y /= length
	var x := y.cross(Vector3.FORWARD if absf(y.dot(Vector3.FORWARD)) < 0.9 else Vector3.RIGHT).normalized()
	var z := x.cross(y)
	mesh.global_transform = Transform3D(Basis(x, y * length, z), (a + b) * 0.5)
