class_name ToolModel
extends RefCounted
## Placeholder tool meshes built from ToolDef look fields. The grip is at the origin and the tip points along -Z.


static func build(def: ToolDef, parent: Node3D) -> Node3D:
	return ModelSlot.instantiate("tools", def.model if def.model else def.id, parent, func(root: Node3D) -> void: _placeholder(def, root))


static func _placeholder(def: ToolDef, root: Node3D) -> void:
	var l := def.length
	var w := def.width
	var handle_len := l * 0.55
	var tip_len := l - handle_len
	var tip_center := Vector3(0, 0, -handle_len - tip_len * 0.5)
	var metal := Color(0.78, 0.8, 0.83)
	match def.tip:
		"bottle", "bag", "cap", "cloth", "pad", "block":
			_whole_body(def, root)
			return
	var handle := Shapes.cylinder(root, w * 0.5, handle_len, def.color, Vector3(0, 0, -handle_len * 0.5 + 0.01))
	handle.rotation.x = PI / 2
	match def.tip:
		"blade":
			Shapes.box(root, Vector3(w * 0.15, w * 0.9, tip_len), metal, tip_center + Vector3(0, -w * 0.2, 0), 0.05)
		"point":
			var cone := Shapes.cylinder(root, w * 0.4, tip_len, metal, tip_center, 0.0)
			cone.rotation.x = -PI / 2
		"jaw":
			for side in [-1.0, 1.0]:
				var jaw := Shapes.box(root, Vector3(w * 0.25, w * 0.25, tip_len), metal, tip_center + Vector3(side * w * 0.35, 0, 0), 0.05)
				jaw.rotation.y = side * 0.08
		"hook":
			var shaft := Shapes.cylinder(root, w * 0.12, tip_len, metal, tip_center)
			shaft.rotation.x = PI / 2
			Shapes.box(root, Vector3(w * 1.2, w * 0.2, w * 0.3), metal, Vector3(0, -w * 0.3, -l))
		"tube":
			var tube := Shapes.cylinder(root, w * 0.3, tip_len, metal, tip_center)
			tube.rotation.x = PI / 2
		"flame":
			Shapes.sphere(root, w * 0.35, Color.ORANGE, Vector3(0, 0, -l), Materials.glow(Color(1.0, 0.55, 0.1)))
		"saw":
			Shapes.box(root, Vector3(w * 0.08, w, tip_len), metal, tip_center + Vector3(0, -w * 0.3, 0), 0.05)
		_:
			Shapes.box(root, Vector3(w, w, tip_len), metal, tip_center)


## Items that are one shape rather than handle + tip.
static func _whole_body(def: ToolDef, root: Node3D) -> void:
	var l := def.length
	var w := def.width
	var center := Vector3(0, 0, -l * 0.5)
	match def.tip:
		"bottle":
			var bottle := Shapes.cylinder(root, w * 0.5, l, def.color, center)
			bottle.rotation.x = PI / 2
		"bag":
			Shapes.box(root, Vector3(w, w * 0.25, l), def.color, center, 0.1)
		"cap":
			Shapes.sphere(root, w * 0.5, def.color, center)
		"cloth":
			var cloth := Shapes.sphere(root, w * 0.5, def.color, center)
			cloth.scale = Vector3(1.0, 0.35, 1.0)
		_:
			Shapes.box(root, Vector3(w, w * 0.5, l), def.color, center)
