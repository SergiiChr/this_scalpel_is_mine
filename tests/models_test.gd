extends Node
## Model contract checks: every model the game loads exists, and the rigged ones have the bones and parts the
## animation code drives. Prints "FAIL: ..." for each problem; run_tests.sh fails on those.
## Run: godot --headless --path . res://tests/models_test.tscn

const PATIENT_BONES: PackedStringArray = [
	"Torso", "Chest", "Neck", "Head", "Jaw",
	"UpperArmL", "ForearmL", "HandL", "UpperArmR", "ForearmR", "HandR",
	"ThighL", "ShinL", "FootL", "ThighR", "ShinR", "FootR",
]
const PATIENT_PARTS: PackedStringArray = ["EyeL", "EyeR", "Lids"]
const ORGANS: PackedStringArray = ["bowel", "lobe", "sac"]
const GripCheck := preload("res://tests/grip_check.gd")
## Most triangles any one model of a category may have. The generators aim under these
## (tools/blender/__main__.py BUDGETS, tools/blender/patient.py BUDGETS).
const BUDGETS: Dictionary = {
	"patient": 52000, "surgeon": 16000, "organs": 7000, "targets": 5000, "tools": 6000, "props": 15000,
}


func _ready() -> void:
	var holder := Node3D.new()
	add_child(holder)
	_rig("patient", "body", PATIENT_BONES, PATIENT_PARTS, holder)
	var glove_bones: PackedStringArray = ["Hand"]
	for finger in SurgeonHand.FINGERS:
		for joint in 3:
			glove_bones.append("%s%d" % [finger, joint + 1])
	_rig("surgeon", "glove", glove_bones, [], holder)
	for organ in ORGANS:
		_exists("organs", organ)
	# Every organ any site's anatomy puts in the body needs a model.
	for site: Variant in Db.patient_sites.values():
		if site is Dictionary:
			for organ: Dictionary in (site as Dictionary).get("anatomy", {}).get("organs", []):
				_exists("organs", organ.model)
	# Every target kind any scenario uses needs a model.
	var kinds: Dictionary = {}
	for scenario: ScenarioDef in Db.scenarios:
		for target: Dictionary in scenario.targets:
			kinds[target.get("kind", "bullet")] = true
	for kind: String in kinds:
		_exists("targets", kind)
	_check_budgets(holder)
	for hand_index in 2:
		await _grip_clearance(holder, hand_index)
		_hand_turn(holder, hand_index)
	_arm_limits(holder)
	await _cuff_fit(holder)
	print("models_test: done")
	get_tree().quit()


## Wherever the hand works (in front, out to the side, low, near), a held tool keeps the hand turned in:
## the back of the hand up, or for a fist round a handle facing out to the hand's own side, never palm up.
func _hand_turn(holder: Node3D, hand_index: int) -> void:
	var hand := GripCheck.make_hand(holder, hand_index)
	var side := -1.0 if hand_index == 0 else 1.0
	var outward := Vector3(side, 0, 0)
	for grip: String in SurgeonHand.GRIPS:
		var def: ToolDef = Db.tools.values().filter(func(d: ToolDef) -> bool: return d.grip == grip).front()
		hand.holding = true
		hand.grip = grip
		hand.fit = Db.grip_fit(def, hand_index)
		for at: Vector3 in [Vector3(0.17, 1.05, -0.42), Vector3(0.4, 0.95, -0.3), Vector3(0.05, 0.9, -0.5), Vector3(0.25, 1.2, -0.3)]:
			hand.target = Vector3(at.x * side, at.y, at.z)
			hand.snap_pose(GripCheck.shoulder(hand))
			var back := hand._glove.global_basis.y.normalized()
			var facing := back.dot(outward) if grip == "fist" else back.dot(Vector3.UP)
			if facing < 0.3:
				print("FAIL: the %s hand holding a %s at %s is twisted (back of the hand %s)" % ["left" if hand_index == 0 else "right", def.id, hand.target, back])
	hand.get_parent().queue_free()


## At the arm's limits (stretched out, folded up to the shoulder, reaching straight along the elbow's bend) both hands
## stay finite, and the left one is the right one mirrored.
func _arm_limits(holder: Node3D) -> void:
	var hands: Array[SurgeonHand] = [GripCheck.make_hand(holder, 0), GripCheck.make_hand(holder, 1)]
	var reach := SurgeonHand.UPPER_ARM + SurgeonHand.FOREARM
	var bend := Vector3(0.6, -1.0, 0.0).normalized()
	var limits: Dictionary = {
		"stretched": Vector3(0.3, -0.25, -1.0).normalized() * reach * 1.2, "folded": Vector3(-0.02, -0.03, -0.04),
		"along the bend": bend * reach * 0.6,
	}
	for grip: String in [""] + SurgeonHand.GRIPS.keys():
		for limit: String in limits:
			var gloves: Array[Transform3D] = []
			for hand in hands:
				var side := -1.0 if hand.index == 0 else 1.0
				var shoulder := GripCheck.shoulder(hand)
				var offset: Vector3 = limits[limit]
				hand.holding = grip != ""
				hand.grip = grip if grip else "pencil"
				hand.fit = {}
				hand.target = shoulder + Vector3(offset.x * side, offset.y, offset.z)
				hand.snap_pose(shoulder)
				var parts: Array[Transform3D] = [hand._glove.global_transform, hand._fore.global_transform, hand._upper.global_transform]
				if not parts.all(func(t: Transform3D) -> bool: return t.is_finite()):
					print("FAIL: the %s hand %s, arm %s, isn't finite" % ["left" if hand.index == 0 else "right", "holding by " + grip if grip else "empty", limit])
				gloves.append(hand._glove.global_transform)
			# Mirrored across the body's middle, the left glove lands exactly on the right one.
			var left := Transform3D(Basis.from_scale(Vector3(-1, 1, 1)), Vector3.ZERO) * gloves[0]
			if left.origin.distance_to(gloves[1].origin) > 0.002 or not left.basis.is_equal_approx(gloves[1].basis):
				print("FAIL: the hands %s, arm %s, don't mirror each other (left %s, right %s)" % ["holding by " + grip if grip else "empty", limit, gloves[0], gloves[1]])
	for hand in hands:
		hand.get_parent().queue_free()


## The glove's cuff follows the forearm and wraps the sleeve: the end of the sleeve that reaches into the cuff stays
## inside the glove, and the cuff hugs the sleeve instead of standing off it. Both hands, empty and in every grip,
## wherever the hand works and at the arm's limits.
func _cuff_fit(holder: Node3D) -> void:
	for hand_index in 2:
		var hand := GripCheck.make_hand(holder, hand_index)
		var side := -1.0 if hand_index == 0 else 1.0
		var shoulder := GripCheck.shoulder(hand)
		for grip: String in [""] + SurgeonHand.GRIPS.keys():
			var def: ToolDef = Db.tools.values().filter(func(d: ToolDef) -> bool: return d.grip == grip).front() if grip else null
			hand.holding = grip != ""
			hand.grip = grip if grip else "pencil"
			hand.fit = Db.grip_fit(def, hand_index) if def else {}
			for at: Vector3 in CUFF_SPOTS:
				hand.target = shoulder + Vector3(at.x * side, at.y, at.z)
				hand.snap_pose(shoulder)
				var what := "the %s hand %s at %s" % ["left" if hand_index == 0 else "right", "holding by " + grip if grip else "empty", at]
				_check_cuff(hand, what)
		hand.get_parent().queue_free()


## Hand positions from the shoulder for _cuff_fit(), right hand (the left one mirrors x): working spots in front,
## out to the side and low, the arm stretched out past its reach and folded up to the shoulder.
const CUFF_SPOTS: Array[Vector3] = [
	Vector3(-0.02, -0.35, -0.34), Vector3(0.2, -0.45, -0.22), Vector3(-0.14, -0.5, -0.42), Vector3(0.06, -0.2, -0.22),
	Vector3(0.2, -0.2, -0.8), Vector3(-0.02, -0.03, -0.04),
]
## Where the glove's cuff starts, behind the wrist (meters, glove model space along -X).
const CUFF_FROM := 0.035
## How far the cuff may stand off the sleeve before it reads as sticking out (meters).
const CUFF_STANDOFF := 0.008
## The end of the sleeve this far into the cuff must be inside the glove (meters from the sleeve's end).
const CUFF_INSIDE := 0.03


func _check_cuff(hand: SurgeonHand, what: String) -> void:
	# The forearm model runs along its Y from -0.5 (elbow) to 0.5 (the end in the cuff).
	var end := hand._fore.global_transform * Vector3(0, 0.5, 0)
	var back := (hand._fore.global_transform * Vector3(0, -0.5, 0) - end).normalized()
	var sleeve := PackedVector3Array()
	for node in hand._fore.find_children("*", "MeshInstance3D", true, false):
		if (node as MeshInstance3D).visible:
			sleeve.append_array(_posed_faces(node as MeshInstance3D))
	# Only the glove's cuff: the hand itself can bend back beside the sleeve without being part of it.
	var glove := PackedVector3Array()
	for node in hand._glove.find_children("*", "MeshInstance3D", true, false):
		glove.append_array(_posed_faces(node as MeshInstance3D, -CUFF_FROM))
	# Under the glove means a ray from the forearm's axis out through a point of the sleeve meets the glove no nearer
	# than the sleeve's surface there. Glove triangles by 5 mm slice along the forearm, so each ray tries only a few.
	var slices: Dictionary = {}
	for t in range(0, glove.size(), 3):
		var low := INF
		var high := -INF
		for n in 3:
			var along := (glove[t + n] - end).dot(back)
			low = minf(low, along)
			high = maxf(high, along)
		for slice in range(floori(low / 0.005), floori(high / 0.005) + 1):
			var list: PackedInt32Array = slices.get(slice, PackedInt32Array())
			list.append(t)
			slices[slice] = list
	var poking := 0
	for p in sleeve:
		# The very end of the sleeve is its cap, deep inside the glove.
		var along := (p - end).dot(back)
		if along < 0.003 or along >= CUFF_INSIDE:
			continue
		var axis := end + back * along
		var out := (p - axis).normalized()
		var covered := false
		for t: int in slices.get(floori(along / 0.005), PackedInt32Array()):
			var hit: Variant = Geometry3D.ray_intersects_triangle(axis, out, glove[t], glove[t + 1], glove[t + 2])
			if hit != null and axis.distance_to(hit) >= _radius(p, end, back) - 0.0005:
				covered = true
				break
		if not covered:
			poking += 1
	if poking > 0:
		print("FAIL: the sleeve pokes through the glove's cuff, %s (%d points)" % [what, poking])
	# The cuff proper (well behind the wrist, still over the sleeve) keeps close to the sleeve all round.
	var rings := _sleeve_rings(sleeve, end, back)
	var standing := 0
	for p in glove:
		# Past the sleeve's end there's no sleeve to hug.
		var along := (p - end).dot(back)
		if along > 0.004 and _radius(p, end, back) > _sleeve_radius(rings, along) + CUFF_STANDOFF:
			standing += 1
	if standing > 0:
		print("FAIL: the glove's cuff sticks out from the sleeve, %s (%d points)" % [what, standing])


## The sleeve's rings: millimeters along it from its end -> its radius there. Its vertices all lie on a few rings.
static func _sleeve_rings(sleeve: PackedVector3Array, end: Vector3, back: Vector3) -> Dictionary:
	var rings: Dictionary = {}
	for q in sleeve:
		var mm := roundi((q - end).dot(back) * 1000.0)
		rings[mm] = maxf(rings.get(mm, 0.0), _radius(q, end, back))
	return rings


## The sleeve's radius a distance along it: the wider of the rings on either side.
static func _sleeve_radius(rings: Dictionary, along: float) -> float:
	var mm := along * 1000.0
	var below := -INF
	var above := INF
	for at: int in rings:
		if at <= mm:
			below = maxf(below, at)
		if at >= mm:
			above = minf(above, at)
	return maxf(rings.get(int(below), 0.0), rings.get(int(above), 0.0))


static func _radius(p: Vector3, end: Vector3, back: Vector3) -> float:
	var d := p - end
	return (d - back * d.dot(back)).length()


## A mesh's triangles where it's drawn now (world space), skinned by its skeleton's current pose if it has one.
## Headless there's no renderer to bake a skinned pose, so this does the skinning itself.
## behind: only triangles whose rest pose lies wholly behind this model-space x (the glove's cuff: behind the wrist).
static func _posed_faces(mesh: MeshInstance3D, behind: float = INF) -> PackedVector3Array:
	var out := PackedVector3Array()
	var skeleton := mesh.get_node_or_null(mesh.skeleton) as Skeleton3D
	if mesh.skin == null or skeleton == null:
		# Riding a bone (the glove's rim on its cuff): where the bone is now, before the attachment catches up.
		var attachment := mesh.get_parent() as BoneAttachment3D
		var xform := mesh.global_transform
		if attachment:
			var rig := attachment.get_parent() as Skeleton3D
			xform = rig.global_transform * rig.get_bone_global_pose(attachment.bone_idx) * mesh.transform
		for v in mesh.mesh.get_faces():
			out.append(xform * v)
		return out
	var binds: Array[Transform3D] = []
	for b in mesh.skin.get_bind_count():
		var bone := mesh.skin.get_bind_bone(b)
		if bone < 0:
			bone = skeleton.find_bone(mesh.skin.get_bind_name(b))
		binds.append(skeleton.global_transform * skeleton.get_bone_global_pose(bone) * mesh.skin.get_bind_pose(b))
	for surface in mesh.mesh.get_surface_count():
		var arrays := mesh.mesh.surface_get_arrays(surface)
		var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
		var bones: PackedInt32Array = arrays[Mesh.ARRAY_BONES]
		var weights: PackedFloat32Array = arrays[Mesh.ARRAY_WEIGHTS]
		var per := bones.size() / vertices.size()
		var posed := PackedVector3Array()
		posed.resize(vertices.size())
		for v in vertices.size():
			var at := Vector3.ZERO
			for n in per:
				at += binds[bones[v * per + n]] * vertices[v] * weights[v * per + n]
			posed[v] = at
		var indices: PackedInt32Array = arrays[Mesh.ARRAY_INDEX]
		for t in range(0, indices.size(), 3):
			if vertices[indices[t]].x < behind and vertices[indices[t + 1]].x < behind and vertices[indices[t + 2]].x < behind:
				out.append_array([posed[indices[t]], posed[indices[t + 1]], posed[indices[t + 2]]])
	return out


## Every tool held in a glove the way the game holds it, fitted by data/grips.json: nothing of the tool is inside the
## glove's fingers or palm. A tool may pass between the fingers or rest against them, not through them.
func _grip_clearance(holder: Node3D, hand_index: int) -> void:
	var hand := GripCheck.make_hand(holder, hand_index)
	var seen: Dictionary = {}
	for def: ToolDef in Db.tools.values():
		var model_id := def.model if def.model else def.id
		if seen.has(model_id):
			continue
		seen[model_id] = true
		var tool := GripCheck.hold(hand, def, Db.grip_fit(def, hand_index), holder)
		await get_tree().physics_frame
		var clipped := GripCheck.clipped(hand)
		if clipped > 0:
			print("FAIL: the %s goes through the %s glove holding it (%d points)" % [model_id, "left" if hand_index == 0 else "right", clipped])
		tool.queue_free()
		await get_tree().physics_frame
	hand.get_parent().queue_free()


func _exists(category: String, model_name: String) -> bool:
	var path := "%s/%s/%s.glb" % [ModelSlot.ROOT, category, model_name]
	if not ResourceLoader.exists(path):
		print("FAIL: missing model ", path)
		return false
	return true


func _rig(category: String, model_name: String, bones: PackedStringArray, parts: PackedStringArray, holder: Node3D) -> void:
	if not _exists(category, model_name):
		return
	var model := ModelSlot.instantiate(category, model_name, holder)
	var rig := BoneRig.find(model)
	if rig == null:
		print("FAIL: %s/%s has no skeleton" % [category, model_name])
		return
	for bone in bones:
		if not rig.has(bone):
			print("FAIL: %s/%s has no bone %s" % [category, model_name, bone])
	for part in parts:
		if model.find_child(part, true, false) == null:
			print("FAIL: %s/%s has no part %s" % [category, model_name, part])


func _check_budgets(holder: Node3D) -> void:
	for category: String in BUDGETS:
		var dir := "%s/%s" % [ModelSlot.ROOT, category]
		for file in DirAccess.get_files_at(dir):
			if not file.ends_with(".glb"):
				continue
			var model := ModelSlot.instantiate(category, file.get_basename(), holder)
			var count := _triangles(model)
			if count > int(BUDGETS[category]):
				print("FAIL: %s/%s has %d triangles, budget %d" % [category, file, count, BUDGETS[category]])
			model.queue_free()


static func _triangles(root: Node) -> int:
	var total := 0
	for node in root.find_children("*", "MeshInstance3D", true, false):
		var mesh := (node as MeshInstance3D).mesh
		for i in mesh.get_surface_count():
			var arrays := mesh.surface_get_arrays(i)
			var indices: PackedInt32Array = arrays[Mesh.ARRAY_INDEX]
			total += (indices.size() if not indices.is_empty() else (arrays[Mesh.ARRAY_VERTEX] as PackedVector3Array).size()) / 3
	return total
