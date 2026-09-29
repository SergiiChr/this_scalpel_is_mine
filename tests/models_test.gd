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
	# Every target kind any scenario uses needs a model.
	var kinds: Dictionary = {}
	for scenario: ScenarioDef in Db.scenarios:
		for target: Dictionary in scenario.targets:
			kinds[target.get("kind", "bullet")] = true
	for kind: String in kinds:
		_exists("targets", kind)
	_check_budgets(holder)
	print("models_test: done")
	get_tree().quit()


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
