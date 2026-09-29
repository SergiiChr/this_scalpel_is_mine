class_name CavityTarget
extends Node3D
## Something under the skin that the scenario cares about: a bullet, an appendix, a bone to saw, fluid to drain.
## Host owns the state and syncs it through Patient. Field meaning matches data/scenarios [patient] targets.

var index: int
var kind: String
var remove_with: String
var rest_uv: Vector2
var uv: Vector2
var depth: float
## How firmly it's attached. Cutting, sawing or a slow steady pull brings it to 0.
var anchor: float
## Suction targets: how much is left to drain.
var amount: float
var refill: float
## Extra bleeding when it comes out (knives plug their own wound).
var surge: float
var covered: bool
var extracted := false
## Tool uid currently holding it, 0 if none.
var gripped_by := 0

## A small solid the resting tool tip lands on, so tools inside an open cavity come down onto the target.
const LAYER := 64
const TOUCH_RADIUS := 0.012


func setup(target_index: int, data: Dictionary, mirrored: bool) -> void:
	index = target_index
	kind = data.get("kind", "bullet")
	remove_with = data.get("remove_with", "clamp")
	var raw: Array = data.get("uv", [0.5, 0.5])
	# uv.y runs across the body (the patient's left), so a mirrored patient flips it; uv.x runs head to feet.
	rest_uv = Vector2(raw[0], 1.0 - raw[1] if mirrored else raw[1])
	var offset: Array = data.get("offset", [0.0, 0.0])
	uv = rest_uv + Vector2(offset[0], offset[1])
	depth = data.get("depth", 0.05)
	anchor = data.get("anchor", 0.0)
	amount = data.get("amount", 1.0)
	refill = data.get("refill", 0.0)
	surge = data.get("surge", 0.0)
	covered = data.get("covered", false)
	name = "%s_%d" % [kind.capitalize(), index]
	# Degrees around the vertical, e.g. to lay a rib across the chest.
	rotation.y = deg_to_rad(data.get("yaw", 0.0))
	ModelSlot.instantiate("targets", kind, self)
	var touch := StaticBody3D.new()
	touch.name = "Touch"
	touch.collision_layer = LAYER
	touch.collision_mask = 0
	var shape := CollisionShape3D.new()
	var sphere := SphereShape3D.new()
	sphere.radius = TOUCH_RADIUS
	shape.shape = sphere
	touch.add_child(shape)
	add_child(touch)


func is_suction_target() -> bool:
	return remove_with == "suction"


## Broken bone ends to line back up (the align objective), not something to take out.
func is_fragment() -> bool:
	return kind in ["fragment", "rib"]


func is_aligned(tolerance: float = 0.01) -> bool:
	return uv.distance_to(rest_uv) < tolerance


func state() -> Array:
	return [position, anchor, amount, extracted, visible]


func apply_state(data: Array) -> void:
	position = data[0]
	anchor = data[1]
	amount = data[2]
	extracted = data[3]
	visible = data[4]
	_update_look()


func _update_look() -> void:
	if is_suction_target():
		scale = Vector3.ONE * clampf(amount / 6.0, 0.05, 1.5)
