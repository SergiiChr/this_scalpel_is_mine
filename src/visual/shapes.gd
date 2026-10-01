class_name Shapes
extends RefCounted
## Room architecture (walls, floors), invisible colliders, 3D labels and tubes along a path (bones, veins, tubing).
## Everything else is a model.


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


## A closed tube with an elliptic cross section (width across the path, height up) along a path (local space; the cross section keeps +Y as up where it can).
static func tube(path: PackedVector3Array, width: float, height: float) -> ArrayMesh:
	const SIDES := 12
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	var rings: Array[PackedVector3Array] = []
	for i in path.size():
		var along := (path[mini(i + 1, path.size() - 1)] - path[maxi(i - 1, 0)]).normalized()
		var side := along.cross(Vector3.UP).normalized()
		if side.length_squared() < 0.5:
			side = Vector3.RIGHT
		var up := side.cross(along).normalized()
		var ring := PackedVector3Array()
		for k in SIDES:
			var angle := TAU * k / SIDES
			ring.append(path[i] + side * cos(angle) * width + up * sin(angle) * height)
		rings.append(ring)
	for i in range(1, rings.size()):
		for k in SIDES:
			var n := (k + 1) % SIDES
			# Clockwise seen from outside: Godot's front faces.
			for v: Vector3 in [rings[i - 1][k], rings[i][n], rings[i][k], rings[i - 1][k], rings[i - 1][n], rings[i][n]]:
				st.add_vertex(v)
	for end: int in [0, rings.size() - 1]:
		for k in SIDES:
			var tri: Array[Vector3] = [path[end], rings[end][(k + 1) % SIDES], rings[end][k]]
			if end != 0:
				tri.reverse()
			for v in tri:
				st.add_vertex(v)
	st.generate_normals()
	return st.commit()
