class_name Shapes
extends RefCounted
## Room architecture (walls, floors), invisible colliders and 3D labels. Everything else is a model.


static func slab(parent: Node3D, size: Vector3, color: Color, pos: Vector3, grime: float = 0.35) -> MeshInstance3D:
	var mesh := BoxMesh.new()
	mesh.size = size
	var instance := MeshInstance3D.new()
	instance.mesh = mesh
	instance.material_override = Materials.environment(color, grime)
	instance.position = pos
	parent.add_child(instance)
	return instance


static func static_box(parent: Node3D, size: Vector3, pos: Vector3, layer: int = 1) -> StaticBody3D:
	var body := StaticBody3D.new()
	body.collision_layer = layer
	body.collision_mask = 0
	body.position = pos
	var shape := CollisionShape3D.new()
	var box_shape := BoxShape3D.new()
	box_shape.size = size
	shape.shape = box_shape
	body.add_child(shape)
	parent.add_child(body)
	return body


static func label(parent: Node3D, text: String, pos: Vector3, size: int = 32) -> Label3D:
	var l := Label3D.new()
	l.text = text
	l.font_size = size
	l.pixel_size = 0.001
	l.position = pos
	l.modulate = Color(0.75, 0.9, 0.78)
	parent.add_child(l)
	return l
