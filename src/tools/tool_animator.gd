class_name ToolAnimator
extends RefCounted
## Moves a tool model's named parts from the holding hand's state, which every peer has.
## Jaws close while squeezed or holding tissue, a syringe plunger sits behind its liquid and air, triggers squeeze, saw blades oscillate,
## flames and glows light up in use. Part names come from tools/assetgen/instruments.py.

const PART_NAMES: PackedStringArray = ["JawA", "JawB", "Plunger", "Trigger", "Blade", "Flame", "Glow", "Light"]
const JAW_OPEN := 0.12

## How far a syringe's plunger is pulled out (0..1 of its volume): its liquid and any air drawn in.
## The plunger moves the moment it changes, with the liquid, not a frame later.
var fill := 0.0:
	set(value):
		fill = value
		_pose("Plunger", Basis.IDENTITY, Vector3(0, 0, _plunger_travel * fill))
## A retractor's jaws stand at this angle (radians), set by its wheel rather than squeezed, NAN for other jaws.
var opening := NAN
var _parts: Dictionary = {}
var _rest: Dictionary = {}
var _squeeze := 0.0
var _time := 0.0
## How far the plunger moves from empty to full: the length of the full "Level" part.
var _plunger_travel := 0.0


func setup(model: Node3D) -> void:
	_parts = ModelSlot.parts(model, PART_NAMES)
	for part_name: String in _parts:
		_rest[part_name] = (_parts[part_name] as Node3D).transform
	var level := model.find_child("Level", true, false) as MeshInstance3D
	if level:
		_plunger_travel = level.get_aabb().size.z
	_animate_parts(false, false)


## Stands a retractor's jaws open so the tips of its rakes, at `tip_z` along the tool, are `spread` meters apart.
## Closed (ToolActions.SPREAD_RANGE.x) they rest as modelled; each jaw swings about its hinge, the model's part origin.
func open_to(spread: float, tip_z: float) -> void:
	var hinge: Transform3D = _rest.get("JawA", Transform3D())
	var reach := hinge.origin.z - tip_z
	var rest_half := ToolActions.SPREAD_RANGE.x * 0.5
	opening = asin(clampf(spread * 0.5 / Vector2(rest_half, reach).length(), -1.0, 1.0)) - atan2(rest_half, reach)
	_animate_parts(false, false)


## active: the tool is being used right now. closed: jaws clamped on something.
func animate(active: bool, closed: bool, delta: float) -> void:
	if _parts.is_empty():
		return
	_time += delta
	_squeeze = move_toward(_squeeze, 1.0 if active else 0.0, delta * 6.0)
	_animate_parts(active, closed)


func _animate_parts(active: bool, closed: bool) -> void:
	var jaw := opening if not is_nan(opening) else 0.0 if closed or active else JAW_OPEN
	_pose("JawA", Basis(Vector3.UP, jaw), Vector3.ZERO)
	_pose("JawB", Basis(Vector3.UP, -jaw), Vector3.ZERO)
	_pose("Plunger", Basis.IDENTITY, Vector3(0, 0, _plunger_travel * fill))
	_pose("Trigger", Basis(Vector3.RIGHT, -0.35 * _squeeze), Vector3.ZERO)
	_pose("Blade", Basis.IDENTITY, Vector3(sin(_time * 70.0) * 0.004 * float(active), 0, 0))
	var flicker := 1.0 + sin(_time * 31.0) * 0.15 + sin(_time * 53.0) * 0.1
	for glow: String in ["Flame", "Glow"]:
		if _parts.has(glow):
			(_parts[glow] as Node3D).visible = active
			_pose(glow, Basis.from_scale(Vector3(1.0, 1.0, flicker)), Vector3.ZERO)
	if _parts.has("Light"):
		(_parts["Light"] as Node3D).visible = active and fmod(_time * 4.0, 1.0) < 0.6


func _pose(part_name: String, rotation: Basis, offset: Vector3) -> void:
	var node: Node3D = _parts.get(part_name)
	if node:
		var rest: Transform3D = _rest[part_name]
		node.transform = Transform3D(rest.basis * rotation, rest.origin + offset)
