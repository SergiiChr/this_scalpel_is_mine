class_name Shapes
extends RefCounted
## Placeholder geometry helpers. Real models replace these through ModelSlot, see assets/models/README.md.


static func box(parent: Node3D, size: Vector3, color: Color, pos: Vector3 = Vector3.ZERO, grime: float = 0.25) -> MeshInstance3D:
	var mesh := BoxMesh.new()
	mesh.size = size
	return _add(parent, mesh, Materials.toon(color, grime), pos)


## Room surfaces: smooth shading so big flat walls don't show light bands.
static func slab(parent: Node3D, size: Vector3, color: Color, pos: Vector3, grime: float = 0.6) -> MeshInstance3D:
	var mesh := BoxMesh.new()
	mesh.size = size
	return _add(parent, mesh, Materials.environment(color, grime), pos)


static func cylinder(parent: Node3D, radius: float, height: float, color: Color, pos: Vector3 = Vector3.ZERO, top_radius: float = -1.0) -> MeshInstance3D:
	var mesh := CylinderMesh.new()
	mesh.bottom_radius = radius
	mesh.top_radius = radius if top_radius < 0.0 else top_radius
	mesh.height = height
	mesh.radial_segments = 16
	return _add(parent, mesh, Materials.toon(color), pos)


static func sphere(parent: Node3D, radius: float, color: Color, pos: Vector3 = Vector3.ZERO, material: Material = null) -> MeshInstance3D:
	var mesh := SphereMesh.new()
	mesh.radius = radius
	mesh.height = radius * 2.0
	return _add(parent, mesh, material if material else Materials.toon(color), pos)


static func capsule(parent: Node3D, radius: float, height: float, color: Color, pos: Vector3 = Vector3.ZERO, material: Material = null) -> MeshInstance3D:
	var mesh := CapsuleMesh.new()
	mesh.radius = radius
	mesh.height = height
	return _add(parent, mesh, material if material else Materials.toon(color), pos)


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


static func _add(parent: Node3D, mesh: Mesh, material: Material, pos: Vector3) -> MeshInstance3D:
	var instance := MeshInstance3D.new()
	instance.mesh = mesh
	instance.material_override = material
	instance.position = pos
	parent.add_child(instance)
	return instance
