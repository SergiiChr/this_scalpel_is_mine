class_name IvLine
extends Node3D
## The IV tubing: from the drip chamber on the stand, sagging, to the catheter in the patient's arm.
## Exists once the line is in, on every peer. It follows the patient when they're turned.
## Anyone who walks into it at full speed yanks it out (host decides, see Patient.pull_iv()).
## Crouch-walking is slow enough to step over it.

const SAMPLES := 18
const RADIUS := 0.004
const SAG := 0.55
## Only tubing hanging lower than this (meters above the floor) catches a walking surgeon's legs.
const TRIP_HEIGHT := 1.1
const TRIP_DISTANCE := 0.22
## Faster than a crouch-walk, slower than a normal walk (Surgeon.WALK_SPEED is 1.6).
const TRIP_SPEED := 0.9

var _from: Node3D
var _from_point := Vector3.ZERO
var _to: Node3D
var _to_point := Vector3.ZERO
var _mesh: MeshInstance3D
var _points := PackedVector3Array()
var _last_ends: Array[Vector3] = [Vector3.INF, Vector3.INF]


func _ready() -> void:
	_mesh = MeshInstance3D.new()
	_mesh.mesh = ImmediateMesh.new()
	_mesh.material_override = Materials.toon(Color(0.82, 0.88, 0.9), 0.1, false, 0.2)
	_mesh.top_level = true
	add_child(_mesh)
	visible = false


## from_point and to_point are local to their nodes, so the line follows whatever they're attached to.
func attach(from: Node3D, from_point: Vector3, to: Node3D, to_point: Vector3) -> void:
	_from = from
	_from_point = from_point
	_to = to
	_to_point = to_point
	_last_ends = [Vector3.INF, Vector3.INF]
	visible = true


func detach() -> void:
	_to = null
	visible = false


func is_attached() -> bool:
	return _to != null and visible


## Host: the first surgeon walking fast through the low part of the tubing, or null.
func tripped_by(surgeons: Array) -> Surgeon:
	if not is_attached():
		return null
	for surgeon: Surgeon in surgeons:
		if surgeon.walk_speed() < TRIP_SPEED:
			continue
		var feet := surgeon.global_position
		for p in _points:
			if p.y < TRIP_HEIGHT and Vector2(p.x - feet.x, p.z - feet.z).length() < TRIP_DISTANCE:
				return surgeon
	return null


func _process(_delta: float) -> void:
	if not is_attached():
		return
	var a := _from.to_global(_from_point)
	var b := _to.to_global(_to_point)
	if a.is_equal_approx(_last_ends[0]) and b.is_equal_approx(_last_ends[1]):
		return
	_last_ends = [a, b]
	_rebuild(a, b)


## A quadratic curve pulled down in the middle, like tubing hanging under its own weight.
func _rebuild(a: Vector3, b: Vector3) -> void:
	var control := (a + b) * 0.5 + Vector3.DOWN * SAG
	control.y = maxf(control.y, 0.05)
	_points.resize(SAMPLES)
	for i in SAMPLES:
		var t := float(i) / (SAMPLES - 1)
		_points[i] = a.lerp(control, t).lerp(control.lerp(b, t), t)
	var mesh := _mesh.mesh as ImmediateMesh
	mesh.clear_surfaces()
	mesh.surface_begin(Mesh.PRIMITIVE_TRIANGLES)
	const SIDES := 6
	for i in SAMPLES - 1:
		var along := (_points[i + 1] - _points[i]).normalized()
		var side := along.cross(Vector3.UP if absf(along.y) < 0.9 else Vector3.RIGHT).normalized()
		var up := side.cross(along)
		for k in SIDES:
			var a0 := TAU * k / SIDES
			var a1 := TAU * (k + 1) / SIDES
			var n0 := side * cos(a0) + up * sin(a0)
			var n1 := side * cos(a1) + up * sin(a1)
			for v: Array in [[i, n0], [i + 1, n0], [i + 1, n1], [i, n0], [i + 1, n1], [i, n1]]:
				mesh.surface_set_normal(v[1])
				mesh.surface_add_vertex(_points[v[0]] + (v[1] as Vector3) * RADIUS)
	mesh.surface_end()
