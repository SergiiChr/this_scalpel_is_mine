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


func setup(target_index: int, data: Dictionary, mirrored: bool) -> void:
	index = target_index
	kind = data.get("kind", "bullet")
	remove_with = data.get("remove_with", "clamp")
	var raw: Array = data.get("uv", [0.5, 0.5])
	rest_uv = Vector2(1.0 - raw[0] if mirrored else raw[0], raw[1])
	var offset: Array = data.get("offset", [0.0, 0.0])
	uv = rest_uv + Vector2(offset[0], offset[1])
	depth = data.get("depth", 0.05)
	anchor = data.get("anchor", 0.0)
	amount = data.get("amount", 1.0)
	refill = data.get("refill", 0.0)
	surge = data.get("surge", 0.0)
	covered = data.get("covered", false)
	name = "%s_%d" % [kind.capitalize(), index]
	ModelSlot.instantiate("targets", kind, self, _placeholder)


func is_suction_target() -> bool:
	return remove_with == "suction"


func is_fragment() -> bool:
	return kind == "fragment"


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


func _placeholder(root: Node3D) -> void:
	match kind:
		"bullet":
			var slug := Shapes.capsule(root, 0.005, 0.018, Color(0.6, 0.5, 0.2))
			slug.rotation.z = PI / 2
		"knife":
			Shapes.box(root, Vector3(0.004, 0.09, 0.025), Color(0.8, 0.82, 0.85), Vector3(0, -0.02, 0), 0.1)
			Shapes.box(root, Vector3(0.02, 0.1, 0.03), Color(0.15, 0.1, 0.08), Vector3(0, 0.08, 0))
		"appendix":
			var appendix := Shapes.capsule(root, 0.008, 0.07, Color(0.7, 0.35, 0.3), Vector3.ZERO, Materials.flesh(Color(0.7, 0.35, 0.3)))
			appendix.rotation.z = PI / 2
		"tumor":
			Shapes.sphere(root, 0.018, Color.WHITE, Vector3.ZERO, Materials.flesh(Color(0.55, 0.5, 0.35)))
		"clot":
			Shapes.sphere(root, 0.01, Color.WHITE, Vector3.ZERO, Materials.flesh(Color(0.2, 0.02, 0.04)))
		"figurine":
			Shapes.capsule(root, 0.012, 0.09, Color(0.85, 0.65, 0.15), Vector3.ZERO, Materials.toon(Color(0.85, 0.65, 0.15), 0.1, true, 0.2))
		"bone":
			var bone := Shapes.cylinder(root, 0.014, 0.18, Color(0.9, 0.88, 0.78))
			bone.rotation.z = PI / 2
		"fragment":
			var fragment := Shapes.cylinder(root, 0.012, 0.07, Color(0.9, 0.86, 0.75))
			fragment.rotation.z = PI / 2
		"fluid":
			var fluid := Shapes.sphere(root, 0.03, Color.WHITE, Vector3.ZERO, Materials.flesh(Color(0.75, 0.65, 0.3)))
			fluid.scale.y = 0.4
		"air":
			var air := Shapes.sphere(root, 0.03, Color(0.8, 0.85, 0.9))
			air.transparency = 0.7
		_:
			Shapes.sphere(root, 0.01, Color.MAGENTA)
