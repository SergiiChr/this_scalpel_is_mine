class_name PatientBody
extends Node3D
## The patient you see and touch: body model, the layered tissue at the surgical site, the cavity, organs and colliders.
## Exists on every peer. Game state lives in Patient; this node knows geometry, the wound map and the tissue sim.
##
## The surgical site is real layered tissue, not a painted decal:
## skin (TissueSim, soft and under tension) over subcutaneous fat over muscle over the cavity.
## Each layer only opens where a cut went deep enough and the sim pulled the edges apart.
##
## Body space: patient lies along X with the head at +X, origin at the body's center line.
## Site space: a plane whose local XZ maps to wound map UV, +Y points out of the skin.

enum Orientation { FACE_UP, SIDE, FACE_DOWN }

const HALF_HEIGHT := 0.11
const GOWN_COLOR := Color(0.5, 0.58, 0.55)
const SITE_LAYER := 4
const PATIENT_LAYER := 2
const CAVITY_LAYER := 32

const SKIN_THICKNESS := 0.004
const MUSCLE_THICKNESS := 0.006
## How much each layer follows the skin's movement (deeper layers are more tethered).
const LAYER_FOLLOW: Array[float] = [1.0, 0.8, 0.55]
const LAYER_DEPTH: Array[int] = [TissueSim.Depth.SKIN, TissueSim.Depth.FAT, TissueSim.Depth.MUSCLE]
const ORGAN_MODELS: PackedStringArray = ["bowel", "lobe", "sac"]
const LIMB_SITES: PackedStringArray = ["forearm", "shoulder", "thigh", "lower_leg"]

var wound_map := WoundMap.new()
var site_id: String
var site_size: Vector2
var site: Node3D
var skin_material: ShaderMaterial
var tissue := TissueSim.new()
## Subcutaneous fat, thicker on obese patients (set before build()).
var fat_thickness := 0.012
var cavity_blood: MeshInstance3D
var organs: Array[RigidBody3D] = []
var animator := PatientAnimator.new()
var orientation: int = Orientation.FACE_UP
var _on_back := false
var _body_root: Node3D
var _body_materials: Array[ShaderMaterial] = []
var _organ_rest: Array[Vector3] = []
var _site_base_y := 0.0
var _heights := PackedFloat32Array()
var _layers: Array[MeshInstance3D] = []
var _layer_version := -1
var _layer_steps := -1
var _layer_uvs := PackedVector2Array()
var _organ_last: Array[Vector3] = []
var _jiggle: Array[Vector2] = []
## Reused by part_at(), which runs every physics frame for every held tool.
var _part_query := PhysicsShapeQueryParameters3D.new()
var _part_sphere := SphereShape3D.new()


func build(site_name: String, tone: Color, age_scale: float) -> void:
	site_id = site_name
	_body_root = Node3D.new()
	_body_root.name = "BodyRoot"
	_body_root.position.y = HALF_HEIGHT * age_scale
	_body_root.scale = Vector3.ONE * age_scale
	add_child(_body_root)
	var skin := Materials.body_skin(tone)
	# The gown gets the same carve-capable material, or it would show through the surgical site on the hips.
	var gown := Materials.body_skin(GOWN_COLOR)
	_body_materials.append_array([skin, gown])
	var model := ModelSlot.instantiate("patient", "body", _body_root, {"skin": skin, "gown": gown})
	add_child(animator)
	animator.setup(self, model)
	_build_colliders()
	_build_site(tone)


func _process(delta: float) -> void:
	wound_map.flush()
	tissue.step(delta)
	if tissue.topology_version != _layer_version or tissue.steps_done != _layer_steps:
		_rebuild_layers()
	_jiggle_organs(delta)


func is_limb_site() -> bool:
	return site_id in LIMB_SITES


func cavity_depth() -> float:
	return _site_def().depth


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
	return site.to_global(Vector3((uv.x - 0.5) * site_size.x, surface_height(uv) - depth, (uv.y - 0.5) * site_size.y))


## Meters between the skin and p along the site normal. Negative = under the skin.
func height_above_site(p: Vector3) -> float:
	var local := site.to_local(p)
	return local.y - surface_height(Vector2(local.x / site_size.x + 0.5, local.z / site_size.y + 0.5))


## Skin height relative to the flat site plane at uv, from the baked height grid (0 when flat).
func surface_height(uv: Vector2) -> float:
	var grid := int(Db.site_heights.get("grid", 0))
	if _heights.size() != grid * grid or grid < 2:
		return 0.0
	var p := uv.clamp(Vector2.ZERO, Vector2.ONE) * (grid - 1)
	var x0 := mini(int(p.x), grid - 2)
	var y0 := mini(int(p.y), grid - 2)
	var f := p - Vector2(x0, y0)
	var top := lerpf(_heights[y0 * grid + x0], _heights[y0 * grid + x0 + 1], f.x)
	var bottom := lerpf(_heights[(y0 + 1) * grid + x0], _heights[(y0 + 1) * grid + x0 + 1], f.x)
	return lerpf(top, bottom, f.y)


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
		if tissue.is_open(uv):
			return {"zone": "cavity", "uv": uv, "depth": -height}
		return {"zone": "site", "uv": uv, "depth": maxf(0.0, -height)}
	var part := part_at(p)
	return {"zone": "body" if part else "none", "part": part, "uv": uv, "depth": 0.0}


func part_at(p: Vector3, radius: float = 0.025) -> String:
	_part_sphere.radius = radius
	_part_query.shape = _part_sphere
	_part_query.transform = Transform3D(Basis.IDENTITY, p)
	_part_query.collision_mask = PATIENT_LAYER
	for hit in get_world_3d().direct_space_state.intersect_shape(_part_query, 4):
		var collider: Object = hit.collider
		if collider.has_meta("part"):
			return collider.get_meta("part")
	return ""


## Cut through every layer and pulled open, so tools reach into the cavity.
func is_open(uv: Vector2) -> bool:
	return tissue.is_open(uv)


# --- Construction ----------------------------------------------------------------------------------


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
	var def := _site_def()
	site_size = Vector2(def.size[0], def.size[1])
	_on_back = def.get("back", false)
	site = Node3D.new()
	site.name = "Site"
	site.position = Vector3(def.pos[0], def.pos[1], def.pos[2])
	if _on_back:
		site.rotation.x = PI
	_site_base_y = site.position.y
	_body_root.add_child(site)

	_heights = PackedFloat32Array(Db.site_heights.get(site_id, []))
	tissue.build(site_size, surface_height)
	skin_material = Materials.skin_site(tone, wound_map.textures[0], wound_map.textures[1])
	for i in 3:
		var layer := MeshInstance3D.new()
		layer.name = ["Skin", "Fat", "Muscle"][i]
		layer.mesh = ArrayMesh.new()
		layer.material_override = skin_material if i == 0 else Materials.tissue_layer(i - 1, wound_map.textures[1])
		site.add_child(layer)
		_layers.append(layer)

	var collider := Shapes.static_box(site, Vector3(site_size.x, 0.004, site_size.y), Vector3(0, -0.002, 0), SITE_LAYER)
	collider.set_meta("site", true)
	_build_cavity()
	_update_carve.call_deferred()


## Rebuilds the skin, fat and muscle meshes from the tissue sim.
## Deeper layers sit lower and follow the skin less. Triangles over an open gap are left out, so you see through.
func _rebuild_layers() -> void:
	_layer_version = tissue.topology_version
	_layer_steps = tissue.steps_done
	var res := TissueSim.RES
	var severed := tissue.any_severed()
	if _layer_uvs.is_empty():
		for k in tissue.rest.size():
			_layer_uvs.append(tissue.uv_of(k))
	for layer in 3:
		var instance := _layers[layer]
		var mesh := instance.mesh as ArrayMesh
		mesh.clear_surfaces()
		instance.visible = layer == 0 or severed
		if not instance.visible:
			continue
		var down := Vector3(0, [0.0, SKIN_THICKNESS, SKIN_THICKNESS + fat_thickness][layer] as float, 0)
		var follow := LAYER_FOLLOW[layer]
		var verts := PackedVector3Array()
		verts.resize(tissue.rest.size())
		for k in verts.size():
			verts[k] = tissue.rest[k] + (tissue.pos[k] - tissue.rest[k]) * follow - down
		# Smooth grid normals from neighbouring particles (cross of the z and x tangents points out of the skin).
		var normals := PackedVector3Array()
		normals.resize(verts.size())
		for j in res + 1:
			for i in res + 1:
				var dx := verts[tissue.index(mini(i + 1, res), j)] - verts[tissue.index(maxi(i - 1, 0), j)]
				var dz := verts[tissue.index(i, mini(j + 1, res))] - verts[tissue.index(i, maxi(j - 1, 0))]
				normals[tissue.index(i, j)] = dz.cross(dx).normalized()
		var arrays := []
		arrays.resize(Mesh.ARRAY_MAX)
		arrays[Mesh.ARRAY_VERTEX] = verts
		arrays[Mesh.ARRAY_NORMAL] = normals
		arrays[Mesh.ARRAY_TEX_UV] = _layer_uvs
		arrays[Mesh.ARRAY_INDEX] = tissue.triangles(LAYER_DEPTH[layer])
		mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)


## Cavity walls whose rim follows the skin just underneath it, so nothing pokes out of the body.
func _cavity_mesh(depth: float) -> ArrayMesh:
	const STEPS := 24
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	var inset := 0.02
	var corners := [Vector2(inset, inset), Vector2(1 - inset, inset), Vector2(1 - inset, 1 - inset), Vector2(inset, 1 - inset)]
	for side in 4:
		var a: Vector2 = corners[side]
		var b: Vector2 = corners[(side + 1) % 4]
		for i in STEPS:
			var u0 := a.lerp(b, float(i) / STEPS)
			var u1 := a.lerp(b, float(i + 1) / STEPS)
			var top0 := _site_point(u0, surface_height(u0) - SKIN_THICKNESS)
			var top1 := _site_point(u1, surface_height(u1) - SKIN_THICKNESS)
			var bottom0 := _site_point(u0, -depth)
			var bottom1 := _site_point(u1, -depth)
			for v: Vector3 in [top0, bottom0, top1, top1, bottom0, bottom1]:
				st.add_vertex(v)
	var floor_corners: Array = corners.map(func(c: Vector2) -> Vector3: return _site_point(c, -depth))
	for v: Vector3 in [floor_corners[0], floor_corners[1], floor_corners[2], floor_corners[0], floor_corners[2], floor_corners[3]]:
		st.add_vertex(v)
	st.generate_normals()
	return st.commit()


func _site_point(uv: Vector2, height: float) -> Vector3:
	return Vector3((uv.x - 0.5) * site_size.x, height, (uv.y - 0.5) * site_size.y)


func _site_def() -> Dictionary:
	return Db.patient_sites.get(site_id, Db.patient_sites.get("abdomen", {}))


func _build_cavity() -> void:
	var depth := cavity_depth()
	var cavity := MeshInstance3D.new()
	cavity.name = "Cavity"
	cavity.mesh = _cavity_mesh(depth)
	cavity.material_override = Materials.flesh()
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
	var model := ModelSlot.instantiate("organs", ORGAN_MODELS[organs.size() % ORGAN_MODELS.size()], organ, {"organ": Materials.flesh(color)})
	model.name = "Model"
	model.scale = Vector3.ONE * radius
	site.add_child(organ)
	organ.position = Vector3((uv.x - 0.5) * site_size.x, -depth, (uv.y - 0.5) * site_size.y)
	organs.append(organ)
	_organ_rest.append(organ.position)
	_organ_last.append(organ.position)
	_jiggle.append(Vector2.ZERO)
	return organ


## Host: organs drift back to where they belong once you stop pushing them.
func settle_organs() -> void:
	for i in organs.size():
		var organ := organs[i]
		organ.apply_central_force((_organ_rest[i] - organ.position) * 40.0 * organ.mass)


## Soft organs: a damped spring squashes and stretches each organ when it's pushed, on every peer.
func _jiggle_organs(delta: float) -> void:
	if delta <= 0.0:
		return
	for i in organs.size():
		var organ := organs[i]
		var velocity := (organ.position - _organ_last[i]) / delta
		_organ_last[i] = organ.position
		var state := _jiggle[i]
		state.y += (-state.x * 180.0 - state.y * 9.0 + clampf(velocity.length() * 6.0, 0.0, 3.0)) * delta
		state.x += state.y * delta
		_jiggle[i] = state
		var squash := clampf(state.x, -0.25, 0.25)
		var model := organ.get_node_or_null("Model") as Node3D
		if model:
			var radius := (organ.get_child(0) as CollisionShape3D).shape.get("radius") as float
			model.scale = Vector3(1.0 + squash, 1.0 - squash, 1.0 + squash) * radius


func organ_offset(index: int) -> float:
	return organs[index].position.distance_to(_organ_rest[index])


func set_organ_damage(index: int, amount: float) -> void:
	for mesh in organs[index].find_children("*", "MeshInstance3D", true, false):
		var mat := (mesh as MeshInstance3D).get_surface_override_material(0) as ShaderMaterial
		if mat:
			mat.set_shader_parameter("damage", amount)


func organ_states() -> Array:
	return organs.map(func(o: RigidBody3D) -> Vector3: return o.position)


func apply_organ_states(positions: Array) -> void:
	for i in mini(positions.size(), organs.size()):
		organs[i].position = positions[i]


## Breathing lifts sites that sit on top of the torso together with the chest.
func set_breath_offset(offset: float) -> void:
	if site_id in ["abdomen", "chest", "shoulder"]:
		site.position.y = _site_base_y + offset
		_update_carve()


func set_cavity_blood(level: float) -> void:
	var depth := cavity_depth()
	cavity_blood.position.y = -depth + 0.002 + clampf(level, 0.0, 1.0) * depth * 0.85


## The body model is cut away under the whole site; the simulated skin layer takes its place.
func _update_carve() -> void:
	for mat in _body_materials:
		Materials.set_carve(mat, site.global_transform, site_size * 0.5, cavity_depth() + 0.02, Materials.white())
