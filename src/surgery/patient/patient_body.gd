class_name PatientBody
extends Node3D
## The patient you see and touch: body model, the layered tissue at the surgical site, the cavity, organs and colliders.
## Exists on every peer. Game state lives in Patient; this node knows geometry, the wound map and the tissue sim.
##
## The surgical site is real layered tissue: skin (TissueSim, soft and under tension) over subcutaneous fat over
## muscle over the cavity. Each layer only opens where a cut went deep enough and the sim pulled the edges apart.
## Where nothing is cut or held, the body model itself is the skin and shows the painted damage (wound maps);
## around cuts and pinched skin the model is cut away and the simulated layers take over (the region).
##
## Body space: patient lies along X with the head at +X, origin at the body's center line.
## Site space: a plane whose local XZ maps to wound map UV, +Y points out of the skin.

enum Orientation { FACE_UP, SIDE, FACE_DOWN }


## How one layer's mesh is put together (see _plan_layers()): per vertex the particle it belongs to (owner), and for a
## crossing the spring's other end, how far along it the blade crossed (share) and the lip neighbour it slides with
## (slide, -1 for none); its uv; the triangle indices; and per wall quad its two top vertices and a vertex of its own
## side's skin, to face it away from.
class LayerPlan:
	var owner := PackedInt32Array()
	var other := PackedInt32Array()
	var share := PackedFloat32Array()
	var slide := PackedInt32Array()
	var uv := PackedVector2Array()
	var index := PackedInt32Array()
	var wall := PackedInt32Array()

const HALF_HEIGHT := 0.11
const SITE_LAYER := 4
const PATIENT_LAYER := 2
const CAVITY_LAYER := 32
## Hands push organs aside on their own layer, so rays looking for what's in the cavity don't hit the hands.
const PUSHER_LAYER := 128
## The patient's real skin (body and gown meshes at rest), for resting hands and tools on. The boxes on
## PATIENT_LAYER stay for what a tool touches, they're too rough to rest a hand on without sinking into a leg.
const SURFACE_LAYER := 256

const SKIN_THICKNESS := 0.004
const MUSCLE_THICKNESS := 0.006
## How much each layer follows the skin's movement (deeper layers are more tethered).
const LAYER_FOLLOW: Array[float] = [1.0, 0.8, 0.55]
const LAYER_DEPTH: Array[int] = [TissueSim.Depth.SKIN, TissueSim.Depth.FAT, TissueSim.Depth.MUSCLE]
## Skin pulled this far (meters) takes its deeper layers fully along, see layer_point().
const FLAP_MOVE := 0.04
## Grid step of the drape's height under folded-out skin (meters, see _lay_skin_on_drape()).
const DRAPE_FLOOR_CELL := 0.01
## Organs that belong under each site, for organs placed without a model of their own (one over a hidden target,
## filler in a deep site without anatomy data), in the order they're used.
const SITE_ORGANS: Dictionary = {
	"abdomen": ["bowel", "lobe", "sac"], "chest": ["lung", "heart", "lung"], "back": ["kidney", "bowel", "kidney"],
}
const LIMB_SITES: PackedStringArray = ["forearm", "shoulder", "thigh", "lower_leg"]
const BONE_COLOR := Color(0.86, 0.81, 0.68)
## A vein drawn along the inside of each forearm, where a syringe draws blood or gives a drug straight into the blood.
## It runs over this part of the forearm (0 the elbow, 1 the wrist), raised this much out of the skin.
const VEIN_SPAN := Vector2(0.15, 0.8)
const VEIN_RADIUS := 0.0016
const VEIN_COLOR := Color(0.28, 0.33, 0.55)
## How close a needle tip has to come to a vein's line to be in it (the tip rests about 1 cm over the skin).
const VEIN_REACH := 0.014
## How much the heart shrinks at full contraction, and the lungs swell full of air.
const HEART_SQUEEZE := 0.12
const LUNG_SWELL := 0.08

var wound_map := WoundMap.new()
var site_id: String
var site_size: Vector2
var site: Node3D
var skin_material: ShaderMaterial
var tissue := TissueSim.new()
## Subcutaneous fat where a site doesn't say ("fat" in patient_sites.json).
const FAT := 0.012
## Subcutaneous fat under this site, thicker on obese patients (set before build()). None on a forearm.
var fat_thickness := FAT
var cavity_blood: MeshInstance3D
var organs: Array[RigidBody3D] = []
## Bones under the site (ribs, breastbone, limb bones), each a StaticBody3D on CAVITY_LAYER with meta "bone".
var bones: Array[StaticBody3D] = []
## Heart contraction 0..1 and lung fill 0..1, set by the animator from the vitals every frame.
var heartbeat := 0.0
var breath := 0.0
var animator := PatientAnimator.new()
var blood := BloodFlow.new()
var orientation: int = Orientation.FACE_UP
var _on_back := false
var _body_root: Node3D
## The surgical drape (operating room only), null without one.
var drape: Drape
var _body_materials: Array[ShaderMaterial] = []
var _organ_rest: Array[Vector3] = []
## Each forearm vein's line, local to its mesh (which rides the forearm bone), for vein_at().
var _veins: Array[MeshInstance3D] = []
## A node riding each forearm bone, from the elbow (its origin) to the wrist (meta "wrist", local), so what's
## stuck to a forearm (veins, an IV catheter's dressing) moves with the arm.
var _forearms: Array[BoneAttachment3D] = []
var _site_base_y := 0.0
var _heights := PackedFloat32Array()
## Per baked grid point: 1 on the body, 0 where the site hangs off it or the body is too thin under it for the layers
## (tools/blender/patient.py bake_site_heights()).
var _on_body := PackedByteArray()
var _layers: Array[MeshInstance3D] = []
var _layer_version := -1
var _layer_steps := -1
var _rebuilt_last := false
## The layers' plans (see _plan_layers()) and the topology they were made for; the particles their vertices need, and
## those plus their neighbours (for normals).
var _plans: Array[LayerPlan] = []
var _plan_for := -1
var _planned := PackedInt32Array()
var _around := PackedInt32Array()
## Scratch space, one entry per particle (per spring end for _xmap), reused between rebuilds: the vertex each particle
## or crossing got in the plan being made (-1: none yet), a mark, the uv of each particle, and where each planned
## particle's skin and layer lie now, which way is out of the skin and its normal.
var _vmap := PackedInt32Array()
var _xmap := PackedInt32Array()
var _mark := PackedByteArray()
var _uv_of := PackedVector2Array()
var _skin_of := PackedVector3Array()
var _point_of := PackedVector3Array()
var _outward := PackedVector3Array()
var _normal := PackedVector3Array()
## Where each grid point's skin lies on the body model (site space), and how far the model's own smooth normal there is
## from the normal the grid's shape gives it. The layers are drawn from these, not from where the sim settled (tension
## pulls the sheet a few millimeters off a round limb): resting skin lies exactly on the model and shades like it, so
## the simulated skin shows no step or seam where it takes over from the model.
var _on_model := PackedVector3Array()
var _normal_fit := PackedVector3Array()
## The body model's skin mesh, whose space the site skin lays out its pores and grime in.
var _skin_model: Node3D
var _organ_last: Array[Vector3] = []
var _jiggle: Array[Vector2] = []
## Organ index -> site-local point a tool is holding it at (host only).
var _held_organs: Dictionary = {}
## Where the simulated skin replaces the body model, one texel per tissue grid point (see TissueSim.region()).
var region_texture: ImageTexture
var _region := PackedByteArray()
## 1 for region points next to one outside it, where the body model takes over: they're drawn right on the model.
var _region_edge := PackedByteArray()
var _region_image: Image
var _cavity_material: ShaderMaterial
var _pool_height := -INF
## Reused by part_at(), which runs every physics frame for every held tool.
var _part_query := PhysicsShapeQueryParameters3D.new()
var _part_sphere := SphereShape3D.new()


func build(site_name: String, tone: Color, age_scale: float) -> void:
	site_id = site_name
	var def := _site_def()
	wound_map = WoundMap.new(WoundMap.size_for(Vector2(def.size[0], def.size[1])))
	_body_root = Node3D.new()
	_body_root.name = "BodyRoot"
	_body_root.position.y = HALF_HEIGHT * age_scale
	_body_root.scale = Vector3.ONE * age_scale
	add_child(_body_root)
	var skin := Materials.body_skin(tone)
	# The gown gets the same carve-capable material, or it would show through the surgical site on the hips.
	var gown := Materials.body_skin(Materials.PATIENT_GOWN)
	_body_materials.append_array([skin, gown])
	for mat in _body_materials:
		Materials.set_site_maps(mat, wound_map.textures[0], wound_map.textures[1])
	var model := ModelSlot.instantiate("patient", "body", _body_root, {"skin": skin, "gown": gown})
	_skin_model = model.find_child("Body", true, false) as Node3D
	add_child(animator)
	animator.setup(self, model)
	_build_colliders()
	_build_surface(model)
	_build_site(tone)
	_fit_to_model(model)
	_build_veins(model)
	blood.name = "BloodFlow"
	add_child(blood)
	blood.setup(self)


func _process(delta: float) -> void:
	wound_map.flush()
	# The meshes catch up with the sim on the frame after it stepped, so a frame that steps the sim (30 times a second)
	# isn't also the frame that rebuilds them: the two costs land on alternate frames.
	var stale := tissue.topology_version != _layer_version or tissue.steps_done != _layer_steps
	var rebuild := stale and not _rebuilt_last
	if rebuild:
		_rebuild_layers()
	_rebuilt_last = rebuild
	tissue.step(delta, not rebuild)
	_jiggle_organs(delta)


## The node that carries the body model, colliders and site. It turns with the patient.
func root() -> Node3D:
	return _body_root


func is_limb_site() -> bool:
	return site_id in LIMB_SITES


## How deep the cavity is under the skin in the middle of the site: at least deep enough for the site's bones to fit
## under the muscle from their middle on, where the cavity can already be shallower (a thick fat layer, a bone off
## to one side like the shoulder's).
func cavity_depth() -> float:
	var depth: float = _site_def().depth
	for bone: Dictionary in _site_def().get("anatomy", {}).get("bones", []):
		var middle := (Vector2(bone.from[0], bone.from[1]) + Vector2(bone.to[0], bone.to[1])) * 0.5
		var bowl := _bowl(middle)
		var needed := muscle_bottom() + 0.003 + float(bone.radius) * 2.0 + 0.004
		depth = maxf(depth, (needed - (1.0 - bowl) * (SKIN_THICKNESS + 0.003)) / bowl)
	return depth


## How far under the skin the muscle layer ends: bones lie right under it, organs further down.
func muscle_bottom() -> float:
	return SKIN_THICKNESS + fat_thickness + MUSCLE_THICKNESS


func site_active() -> bool:
	return orientation == (Orientation.FACE_DOWN if _on_back else Orientation.FACE_UP)


func set_orientation(value: int) -> void:
	orientation = value
	_body_root.rotation.x = [0.0, PI / 2, PI][value]
	_update_carve()
	# Turned away from the site, the drape would lie between the patient and the table.
	if drape:
		drape.visible = site_active()


## Skin flaps folded out of the drape's opening lie on the drape instead of passing through it: the drape's height
## over and around the site (site space, a grid out to a site's size past each edge). Only the skin is held up, the
## layers drawn under it stay under the drape: lifting the flap by their thickness too would tear it off its edge.
## Only skin that starts inside the opening is held up; the site's edge stays under the drape's frame.
func _lay_skin_on_drape() -> void:
	var drape_mesh := TriangleMesh.new()
	var faces := PackedVector3Array()
	for v in drape.mesh.get_faces():
		faces.append(site.transform.affine_inverse() * (drape.transform * v))
	drape_mesh.create_from_faces(faces)
	var cell := DRAPE_FLOOR_CELL
	var columns := ceili(site_size.x * 3.0 / cell) + 1
	var rows := ceili(site_size.y * 3.0 / cell) + 1
	var heights := PackedFloat32Array()
	heights.resize(columns * rows)
	for j in rows:
		for i in columns:
			var at := Vector3(-site_size.x * 1.5 + i * cell, 0.5, -site_size.y * 1.5 + j * cell)
			var hit := drape_mesh.intersect_ray(at, Vector3.DOWN)
			heights[j * columns + i] = (hit.position as Vector3).y + 0.004 if not hit.is_empty() else NAN
	var origin := Vector2(-site_size.x * 1.5, -site_size.y * 1.5)
	tissue.floor_at = func(x: float, z: float) -> float:
		var c := Vector2i(((Vector2(x, z) - origin) / cell).round())
		if c.x < 0 or c.y < 0 or c.x >= columns or c.y >= rows:
			return NAN
		return heights[c.y * columns + c.x]
	# The opening: the site short of the drape's frame, a cell further in to be safe.
	var frame := site_size * Drape.FRAME + Vector2.ONE * cell
	tissue.floor_open = Rect2(-site_size * 0.5 + frame, site_size - frame * 2.0)
	tissue.exposed.resize(tissue.rest.size())
	for k in tissue.rest.size():
		var uv := tissue.uv_of(k)
		# Inside the opening, and not under the drape's edge where it slopes down to the skin.
		var floor_y: float = tissue.floor_at.call(tissue.settled[k].x, tissue.settled[k].z)
		var open := is_nan(floor_y) or floor_y <= tissue.settled[k].y + 0.002
		tissue.exposed[k] = 1 if open and uv.x > Drape.FRAME and uv.x < 1.0 - Drape.FRAME and uv.y > Drape.FRAME and uv.y < 1.0 - Drape.FRAME else 0


## Lays the surgical drape over the patient, open over the site (operating room only; call after build()).
func add_drape() -> void:
	var meshes: Array[MeshInstance3D] = []
	for part_name in ["Body", "Gown"]:
		var mesh := _body_root.find_child(part_name, true, false) as MeshInstance3D
		if mesh:
			meshes.append(mesh)
	drape = Drape.new()
	_body_root.add_child(drape)
	drape.build(_body_root, meshes, site, site_size, -1.0 if _on_back else 1.0)
	drape.visible = site_active()
	_lay_skin_on_drape()


# --- Space conversion ------------------------------------------------------------------------------


func world_to_uv(p: Vector3) -> Vector2:
	var local := site.to_local(p)
	return Vector2(local.x / site_size.x + 0.5, local.z / site_size.y + 0.5)


func uv_to_world(uv: Vector2, depth: float = 0.0) -> Vector3:
	return site.to_global(Vector3((uv.x - 0.5) * site_size.x, surface_height(uv) - depth, (uv.y - 0.5) * site_size.y))


## Meters between the skin and p along the site normal. Negative = under the skin.
func height_above_site(p: Vector3) -> float:
	var local := site.to_local(p)
	return local.y - skin_height(Vector2(local.x / site_size.x + 0.5, local.z / site_size.y + 0.5))


## Skin height at uv as it's drawn now: the simulated skin where it replaces the body (pulled, pressed or cut),
## the body's own rest surface everywhere else and over an opening.
func skin_height(uv: Vector2) -> float:
	var k := tissue.nearest(uv)
	if k < _region.size() and _region[k] == 1:
		var height := tissue.skin_height(uv)
		if not is_nan(height):
			return height
	return surface_height(uv)


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


## False where the site hangs off the body, or the body under it is too thin to hold the site's layers.
func on_body(uv: Vector2) -> bool:
	var grid := int(Db.site_heights.get("grid", 0))
	if _on_body.size() != grid * grid or grid < 2:
		return true
	var cell := Vector2i((uv.clamp(Vector2.ZERO, Vector2.ONE) * (grid - 1)).round())
	return _on_body[cell.y * grid + cell.x] == 1


## Blood on the skin at uv, 0..1, from the fluid map.
func blood_at(uv: Vector2) -> float:
	if uv.x < 0.0 or uv.y < 0.0 or uv.x > 1.0 or uv.y > 1.0:
		return 0.0
	return wound_map.value(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, uv)


func uv_to_meters(uv_length: float) -> float:
	return uv_length * (site_size.x + site_size.y) * 0.5


func meters_to_uv(meters: float) -> float:
	return meters / ((site_size.x + site_size.y) * 0.5)


## What a tool tip at p is touching:
## zone = "air" (above the site), "site" (skin), "cavity" (inside an opening), "body" (other part), "none".
func probe(p: Vector3) -> Dictionary:
	var uv := world_to_uv(p)
	var height := height_above_site(p)
	var on_site := site_active() and uv.x >= 0.0 and uv.x <= 1.0 and uv.y >= 0.0 and uv.y <= 1.0 and on_body(uv)
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


## The ring of a limb's skin around p (world space), for something wrapped around it: {center, axis, radius}, empty
## when p isn't on an arm or a leg. Arms and legs lie along the body; rays cast out from inside the limb find its skin.
func limb_ring(p: Vector3) -> Dictionary:
	# The limb boxes are rough and thinner than the limbs, so look well around p; the skin found decides.
	var part := part_at(p, 0.08)
	if not (part.begins_with("arm") or part.begins_with("leg")):
		return {}
	var box := _body_root.find_child(part.to_pascal_case(), false, false) as Node3D
	var axis := _body_root.global_basis.x.normalized()
	var inside := box.global_position + axis * axis.dot(p - box.global_position)
	var space := get_world_3d().direct_space_state
	var hits := PackedVector3Array()
	for i in 16:
		var out := Basis(axis, TAU * i / 16.0) * _body_root.global_basis.y.normalized()
		var query := PhysicsRayQueryParameters3D.create(inside, inside + out * 0.2, SURFACE_LAYER)
		query.hit_back_faces = true
		query.hit_from_inside = true
		var hit := space.intersect_ray(query)
		if not hit.is_empty():
			hits.append(hit.position)
	if hits.size() < 8:
		return {}
	var center := Vector3.ZERO
	for hit in hits:
		center += hit
	center /= hits.size()
	var radius := 0.0
	for hit in hits:
		radius = maxf(radius, (hit - center).slide(axis).length())
	# Only when p is right at this limb's skin, not somewhere above it.
	if (p - center).slide(axis).length() > radius + 0.03:
		return {}
	return {"center": center, "axis": axis, "radius": radius}


## Cut through every layer and pulled open, so tools reach into the cavity.
func is_open(uv: Vector2) -> bool:
	return tissue.is_open(uv)


## The deepest layer showing at uv: "skin" where it's whole, "fat" or "muscle" where a cut opened down to it,
## "cavity" where it's open through the muscle.
func layer_at(uv: Vector2) -> String:
	if tissue.is_open(uv):
		return "cavity"
	if tissue.is_open(uv, TissueSim.Depth.FAT):
		return "muscle"
	if tissue.is_open(uv, TissueSim.Depth.SKIN):
		# No fat on this part of the body: the muscle lies right under the skin.
		return "fat" if fat_thickness > 0.0005 else "muscle"
	return "skin"


## Where an IV catheter going in at p (world space, just under the skin) sits on the arm: {"node": the forearm it rides,
## "frame": a Transform3D local to it, origin on the skin, X along the arm toward the elbow, Y out of the skin, Z
## across, "radius": the arm's radius there}. The arm counts as round about the forearm bone, clamped to its ends
## (the back of the hand counts as the wrist). Falls back to the body when there are no forearms.
func iv_site(p: Vector3) -> Dictionary:
	var best: Node3D = null
	var center := Vector3.ZERO
	for forearm in _forearms:
		var on_bone := Geometry3D.get_closest_point_to_segment(p, forearm.global_position, forearm.to_global(forearm.get_meta("wrist")))
		if best == null or on_bone.distance_to(p) < center.distance_to(p):
			best = forearm
			center = on_bone
	if best == null:
		var flat := Transform3D(_body_root.global_basis.orthonormalized(), p)
		return {"node": _body_root, "frame": _body_root.global_transform.affine_inverse() * flat, "radius": 0.035}
	var elbow := (best.global_position - best.to_global(best.get_meta("wrist"))).normalized()
	var out := (p - center).slide(elbow)
	var radius := out.length() + 0.002
	var normal := out.normalized()
	var world := Transform3D(Basis(elbow, normal, elbow.cross(normal)), center + normal * radius)
	return {"node": best, "frame": best.global_transform.affine_inverse() * world, "radius": radius}


## True when p (world space) is in or just over a forearm vein.
func vein_at(p: Vector3) -> bool:
	for vein in _veins:
		var local := vein.to_local(p)
		var line: PackedVector3Array = vein.get_meta("line")
		for i in range(1, line.size()):
			if Geometry3D.get_closest_point_to_segment(local, line[i - 1], line[i]).distance_to(local) < VEIN_REACH:
				return true
	return false


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


## A vein on the upper side of each forearm, found on the body mesh at rest and carried by the forearm bone, so it
## moves with the arm. A forearm under the surgical site gets none: the site's own skin lies there.
func _build_veins(model: Node3D) -> void:
	var skin := model.find_child("Body", true, false) as MeshInstance3D
	var skeleton := model.find_children("*", "Skeleton3D", true, false)[0] as Skeleton3D
	if skin == null or skeleton == null:
		return
	var faces := skin.mesh.generate_triangle_mesh()
	var to_skin := skin.global_transform.affine_inverse()
	var up := _body_root.global_basis.y.normalized()
	for side: String in ["L", "R"]:
		var bone := skeleton.find_bone("Forearm" + side)
		var hand := skeleton.find_bone("Hand" + side)
		if bone < 0 or hand < 0:
			continue
		var bone_pose := skeleton.global_transform * skeleton.get_bone_global_pose(bone)
		var wrist := skeleton.global_transform * skeleton.get_bone_global_pose(hand).origin
		var forearm := BoneAttachment3D.new()
		forearm.name = "Forearm" + side
		forearm.bone_name = "Forearm" + side
		skeleton.add_child(forearm)
		forearm.transform = skeleton.get_bone_global_pose(bone)
		forearm.set_meta("wrist", bone_pose.affine_inverse() * wrist)
		_forearms.append(forearm)
		var across := (wrist - bone_pose.origin).cross(up).normalized()
		var line := PackedVector3Array()
		for i in 12:
			var t := lerpf(VEIN_SPAN.x, VEIN_SPAN.y, i / 11.0)
			# A gentle wander across the arm, like a real vein.
			var over := bone_pose.origin.lerp(wrist, t) + across * sin(t * 9.0) * 0.004
			var hit := faces.intersect_ray(to_skin * (over + up * 0.15), (to_skin.basis * -up).normalized())
			if hit.is_empty():
				continue
			var on_skin: Vector3 = skin.global_transform * (hit.position as Vector3)
			var uv := world_to_uv(on_skin)
			if site_active() and Rect2(0, 0, 1, 1).has_point(uv):
				line.clear()
				break
			# Mostly under the skin: only a low ridge of it shows.
			line.append(on_skin - up * VEIN_RADIUS * 0.4)
		if line.size() < 2:
			continue
		var vein := MeshInstance3D.new()
		vein.name = "Vein"
		forearm.add_child(vein)
		var into := bone_pose.affine_inverse()
		for i in line.size():
			line[i] = into * line[i]
		vein.mesh = Shapes.tube(line, VEIN_RADIUS * 1.3, VEIN_RADIUS)
		vein.material_override = Materials.toon(VEIN_COLOR, 0.1, false, 0.4)
		vein.set_meta("line", line)
		_veins.append(vein)


func _build_surface(model: Node3D) -> void:
	for part_name in ["Body", "Gown"]:
		var mesh := model.find_child(part_name, true, false) as MeshInstance3D
		if mesh == null:
			continue
		var surface := StaticBody3D.new()
		surface.name = part_name + "Surface"
		surface.collision_layer = SURFACE_LAYER
		surface.collision_mask = 0
		var shape := CollisionShape3D.new()
		var skin := mesh.mesh.create_trimesh_shape()
		# So rays from inside a limb find its skin too (limb_ring()).
		skin.backface_collision = true
		shape.shape = skin
		surface.add_child(shape)
		_body_root.add_child(surface)
		surface.transform = _body_root.global_transform.affine_inverse() * mesh.global_transform


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
	_on_body.resize(_heights.size())
	_on_body.fill(1)
	for i: int in Db.site_heights.get("off", {}).get(site_id, []):
		_on_body[i] = 0
	tissue.build(site_size, surface_height, on_body)
	_region_image = Image.create(tissue.res_x + 1, tissue.res_y + 1, false, Image.FORMAT_L8)
	region_texture = ImageTexture.create_from_image(_region_image)
	skin_material = Materials.skin_site(tone, wound_map.textures[0], wound_map.textures[1])
	for i in 3:
		var layer := MeshInstance3D.new()
		layer.name = ["Skin", "Fat", "Muscle"][i]
		layer.mesh = ArrayMesh.new()
		# The walls of a cut through the skin are its cut face, in its tone; fat and muscle walls are fat and muscle.
		var flesh := Materials.tissue_layer(i - 1 if i > 0 else 2, wound_map.textures[1], tone)
		layer.set_meta("sheet", skin_material if i == 0 else flesh)
		layer.set_meta("walls", flesh)
		site.add_child(layer)
		_layers.append(layer)

	var collider := Shapes.static_box(site, Vector3(site_size.x, 0.004, site_size.y), Vector3(0, -0.002, 0), SITE_LAYER)
	collider.set_meta("site", true)
	_build_cavity()
	_update_carve.call_deferred()


## Lays every tissue grid point onto the body model (see _on_model): straight down the site's normal onto the skin or
## gown under where the sim settled it, like the baked heights (from 15 cm out), with the model's smooth normal there.
## Points the ray misses keep where they settled.
func _fit_to_model(model: Node3D) -> void:
	var faces := PackedVector3Array()
	var normals := PackedVector3Array()
	for part_name in ["Body", "Gown"]:
		var mesh := model.find_child(part_name, true, false) as MeshInstance3D
		if mesh == null:
			continue
		var to_site := site.global_transform.affine_inverse() * mesh.global_transform
		for s in mesh.mesh.get_surface_count():
			var arrays := mesh.mesh.surface_get_arrays(s)
			var verts: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
			var vertex_normals: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]
			for i: int in arrays[Mesh.ARRAY_INDEX]:
				faces.append(to_site * verts[i])
				normals.append((to_site.basis * vertex_normals[i]).normalized())
	var surface := TriangleMesh.new()
	surface.create_from_faces(faces)
	var count := tissue.rest.size()
	_on_model = tissue.settled.duplicate()
	var model_normals := PackedVector3Array()
	model_normals.resize(count)
	for k in count:
		var p := tissue.settled[k]
		var hit := surface.intersect_ray(Vector3(p.x, 0.15, p.z), Vector3.DOWN)
		if hit.is_empty():
			continue
		var at: Vector3 = hit.position
		var f: int = hit.face_index * 3
		var w := Geometry3D.get_triangle_barycentric_coords(at, faces[f], faces[f + 1], faces[f + 2])
		_on_model[k] = at
		model_normals[k] = (normals[f] * w.x + normals[f + 1] * w.y + normals[f + 2] * w.z).normalized()
	_normal_fit.resize(count)
	for k in count:
		_normal_fit[k] = model_normals[k] - _grid_normal(_on_model, k) if model_normals[k] != Vector3.ZERO else Vector3.ZERO


## The normal of the grid's surface at point k, from where its neighbours lie in `points`.
func _grid_normal(points: PackedVector3Array, k: int) -> Vector3:
	var at := tissue.cell_of(k)
	var dx := points[tissue.index(mini(at.x + 1, tissue.res_x), at.y)] - points[tissue.index(maxi(at.x - 1, 0), at.y)]
	var dz := points[tissue.index(at.x, mini(at.y + 1, tissue.res_y))] - points[tissue.index(at.x, maxi(at.y - 1, 0))]
	return dz.cross(dx).normalized()


## Rebuilds the skin, fat and muscle meshes from the tissue sim, only where the simulated skin replaces the body
## (the region). Deeper layers sit lower and follow the skin less.
## A layer cut through is split exactly where the blade crossed each spring (TissueSim.c_cross), not along the grid:
## each side of the cut keeps its part of the triangle and moves with it, so the lips pull apart along the blade's
## path and the cut opens from the middle and stays closed at its ends, like a zipper. Walls run down each lip
## through the layer's thickness (dermis under the skin, fat, muscle), so the cut has depth.
## Which triangles there are and how they split only changes with the cuts and the region (_plan_layers()); while the
## skin just moves, only the vertices move.
func _rebuild_layers() -> void:
	_layer_version = tissue.topology_version
	_layer_steps = tissue.steps_done
	if _update_region() or _plan_for != tissue.topology_version:
		_plan_layers()
	_place_particles()
	for layer in 3:
		var instance := _layers[layer]
		var mesh := instance.mesh as ArrayMesh
		mesh.clear_surfaces()
		var plan := _plans[layer]
		# No fat on this part of the body: the muscle lies right under the skin.
		instance.visible = not plan.index.is_empty() and (layer != 1 or fat_thickness > 0.0005)
		if not instance.visible:
			continue
		var built := _fill_layer(layer, plan)
		for i in range(0, built.size(), 2):
			var arrays: Array = built[i]
			if (arrays[Mesh.ARRAY_INDEX] as PackedInt32Array).is_empty():
				continue
			mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
			mesh.surface_set_material(mesh.get_surface_count() - 1, instance.get_meta(built[i + 1]) as Material)


## Works out, per layer, the vertices (a particle, or where the blade crossed a spring seen from one end), the
## triangles between them and the walls down the lips of its cuts. Within a triangle, particles still joined by an
## uncut edge stay together; a triangle cut through the layer is drawn as one polygon per side, bounded by where the
## blade crossed its edges.
func _plan_layers() -> void:
	_plan_for = tissue.topology_version
	var hanging := tissue.hanging_off()
	var stride := tissue.res_x + 1
	var count := tissue.rest.size()
	if _vmap.size() != count:
		_uv_of.resize(count)
		for k in count:
			_uv_of[k] = tissue.uv_of(k)
		_vmap.resize(count)
		_vmap.fill(-1)
		_mark.resize(count)
		_point_of.resize(count)
		_skin_of.resize(count)
		_outward.resize(count)
		_normal.resize(count)
	if _xmap.size() < tissue.c_a.size() * 2:
		var grown := _xmap.size()
		_xmap.resize(tissue.c_a.size() * 2 + 64)
		for i in range(grown, _xmap.size()):
			_xmap[i] = -1
	# The triangles to draw: those touching the region, none of whose corners hang off the body. Six entries each:
	# three particles, then the springs of the edges between them in order.
	var marked := PackedByteArray()
	marked.resize(tissue.res_x * tissue.res_y)
	var triangles := PackedInt32Array()
	for k in count:
		if _region[k] == 0:
			continue
		var at := tissue.cell_of(k)
		for j in range(maxi(at.y - 1, 0), mini(at.y, tissue.res_y - 1) + 1):
			for i in range(maxi(at.x - 1, 0), mini(at.x, tissue.res_x - 1) + 1):
				var cell := j * tissue.res_x + i
				if marked[cell] == 1:
					continue
				marked[cell] = 1
				var a := j * stride + i
				var c := a + stride
				if hanging[a] + hanging[a + 1] + hanging[c] == 0 and _region[a] + _region[a + 1] + _region[c] > 0:
					triangles.append_array([a, a + 1, c, tissue.spring_right[a], tissue.spring_diag[a], tissue.spring_down[a]])
				if hanging[a + 1] + hanging[c + 1] + hanging[c] == 0 and _region[a + 1] + _region[c + 1] + _region[c] > 0:
					triangles.append_array([a + 1, c + 1, c, tissue.spring_down[a + 1], tissue.spring_right[c], tissue.spring_diag[a]])
	var cut_depths := PackedByteArray()
	for t in range(0, triangles.size(), 6):
		for n in 3:
			cut_depths.append(tissue.cut_depth(triangles[t + 3 + n]))
	_plans.clear()
	_planned = PackedInt32Array()
	for layer in 3:
		_plans.append(_plan_layer(layer, triangles, cut_depths))
	# Every particle a vertex needs, the lips' neighbours included, and the grid points around them for normals.
	for plan in _plans:
		for list: PackedInt32Array in [plan.owner, plan.other, plan.slide]:
			for k in list:
				if k >= 0 and _mark[k] == 0:
					_mark[k] = 1
					_planned.append(k)
	for k in _planned:
		_mark[k] = 0
	_around = _planned.duplicate()
	for k in _planned:
		_mark[k] = 1
	for k in _planned:
		var at := tissue.cell_of(k)
		for n: Vector2i in [Vector2i(at.x - 1, at.y), Vector2i(at.x + 1, at.y), Vector2i(at.x, at.y - 1), Vector2i(at.x, at.y + 1)]:
			if n.x >= 0 and n.y >= 0 and n.x <= tissue.res_x and n.y <= tissue.res_y:
				var m := tissue.index(n.x, n.y)
				if _mark[m] == 0:
					_mark[m] = 1
					_around.append(m)
	for k in _around:
		_mark[k] = 0


## One layer's plan (see LayerPlan).
func _plan_layer(layer: int, triangles: PackedInt32Array, cut_depths: PackedByteArray) -> LayerPlan:
	var plan := LayerPlan.new()
	var cut_at: int = LAYER_DEPTH[layer]
	var walls := layer != 1 or fat_thickness > 0.0005
	# Skin taken off leaves a hole in the skin layer only: no sheet there, and no wall on the piece's side of the cut.
	var gone := tissue.excised if layer == 0 else PackedByteArray()
	gone.resize(tissue.rest.size())
	var crossed := PackedInt32Array()
	var corners := PackedInt32Array([0, 0, 0])
	var edges := PackedInt32Array([0, 0, 0])
	var cut := PackedByteArray([0, 0, 0])
	var side := PackedInt32Array([0, 1, 2])
	var polygon := PackedInt32Array()
	var crossing := PackedByteArray()
	var touched := PackedInt32Array()
	for t in range(0, triangles.size(), 6):
		var any_cut := false
		for n in 3:
			corners[n] = triangles[t + n]
			edges[n] = triangles[t + 3 + n]
			cut[n] = 1 if cut_depths[t / 2 + n] >= cut_at else 0
			any_cut = any_cut or cut[n] == 1
			side[n] = n
		if any_cut:
			# Which side of the cut each corner is on: corners joined by an uncut edge share one.
			for n in 3:
				if cut[n] == 0:
					var from := side[(n + 1) % 3]
					for m in 3:
						if side[m] == from:
							side[m] = side[n]
		if not any_cut or (side[0] == side[1] and side[1] == side[2]):
			if gone[corners[0]] + gone[corners[1]] + gone[corners[2]] > 0:
				continue
			for n in 3:
				plan.index.append(_plan_own(plan, corners[n], touched))
			continue
		for group in 3:
			if side[0] != group and side[1] != group and side[2] != group:
				continue
			if (side[0] == group and gone[corners[0]] == 1) or (side[1] == group and gone[corners[1]] == 1) or (side[2] == group and gone[corners[2]] == 1):
				continue
			# Around the triangle's edge in its own order, so every polygon faces the way the triangle does.
			polygon.resize(0)
			crossing.resize(0)
			var mine := -1
			for n in 3:
				if side[n] == group:
					mine = _plan_own(plan, corners[n], touched)
					polygon.append(mine)
					crossing.append(0)
				var here := side[n] == group
				if cut[n] == 1 and here != (side[(n + 1) % 3] == group):
					var k := corners[n] if here else corners[(n + 1) % 3]
					polygon.append(_plan_cross(plan, edges[n], k, crossed))
					crossing.append(1)
			for n in range(1, polygon.size() - 1):
				plan.index.append_array([polygon[0], polygon[n], polygon[n + 1]])
			if not walls:
				continue
			# Each stretch of the polygon's edge between two crossings is a lip of the cut: a wall goes down from it.
			for n in polygon.size():
				var next := (n + 1) % polygon.size()
				if crossing[n] == 1 and crossing[next] == 1:
					plan.wall.append_array([polygon[n], polygon[next], mine])
	for k in touched:
		_vmap[k] = -1
	for key in crossed:
		_xmap[key] = -1
	return plan


## The vertex of particle k in the plan, added the first time it's used.
func _plan_own(plan: LayerPlan, k: int, touched: PackedInt32Array) -> int:
	if _vmap[k] < 0:
		_vmap[k] = plan.owner.size()
		touched.append(k)
		plan.owner.append(k)
		plan.other.append(-1)
		plan.share.append(0.0)
		plan.slide.append(-1)
		plan.uv.append(_uv_of[k])
	return _vmap[k]


## The vertex where the blade crossed spring s, on the side of its end k, added the first time it's used. It lies as
## far from k as it did at rest, so each lip moves with its own side. A diagonal spring's far end lies a cell along the
## cut from k: there the lip moves like k's neighbour that way (if they're still joined), so the lip doesn't step from
## cell to cell.
func _plan_cross(plan: LayerPlan, s: int, k: int, crossed: PackedInt32Array) -> int:
	var at_start := tissue.c_a[s] == k
	var key := s * 2 + (0 if at_start else 1)
	if _xmap[key] < 0:
		_xmap[key] = plan.owner.size()
		crossed.append(key)
		var other := tissue.c_b[s] if at_start else tissue.c_a[s]
		var slide := -1
		var cell := tissue.cell_of(k)
		var step := tissue.cell_of(other) - cell
		var dir := tissue.c_cut_dir[s]
		step = Vector2i(step.x, 0) if absf(dir.x) >= absf(dir.y) else Vector2i(0, step.y)
		if step != Vector2i.ZERO:
			var m := tissue.index(cell.x + step.x, cell.y + step.y)
			var low := mini(k, m)
			var joined := tissue.spring_right[low] if step.x != 0 else tissue.spring_down[low]
			if joined >= 0 and tissue.c_active[joined] == 1:
				slide = m
		plan.owner.append(k)
		plan.other.append(other)
		plan.share.append(tissue.c_cross[s] if at_start else 1.0 - tissue.c_cross[s])
		plan.slide.append(slide)
		plan.uv.append(_uv_of[tissue.c_a[s]].lerp(_uv_of[tissue.c_b[s]], tissue.c_cross[s]))
	return _xmap[key]


## Where the skin of every planned particle is now, its normal, and which way is out of it: straight up where the skin
## is in place, along its own normal on a flap pulled far, so a flap folded over shows its fat on top instead of
## drawing it under the skin, through the drape.
func _place_particles() -> void:
	for k in _around:
		_skin_of[k] = layer_point(0, k)
	for k in _planned:
		var moved := clampf(_moved(k).length() / FLAP_MOVE, 0.0, 1.0)
		# The model's own normal where the skin rests, turning with the skin as it moves (a flap keeps its own).
		var normal := (_grid_normal(_skin_of, k) + _normal_fit[k] * (1.0 - moved)).normalized()
		_normal[k] = normal
		_outward[k] = Vector3.UP.lerp(normal, moved).normalized()


## One layer's sheet and the walls of its cuts, where the skin is now: [sheet arrays, "sheet", wall arrays, "walls"].
func _fill_layer(layer: int, plan: LayerPlan) -> Array:
	var depth: float = [0.0, SKIN_THICKNESS, SKIN_THICKNESS + fat_thickness][layer]
	var thickness: float = [SKIN_THICKNESS, fat_thickness, MUSCLE_THICKNESS][layer]
	for k in _planned:
		_point_of[k] = layer_point(layer, k) - _outward[k] * depth
	var owner := plan.owner
	var other := plan.other
	var share := plan.share
	var slide := plan.slide
	var verts := PackedVector3Array()
	var norms := PackedVector3Array()
	verts.resize(owner.size())
	norms.resize(owner.size())
	for v in owner.size():
		var k := owner[v]
		var p := _point_of[k]
		if other[v] >= 0:
			var offset := _on_model[other[v]] - _on_model[k]
			if slide[v] >= 0:
				var m := slide[v]
				offset += (_point_of[m] - _on_model[m]) - (p - _on_model[k])
			p += offset * share[v]
		verts[v] = p
		norms[v] = _normal[k]
	var wall := plan.wall
	var uvs := plan.uv
	var wall_verts := PackedVector3Array()
	var wall_norms := PackedVector3Array()
	var wall_uvs := PackedVector2Array()
	var wall_indices := PackedInt32Array()
	for w in range(0, wall.size(), 3):
		var a := wall[w]
		var b := wall[w + 1]
		var top_a := verts[a]
		var top_b := verts[b]
		var bottom_a := top_a - _outward[owner[a]] * thickness
		var bottom_b := top_b - _outward[owner[b]] * thickness
		var normal := (top_b - top_a).cross(bottom_a - top_a).normalized()
		# Facing into the cut, away from this side's own skin.
		if normal.dot(top_a - verts[wall[w + 2]]) < 0.0:
			normal = -normal
		var base := wall_verts.size()
		wall_verts.append_array([top_a, top_b, bottom_b, bottom_a])
		wall_norms.append_array([normal, normal, normal, normal])
		wall_uvs.append_array([uvs[a], uvs[b], uvs[b], uvs[a]])
		# Godot's front faces wind clockwise seen from the side the normal points to.
		if (bottom_b - top_a).cross(top_b - top_a).dot(normal) > 0.0:
			wall_indices.append_array([base, base + 1, base + 2, base, base + 2, base + 3])
		else:
			wall_indices.append_array([base, base + 2, base + 1, base, base + 3, base + 2])
	return [_arrays(verts, norms, uvs, plan.index), "sheet", _arrays(wall_verts, wall_norms, wall_uvs, wall_indices), "walls"]


static func _arrays(verts: PackedVector3Array, normals: PackedVector3Array, uvs: PackedVector2Array, indices: PackedInt32Array) -> Array:
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices
	return arrays


## Where a layer's grid point k is now, before it's moved down to its depth: on the body model where the skin rests,
## moved as far as the sim moved it (_moved()). Deeper layers are tethered and follow the skin only partly (a stepped
## wound edge), but a flap pulled far back takes all of its layers along.
func layer_point(layer: int, k: int) -> Vector3:
	var moved := _moved(k)
	return _on_model[k] + moved * lerpf(LAYER_FOLLOW[layer], 1.0, clampf(moved.length() / FLAP_MOVE, 0.0, 1.0))


## How far the sim moved grid point k since it settled, less any way back toward the body model: tension holds the
## sheet off a curved body, and where a cut lets go of it the skin springs back, to where it's drawn already.
## None on the region's edge where it moved less than the region takes in (TissueSim.REGION_MOVE): that still shows
## a step against the body model next to it.
func _moved(k: int) -> Vector3:
	var moved := tissue.pos[k] - tissue.settled[k]
	if k < _region_edge.size() and _region_edge[k] == 1 and moved.length() <= TissueSim.REGION_MOVE:
		return Vector3.ZERO
	var fit := _on_model[k] - tissue.settled[k]
	if fit.is_zero_approx():
		return moved
	var back := fit.normalized()
	return moved - back * clampf(moved.dot(back), 0.0, fit.length())


## True when the region changed.
func _update_region() -> bool:
	var region := tissue.region()
	if region == _region:
		return false
	_region = region
	_region_edge.resize(region.size())
	_region_edge.fill(0)
	for k in region.size():
		if region[k] == 1:
			var at := tissue.cell_of(k)
			for step: Vector2i in [Vector2i.LEFT, Vector2i.RIGHT, Vector2i.UP, Vector2i.DOWN]:
				var n := (at + step).clamp(Vector2i.ZERO, Vector2i(tissue.res_x, tissue.res_y))
				if region[tissue.index(n.x, n.y)] == 0:
					_region_edge[k] = 1
	# One byte per particle, row by row: the image's own layout.
	var texels := region.duplicate()
	for k in texels.size():
		texels[k] *= 255
	_region_image.set_data(tissue.res_x + 1, tissue.res_y + 1, false, Image.FORMAT_L8, texels)
	region_texture.update(_region_image)
	return true


## Cavity grid points per side.
const CAVITY_STEPS := 24


## Height of the cavity floor at uv: a bowl that is deepest (cavity_depth under the skin) in the middle and rises to
## just under the skin at the site's edges. It follows the skin, so on a round limb it never pokes out of the sides
## and is as deep on a sloping shoulder as on a flat belly.
func _cavity_floor(uv: Vector2) -> float:
	var under_skin := surface_height(uv) - SKIN_THICKNESS - 0.003
	return minf(lerpf(under_skin, surface_height(uv) - cavity_depth(), _bowl(uv)), under_skin)


## How much of the cavity's depth it has at uv: all of it in the middle, rising to nothing at the site's edges.
static func _bowl(uv: Vector2) -> float:
	var edge := Vector2(absf(uv.x * 2.0 - 1.0), absf(uv.y * 2.0 - 1.0))
	return (1.0 - pow(edge.x, 4.0)) * (1.0 - pow(edge.y, 4.0))


func _cavity_uv(i: int, j: int) -> Vector2:
	return Vector2(i, j) / CAVITY_STEPS


## Grid triangles over the site, for quads whose four corners pass keep(i, j).
func _cavity_grid(height: Callable, keep: Callable) -> ArrayMesh:
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	for j in CAVITY_STEPS:
		for i in CAVITY_STEPS:
			if not (keep.call(i, j) and keep.call(i + 1, j) and keep.call(i + 1, j + 1) and keep.call(i, j + 1)):
				continue
			for c: Vector2i in [Vector2i(i, j), Vector2i(i + 1, j), Vector2i(i + 1, j + 1), Vector2i(i, j), Vector2i(i + 1, j + 1), Vector2i(i, j + 1)]:
				var uv := _cavity_uv(c.x, c.y)
				st.add_vertex(_site_point(uv, height.call(uv)))
	st.generate_normals()
	return st.commit()


func _site_point(uv: Vector2, height: float) -> Vector3:
	return Vector3((uv.x - 0.5) * site_size.x, height, (uv.y - 0.5) * site_size.y)


func _site_def() -> Dictionary:
	return Db.patient_sites.get(site_id, Db.patient_sites.get("abdomen", {}))


func _build_cavity() -> void:
	var cavity := MeshInstance3D.new()
	cavity.name = "Cavity"
	# Only under the body: past its edge the bowl would hang in the air.
	cavity.mesh = _cavity_grid(_cavity_floor, func(i: int, j: int) -> bool: return on_body(_cavity_uv(i, j)))
	_cavity_material = Materials.flesh()
	cavity.material_override = _cavity_material
	site.add_child(cavity)
	# What a tool reaching into an opening comes down on when nothing else is in the way: the bowl itself.
	var floor_body := StaticBody3D.new()
	floor_body.name = "CavityFloor"
	floor_body.collision_layer = CAVITY_LAYER
	floor_body.collision_mask = 0
	var floor_shape := CollisionShape3D.new()
	floor_shape.shape = cavity.mesh.create_trimesh_shape()
	floor_body.add_child(floor_shape)
	site.add_child(floor_body)

	cavity_blood = MeshInstance3D.new()
	cavity_blood.name = "CavityBlood"
	cavity_blood.material_override = Materials.blood_pool()
	cavity_blood.visible = false
	site.add_child(cavity_blood)


## Adds a pushable organ. Only the host simulates them, clients get transforms from Patient.
## spec (optional): model, yaw (degrees), mirror, motion ("beat", "breath"), layer (0 on top). Without a model it's a
## round blob with a sphere collider; an anatomical organ collides as the box around its model.
func add_organ(uv: Vector2, depth: float, radius: float, color: Color, spec: Dictionary = {}) -> RigidBody3D:
	var organ := RigidBody3D.new()
	organ.name = "Organ%d" % organs.size()
	organ.collision_layer = CAVITY_LAYER
	# Organs lie on top of each other without pushing each other around; hands push them aside.
	organ.collision_mask = PUSHER_LAYER
	organ.gravity_scale = 0.0
	organ.linear_damp = 6.0
	organ.angular_damp = 6.0
	organ.lock_rotation = true
	organ.mass = 0.3
	organ.freeze = not multiplayer.is_server()
	organ.rotation.y = deg_to_rad(float(spec.get("yaw", 0.0)))
	var choices: Array = SITE_ORGANS.get(site_id, SITE_ORGANS.abdomen)
	var model_name: String = spec.get("model", choices[organs.size() % choices.size()])
	var model := ModelSlot.instantiate("organs", model_name, organ, {"organ": Materials.flesh(color)})
	model.name = "Model"
	var base := Vector3(1, 1, -1 if spec.get("mirror", false) else 1) * radius
	model.scale = base
	organ.set_meta("scale", base)
	organ.set_meta("motion", spec.get("motion", ""))
	organ.set_meta("layer", int(spec.get("layer", 0)))
	organ.set_meta("kind", model_name)
	var shape := CollisionShape3D.new()
	var height := surface_height(uv) - depth
	if spec.has("model"):
		var bounds := _local_bounds(model, organ)
		var box := BoxShape3D.new()
		box.size = bounds.size
		shape.shape = box
		shape.position = bounds.get_center()
		# spec.top: how far under the muscle the organ's top lies, so it stays under the muscle and ribs on any patient.
		# Measured from the lowest skin over it: the body curves, the box's top is flat.
		var lowest := INF
		for corner: Vector3 in [bounds.position, bounds.position + Vector3(bounds.size.x, 0, 0), bounds.position + Vector3(0, 0, bounds.size.z), bounds.end]:
			var at := Basis(Vector3.UP, organ.rotation.y) * corner
			lowest = minf(lowest, surface_height(uv + Vector2(at.x / site_size.x, at.z / site_size.y)))
		height = minf(lowest, surface_height(uv)) - muscle_bottom() - float(spec.get("top", 0.0)) - bounds.end.y
	else:
		var sphere := SphereShape3D.new()
		sphere.radius = radius
		shape.shape = sphere
	organ.add_child(shape)
	site.add_child(organ)
	organ.position = Vector3((uv.x - 0.5) * site_size.x, height, (uv.y - 0.5) * site_size.y)
	organs.append(organ)
	_organ_rest.append(organ.position)
	_organ_last.append(organ.position)
	_jiggle.append(Vector2.ZERO)
	return organ


## The box around every mesh under node, in the space of `space` (an ancestor). Works before they're in the tree.
static func _local_bounds(node: Node3D, space: Node3D) -> AABB:
	var bounds := AABB()
	var first := true
	for child in node.find_children("*", "MeshInstance3D", true, false):
		var mesh := child as MeshInstance3D
		var xform := Transform3D.IDENTITY
		var at: Node = mesh
		while at != space and at is Node3D:
			xform = (at as Node3D).transform * xform
			at = at.get_parent()
		var box := xform * mesh.get_aabb()
		bounds = box if first else bounds.merge(box)
		first = false
	return bounds


## Builds the site's anatomy (patient_sites.json "anatomy"): organs in layers, the rib cage, limb bones.
## avoid: uv of targets that must stay in view, top layer organs over one move aside. skip_bones: a scenario
## target takes the bones' place (a femur to saw, a sternum to open), so the anatomical ones are left out.
func build_anatomy(avoid: Array[Vector2], skip_bones: PackedStringArray) -> void:
	var anatomy: Dictionary = _site_def().get("anatomy", {})
	for spec: Dictionary in anatomy.get("organs", []):
		var uv := Vector2(spec.uv[0], spec.uv[1])
		var raw: Array = spec.get("color", [0.6, 0.3, 0.3])
		add_organ(uv, 0.0, spec.size, Color(raw[0], raw[1], raw[2]), spec)
		if int(spec.get("layer", 0)) == 0:
			_clear_view(organs.size() - 1, avoid)
	if not "bone" in skip_bones:
		for spec: Dictionary in anatomy.get("bones", []):
			_add_bone("Bone", [Vector2(spec.from[0], spec.from[1]), Vector2(spec.to[0], spec.to[1])], spec.radius, 1.0)
	if anatomy.has("sternum") and not "sternum" in skip_bones:
		var spec: Dictionary = anatomy.sternum
		_add_bone("Sternum", [Vector2(spec.from[0], spec.from[1]), Vector2(spec.to[0], spec.to[1])], spec.radius, 0.4)
	if anatomy.has("ribs"):
		var ribs: Dictionary = anatomy.ribs
		for row: float in ribs.rows:
			for side: float in [-1.0, 1.0]:
				# From beside the breastbone out to the side of the site, dropping toward the feet as it goes.
				var points: Array[Vector2] = []
				for i in 7:
					var t := i / 6.0
					var y := 0.5 + side * lerpf(float(ribs.inner), 0.5, t)
					points.append(Vector2(row - float(ribs.drop) * t * t, y))
				if "rib" in skip_bones and points.any(func(p: Vector2) -> bool: return avoid.any(func(a: Vector2) -> bool: return a.distance_to(p) < 0.08)):
					continue
				_add_bone("Rib", points, ribs.radius, 0.55)


## Moves a top layer organ off a target that has to stay in view, just far enough that its box clears it.
func _clear_view(index: int, avoid: Array[Vector2]) -> void:
	var organ := organs[index]
	var box := ((organ.get_child(organ.get_child_count() - 1) as CollisionShape3D).shape as BoxShape3D).size
	var reach := maxf(box.x / site_size.x, box.z / site_size.y) * 0.5
	for target in avoid:
		var uv := Vector2(organ.position.x / site_size.x + 0.5, organ.position.z / site_size.y + 0.5)
		var away := uv - target
		if away.length() >= reach:
			continue
		uv = (target + (away.normalized() if away.length() > 0.001 else Vector2.RIGHT) * reach).clamp(Vector2.ONE * 0.1, Vector2.ONE * 0.9)
		organ.position.x = (uv.x - 0.5) * site_size.x
		organ.position.z = (uv.y - 0.5) * site_size.y
		_organ_rest[index] = organ.position
		_organ_last[index] = organ.position


## A bone along a polyline in uv, lying right under the muscle: a tube, flattened to `flat` of its width for ribs
## and the breastbone, with capsules along it for tools to rest on.
func _add_bone(kind: String, points: Array[Vector2], radius: float, flat: float) -> void:
	var top := muscle_bottom() + 0.003
	var path := PackedVector3Array()
	# Short steps, so a straight bone still follows the curve of the skin over it.
	var dense: Array[Vector2] = [points[0]]
	for i in range(1, points.size()):
		var steps := maxi(1, ceili(points[i - 1].distance_to(points[i]) / 0.08))
		for s in steps:
			dense.append(points[i - 1].lerp(points[i], float(s + 1) / steps))
	for uv in dense:
		# Toward the site's edges the cavity rises to the skin; a bone that no longer fits under the muscle there ends.
		var height := surface_height(uv) - top - radius * flat
		if height - radius * flat < _cavity_floor(uv) + 0.001:
			continue
		path.append(_site_point(uv, height))
	if path.size() < 2:
		return
	var bone := StaticBody3D.new()
	bone.name = "%s%d" % [kind, bones.size()]
	bone.collision_layer = CAVITY_LAYER
	bone.collision_mask = 0
	bone.set_meta("bone", kind.to_lower())
	var mesh := MeshInstance3D.new()
	mesh.name = "Mesh"
	mesh.mesh = Shapes.tube(path, radius, radius * flat)
	mesh.material_override = Materials.toon(BONE_COLOR, 0.2)
	bone.add_child(mesh)
	for i in range(1, path.size()):
		var a := path[i - 1]
		var b := path[i]
		var shape := CollisionShape3D.new()
		var capsule := CapsuleShape3D.new()
		capsule.radius = radius * flat
		capsule.height = a.distance_to(b) + capsule.radius * 2.0
		shape.shape = capsule
		# A capsule runs along its Y axis.
		var along := (b - a).normalized()
		var side := along.cross(Vector3.UP if absf(along.y) < 0.9 else Vector3.RIGHT).normalized()
		shape.transform = Transform3D(Basis(side, along, side.cross(along)), (a + b) * 0.5)
		bone.add_child(shape)
	site.add_child(bone)
	bones.append(bone)


## The bone within `radius` of p (world space), "" when there's none: "rib", "sternum" or "bone".
func bone_at(p: Vector3, radius: float = 0.015) -> String:
	_part_sphere.radius = radius
	_part_query.shape = _part_sphere
	_part_query.transform = Transform3D(Basis.IDENTITY, p)
	_part_query.collision_mask = CAVITY_LAYER
	for hit in get_world_3d().direct_space_state.intersect_shape(_part_query, 32):
		var collider: Object = hit.collider
		if collider.has_meta("bone"):
			return collider.get_meta("bone")
	return ""


## The organ at p (world space), or -1.
func organ_at(p: Vector3, radius: float = 0.012) -> int:
	_part_sphere.radius = radius
	_part_query.shape = _part_sphere
	_part_query.transform = Transform3D(Basis.IDENTITY, p)
	_part_query.collision_mask = CAVITY_LAYER
	var best := -1
	for hit in get_world_3d().direct_space_state.intersect_shape(_part_query, 32):
		var index := organs.find(hit.collider as RigidBody3D)
		# Of two organs lying on top of each other, the one on top is what the tool meets.
		if index >= 0 and (best < 0 or organs[index].position.y > organs[best].position.y):
			best = index
	return best


## Host: a tool holding an organ drags it to a site-local point; released, it drifts back where it belongs.
func hold_organ(index: int, at: Vector3) -> void:
	_held_organs[index] = at


func release_organ(index: int) -> void:
	_held_organs.erase(index)


## Host: organs drift back to where they belong once you stop pushing them. Held ones go where the tool takes them.
func settle_organs() -> void:
	for i in organs.size():
		var organ := organs[i]
		if _held_organs.has(i):
			organ.position = _held_organs[i]
			organ.linear_velocity = Vector3.ZERO
			continue
		organ.apply_central_force((_organ_rest[i] - organ.position) * 40.0 * organ.mass)


## Soft organs: a damped spring squashes and stretches each organ when it's pushed, on every peer.
## The heart beats and the lungs fill with the vitals.
func _jiggle_organs(delta: float) -> void:
	if delta <= 0.0:
		return
	# The wobble spring is stiff: stepped over a long frame (a hitch, a slow renderer) it would blow up to NaN.
	var step := minf(delta, 1.0 / 30.0)
	for i in organs.size():
		var organ := organs[i]
		var velocity := (organ.position - _organ_last[i]) / delta
		_organ_last[i] = organ.position
		var state := _jiggle[i]
		state.y += (-state.x * 180.0 - state.y * 9.0 + clampf(velocity.length() * 6.0, 0.0, 3.0)) * step
		state.x += state.y * step
		_jiggle[i] = state
		var squash := clampf(state.x, -0.25, 0.25)
		var model := organ.get_node_or_null("Model") as Node3D
		if model:
			model.scale = Vector3(1.0 + squash, 1.0 - squash, 1.0 + squash) * (organ.get_meta("scale") as Vector3) * organ_motion(i)


## How much bigger than at rest the organ is drawn right now: the heart shrinks as it contracts, lungs swell.
func organ_motion(index: int) -> float:
	match organs[index].get_meta("motion", ""):
		"beat":
			return 1.0 - HEART_SQUEEZE * heartbeat
		"breath":
			return 1.0 + LUNG_SWELL * breath
	return 1.0


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
	# The drape lies on the trunk, so it rises with it.
	if drape:
		drape.position.y = offset


## Blood filling the cavity bowl, level 0..1. The surface only covers the part of the bowl that is under it and
## still under the skin, so it never shows outside the body. Rebuilt only when the level moves a millimeter or so.
func set_cavity_blood(level: float) -> void:
	var height := _cavity_floor(Vector2(0.5, 0.5)) + 0.002 + clampf(level, 0.0, 1.0) * cavity_depth() * 0.85
	cavity_blood.visible = level > 0.01
	if not cavity_blood.visible or absf(height - _pool_height) < 0.0015:
		return
	_pool_height = height
	var keep := func(i: int, j: int) -> bool:
		var uv := _cavity_uv(i, j)
		return on_body(uv) and _cavity_floor(uv) < height and height < surface_height(uv) - SKIN_THICKNESS
	cavity_blood.mesh = _cavity_grid(func(_uv: Vector2) -> float: return height, keep)


## Where the body model is cut away (the region), the simulated skin layers take over.
func _update_carve() -> void:
	for mat in _body_materials:
		Materials.set_carve(mat, site.global_transform, site_size * 0.5, cavity_depth() + 0.02, region_texture)
	skin_material.set_shader_parameter("site_to_model", Projection(_skin_model.global_transform.affine_inverse() * site.global_transform))
	Materials.set_reveal(_cavity_material, site.global_transform, site_size * 0.5, region_texture)


## Blood loss drains the color from the skin, body and site alike.
func set_pallor(value: float) -> void:
	skin_material.set_shader_parameter("pallor", value)
	_body_materials[0].set_shader_parameter("pallor", value)
