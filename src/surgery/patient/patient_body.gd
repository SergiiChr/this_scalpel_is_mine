class_name PatientBody
extends Node3D
## The patient you see and touch: mannequin, surgical site skin, the cavity under it, organs and colliders.
## Exists on every peer. Game state lives in Patient; this node only knows geometry and the wound map.
##
## Body space: patient lies along X with the head at +X, origin at the body's center line.
## Site space: a plane whose local XZ maps to wound map UV, +Y points out of the skin.

enum Orientation { FACE_UP, SIDE, FACE_DOWN }

const HALF_HEIGHT := 0.11
const SITE_LAYER := 4
const PATIENT_LAYER := 2
const CAVITY_LAYER := 32

## Where each scenario site sits on the body. "back" sites face down until the patient is turned over.
const SITES: Dictionary = {
	"abdomen": {"pos": Vector3(0.0, 0.112, 0.0), "size": Vector2(0.3, 0.3), "depth": 0.12},
	"chest": {"pos": Vector3(0.3, 0.112, 0.0), "size": Vector2(0.26, 0.3), "depth": 0.12},
	"back": {"pos": Vector3(0.15, -0.112, 0.0), "size": Vector2(0.3, 0.3), "depth": 0.12, "back": true},
	"neck": {"pos": Vector3(0.5, 0.06, 0.0), "size": Vector2(0.12, 0.1), "depth": 0.05},
	"head": {"pos": Vector3(0.72, 0.13, 0.0), "size": Vector2(0.12, 0.14), "depth": 0.05},
	"face": {"pos": Vector3(0.64, 0.135, 0.0), "size": Vector2(0.1, 0.1), "depth": 0.05},
	"shoulder": {"pos": Vector3(0.38, 0.112, 0.15), "size": Vector2(0.14, 0.14), "depth": 0.05},
	"forearm": {"pos": Vector3(0.2, 0.027, 0.25), "size": Vector2(0.22, 0.09), "depth": 0.05},
	"thigh": {"pos": Vector3(-0.55, 0.052, 0.1), "size": Vector2(0.25, 0.14), "depth": 0.05},
	"lower_leg": {"pos": Vector3(-0.95, 0.052, 0.1), "size": Vector2(0.25, 0.14), "depth": 0.05},
}
const LIMB_SITES: PackedStringArray = ["forearm", "shoulder", "thigh", "lower_leg"]

var wound_map := WoundMap.new()
var site_id: String
var site_size: Vector2
var site: Node3D
var skin_material: ShaderMaterial
var cavity_blood: MeshInstance3D
var organs: Array[RigidBody3D] = []
var orientation: int = Orientation.FACE_UP
var _on_back := false
var _body_root: Node3D
var _body_materials: Array[ShaderMaterial] = []
var _organ_rest: Array[Vector3] = []


func build(site_name: String, tone: Color, age_scale: float) -> void:
	site_id = site_name
	_body_root = Node3D.new()
	_body_root.name = "BodyRoot"
	_body_root.position.y = HALF_HEIGHT * age_scale
	_body_root.scale = Vector3.ONE * age_scale
	add_child(_body_root)
	ModelSlot.instantiate("patient", "body", _body_root, func(root: Node3D) -> void: _build_mannequin(root, tone))
	_build_colliders()
	_build_site(tone)


func _process(_delta: float) -> void:
	wound_map.flush()


func is_limb_site() -> bool:
	return site_id in LIMB_SITES


func cavity_depth() -> float:
	return SITES.get(site_id, SITES.abdomen).depth


func site_active() -> bool:
	return orientation == (Orientation.FACE_DOWN if _on_back else Orientation.FACE_UP)


func set_orientation(value: int) -> void:
	orientation = value
	_body_root.rotation.x = [0.0, PI / 2, PI][value]
	_update_carve()


# --- Space conversion ------------------------------------------------------------------------------


func world_to_uv(p: Vector3) -> Vector2:
	var local := site.to_local(p)
	return Vector2(local.x / site_size.x + 0.5, local.z / site_size.y + 0.5)


func uv_to_world(uv: Vector2, depth: float = 0.0) -> Vector3:
	return site.to_global(Vector3((uv.x - 0.5) * site_size.x, -depth, (uv.y - 0.5) * site_size.y))


## Meters between the site surface and p along the skin normal. Negative = under the skin.
func height_above_site(p: Vector3) -> float:
	return site.to_local(p).y


func uv_to_meters(uv_length: float) -> float:
	return uv_length * (site_size.x + site_size.y) * 0.5


func meters_to_uv(meters: float) -> float:
	return meters / ((site_size.x + site_size.y) * 0.5)


## What a tool tip at p is touching:
## zone = "air" (above the site), "site" (skin), "cavity" (inside an opening), "body" (other part), "none".
func probe(p: Vector3) -> Dictionary:
	var uv := world_to_uv(p)
	var height := height_above_site(p)
	var on_site := site_active() and uv.x >= 0.0 and uv.x <= 1.0 and uv.y >= 0.0 and uv.y <= 1.0
	if on_site and height > -cavity_depth():
		if height > 0.012:
			return {"zone": "air", "uv": uv, "depth": 0.0}
		if wound_map.is_open(uv) or height < -0.01 and _is_open_near(uv):
			return {"zone": "cavity", "uv": uv, "depth": -height}
		return {"zone": "site", "uv": uv, "depth": maxf(0.0, -height)}
	var part := part_at(p)
	return {"zone": "body" if part else "none", "part": part, "uv": uv, "depth": 0.0}


func part_at(p: Vector3, radius: float = 0.025) -> String:
	var query := PhysicsShapeQueryParameters3D.new()
	var sphere := SphereShape3D.new()
	sphere.radius = radius
	query.shape = sphere
	query.transform = Transform3D(Basis.IDENTITY, p)
	query.collision_mask = PATIENT_LAYER
	for hit in get_world_3d().direct_space_state.intersect_shape(query, 4):
		var collider: Object = hit.collider
		if collider.has_meta("part"):
			return collider.get_meta("part")
	return ""


func _is_open_near(uv: Vector2) -> bool:
	var r := 0.02
	for offset: Vector2 in [Vector2.ZERO, Vector2(r, 0), Vector2(-r, 0), Vector2(0, r), Vector2(0, -r)]:
		if wound_map.is_open(uv + offset):
			return true
	return false


# --- Construction ----------------------------------------------------------------------------------


func _build_mannequin(root: Node3D, tone: Color) -> void:
	var skin := Materials.body_skin(tone)
	_body_materials.append(skin)
	var gown := Materials.toon(Color(0.42, 0.52, 0.5), 0.45)
	_part_mesh(root, BoxMesh.new(), Vector3(0.62, 0.22, 0.38), Vector3(0.12, 0, 0), skin)
	_part_mesh(root, BoxMesh.new(), Vector3(0.22, 0.2, 0.36), Vector3(-0.3, -0.005, 0), gown)
	_capsule(root, 0.055, 0.16, Vector3(0.5, 0.0, 0.0), skin, true)
	Shapes.sphere(root, 0.105, tone, Vector3(0.67, 0.03, 0.0), skin)
	for side: float in [-1.0, 1.0]:
		_capsule(root, 0.045, 0.62, Vector3(0.12, -0.02, 0.25 * side), skin, true)
		_capsule(root, 0.07, 0.9, Vector3(-0.8, -0.02, 0.1 * side), skin, true)


func _capsule(root: Node3D, radius: float, height: float, pos: Vector3, mat: Material, along_x: bool) -> void:
	var mesh := CapsuleMesh.new()
	mesh.radius = radius
	mesh.height = height
	var instance := _part_mesh(root, mesh, Vector3.ZERO, pos, mat)
	if along_x:
		instance.rotation.z = PI / 2


func _part_mesh(root: Node3D, mesh: PrimitiveMesh, size: Vector3, pos: Vector3, mat: Material) -> MeshInstance3D:
	if mesh is BoxMesh:
		(mesh as BoxMesh).size = size
	var instance := MeshInstance3D.new()
	instance.mesh = mesh
	instance.material_override = mat
	instance.position = pos
	root.add_child(instance)
	return instance


func _build_colliders() -> void:
	var parts: Dictionary = {
		"torso": [Vector3(0.62, 0.22, 0.38), Vector3(0.12, 0, 0)],
		"pelvis": [Vector3(0.22, 0.2, 0.36), Vector3(-0.3, 0, 0)],
		"neck": [Vector3(0.14, 0.1, 0.1), Vector3(0.5, 0, 0)],
		"head": [Vector3(0.2, 0.2, 0.2), Vector3(0.67, 0.03, 0)],
		"arm_right": [Vector3(0.7, 0.09, 0.09), Vector3(0.12, -0.02, 0.25)],
		"arm_left": [Vector3(0.7, 0.09, 0.09), Vector3(0.12, -0.02, -0.25)],
		"leg_right": [Vector3(0.95, 0.14, 0.14), Vector3(-0.8, -0.02, 0.1)],
		"leg_left": [Vector3(0.95, 0.14, 0.14), Vector3(-0.8, -0.02, -0.1)],
	}
	for part: String in parts:
		var body := Shapes.static_box(_body_root, parts[part][0], parts[part][1], PATIENT_LAYER)
		body.name = part.to_pascal_case()
		body.set_meta("part", part)


func _build_site(tone: Color) -> void:
	var def: Dictionary = SITES.get(site_id, SITES.abdomen)
	site_size = def.size
	_on_back = def.get("back", false)
	site = Node3D.new()
	site.name = "Site"
	site.position = def.pos + Vector3(0, -0.002 if _on_back else 0.002, 0)
	if _on_back:
		site.rotation.x = PI
	_body_root.add_child(site)

	var plane := PlaneMesh.new()
	plane.size = site_size
	plane.subdivide_width = 48
	plane.subdivide_depth = 48
	skin_material = Materials.skin_site(tone, wound_map.textures[0], wound_map.textures[1], site_size)
	var mesh := MeshInstance3D.new()
	mesh.name = "Skin"
	mesh.mesh = plane
	mesh.material_override = skin_material
	site.add_child(mesh)

	var collider := Shapes.static_box(site, Vector3(site_size.x, 0.004, site_size.y), Vector3(0, -0.002, 0), SITE_LAYER)
	collider.set_meta("site", true)
	_build_cavity()
	_update_carve.call_deferred()


func _build_cavity() -> void:
	var depth := cavity_depth()
	var walls := BoxMesh.new()
	walls.size = Vector3(site_size.x * 0.96, depth, site_size.y * 0.96)
	walls.flip_faces = true
	var cavity := MeshInstance3D.new()
	cavity.name = "Cavity"
	cavity.mesh = walls
	cavity.material_override = Materials.flesh()
	cavity.position.y = -depth * 0.5
	site.add_child(cavity)
	Shapes.static_box(site, Vector3(site_size.x, 0.01, site_size.y), Vector3(0, -depth - 0.005, 0), CAVITY_LAYER)

	var pool := PlaneMesh.new()
	pool.size = site_size * 0.95
	cavity_blood = MeshInstance3D.new()
	cavity_blood.name = "CavityBlood"
	cavity_blood.mesh = pool
	cavity_blood.material_override = Materials.blood_pool()
	cavity_blood.position.y = -depth + 0.002
	site.add_child(cavity_blood)


## Adds a pushable organ blob. Only the host simulates them, clients get transforms from Patient.
func add_organ(uv: Vector2, depth: float, radius: float, color: Color) -> RigidBody3D:
	var organ := RigidBody3D.new()
	organ.name = "Organ%d" % organs.size()
	organ.collision_layer = CAVITY_LAYER
	organ.collision_mask = CAVITY_LAYER
	organ.gravity_scale = 0.0
	organ.linear_damp = 6.0
	organ.angular_damp = 6.0
	organ.mass = 0.3
	organ.freeze = not multiplayer.is_server()
	var shape := CollisionShape3D.new()
	var sphere := SphereShape3D.new()
	sphere.radius = radius
	shape.shape = sphere
	organ.add_child(shape)
	var mesh := Shapes.sphere(organ, radius, color, Vector3.ZERO, Materials.flesh(color))
	mesh.scale = Vector3(1.3, 0.7, 1.0)
	site.add_child(organ)
	organ.position = Vector3((uv.x - 0.5) * site_size.x, -depth, (uv.y - 0.5) * site_size.y)
	organs.append(organ)
	_organ_rest.append(organ.position)
	return organ


## Host: organs drift back to where they belong once you stop pushing them.
func settle_organs() -> void:
	for i in organs.size():
		var organ := organs[i]
		organ.apply_central_force((_organ_rest[i] - organ.position) * 40.0 * organ.mass)


func organ_states() -> Array:
	return organs.map(func(o: RigidBody3D) -> Vector3: return o.position)


func apply_organ_states(positions: Array) -> void:
	for i in mini(positions.size(), organs.size()):
		organs[i].position = positions[i]


func set_cavity_blood(level: float) -> void:
	var depth := cavity_depth()
	cavity_blood.position.y = -depth + 0.002 + clampf(level, 0.0, 1.0) * depth * 0.85


func _update_carve() -> void:
	var depth := cavity_depth()
	var box := site.global_transform.translated_local(Vector3(0, -depth * 0.5 + 0.01, 0))
	var extent := Vector3(site_size.x * 0.48, depth * 0.5 + 0.012, site_size.y * 0.48)
	for mat in _body_materials:
		Materials.set_carve(mat, box, extent)
