extends RefCounted
## Holding a tool the way the game does and finding where it goes through the glove. Shared by tests/models_test.gd
## (checks every tool) and tests/fit_grips.gd (fits every grip so nothing does).

const SOLID_LAYER := 1 << 19
## Where the hand holds a tool for the check: in front of the right or left shoulder, about where it works.
const HAND_AT := Vector3(0.17, 1.05, -0.42)
const SHOULDER := Vector3(0.19, 1.4, -0.08)


## A glove under holder, the way a surgeon's hand is built.
static func make_hand(holder: Node3D, index: int) -> SurgeonHand:
	var body := Node3D.new()
	holder.add_child(body)
	var hand := SurgeonHand.new()
	body.add_child(hand)
	hand.build(index, Materials.toon_unique(Color(0.2, 0.36, 0.34)))
	return hand


## Poses the hand holding a tool of this kind with the given fit; returns the tool's model, placed in the hand,
## with a solid for point queries (wait a physics frame before querying it).
static func hold(hand: SurgeonHand, def: ToolDef, fit: Dictionary, holder: Node3D) -> Node3D:
	var side := -1.0 if hand.index == 0 else 1.0
	hand.holding = true
	hand.grip = def.grip
	hand.fit = fit
	hand.target = Vector3(HAND_AT.x * side, HAND_AT.y, HAND_AT.z)
	hand.snap_pose(shoulder(hand))
	var tool := Node3D.new()
	holder.add_child(tool)
	ToolModel.build(def, tool)
	tool.global_transform = hand.grip_transform()
	_solid(tool)
	return tool


static func shoulder(hand: SurgeonHand) -> Vector3:
	return Vector3(SHOULDER.x * (-1.0 if hand.index == 0 else 1.0), SHOULDER.y, SHOULDER.z)


## How many points inside the glove (or its part, see SurgeonHand.bone_points()) are inside the tool or too close
## to its surface: a tool may rest against the fingers, not pass through them.
static func clipped(hand: SurgeonHand, part: String = "") -> int:
	var space := hand.get_world_3d().direct_space_state
	var count := 0
	for point: Array in hand.bone_points(part):
		if _inside(space, point[0]) or _near(space, point[0], float(point[1]) * 0.5):
			count += 1
	return count


## A static trimesh of a model where it stands, for point queries.
static func _solid(model: Node3D) -> void:
	var faces := PackedVector3Array()
	for node in model.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		var xform := model.global_transform.affine_inverse() * mesh.global_transform
		for v in mesh.mesh.get_faces():
			faces.append(xform * v)
	var shape := ConcavePolygonShape3D.new()
	shape.set_faces(faces)
	shape.backface_collision = true
	var body := StaticBody3D.new()
	body.collision_layer = SOLID_LAYER
	body.collision_mask = 0
	var collision := CollisionShape3D.new()
	collision.shape = shape
	body.add_child(collision)
	model.add_child(body)


## Inside a closed mesh: a ray out from the point crosses its surface an odd number of times.
static func _inside(space: PhysicsDirectSpaceState3D, p: Vector3) -> bool:
	var direction := Vector3(1.0, 0.013, 0.007).normalized()
	var crossings := 0
	var from := p
	for i in 32:
		var query := PhysicsRayQueryParameters3D.create(from, p + direction, SOLID_LAYER)
		query.hit_back_faces = true
		var hit := space.intersect_ray(query)
		if hit.is_empty():
			break
		crossings += 1
		from = hit.position + direction * 0.0002
	return crossings % 2 == 1


static func _near(space: PhysicsDirectSpaceState3D, p: Vector3, radius: float) -> bool:
	var sphere := SphereShape3D.new()
	sphere.radius = radius
	var query := PhysicsShapeQueryParameters3D.new()
	query.shape = sphere
	query.transform = Transform3D(Basis.IDENTITY, p)
	query.collision_mask = SOLID_LAYER
	return not space.intersect_shape(query, 1).is_empty()
