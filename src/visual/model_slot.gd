class_name ModelSlot
extends RefCounted
## Loads the model for assets/models/<category>/<name>.glb (or .gltf/.tscn).
## Generated models come from tools/assetgen; replace a file to replace the art.
## Imported materials are swapped for the game's shaded ones by material name.

const ROOT := "res://assets/models"


## overrides: material name -> Material, for names that need per-instance treatment ("skin", "tint").
static func instantiate(category: String, model_name: String, parent: Node3D, overrides: Dictionary = {}) -> Node3D:
	for extension: String in ["glb", "gltf", "tscn"]:
		var path := "%s/%s/%s.%s" % [ROOT, category, model_name, extension]
		if ResourceLoader.exists(path):
			var node := (load(path) as PackedScene).instantiate() as Node3D
			parent.add_child(node)
			toonify(node, overrides)
			return node
	push_error("Missing model %s/%s. Run: python -m tools.assetgen" % [category, model_name])
	var empty := Node3D.new()
	parent.add_child(empty)
	return empty


## Replaces imported glTF materials with the game's shading, preserving their authored texture inputs.
static func toonify(root: Node, overrides: Dictionary = {}) -> void:
	for node in root.find_children("*", "MeshInstance3D", true, false):
		var mesh_instance := node as MeshInstance3D
		for i in mesh_instance.mesh.get_surface_count():
			var source := mesh_instance.mesh.surface_get_material(i)
			var key := source.resource_name if source else ""
			if overrides.has(key):
				mesh_instance.set_surface_override_material(i, overrides[key])
			elif key == "marks":
				# Fine print: an outline would blot it out.
				mesh_instance.set_surface_override_material(i, Materials.toon(Color(0.05, 0.05, 0.06), 0.1, false))
			elif key == "glass":
				mesh_instance.set_surface_override_material(i, Materials.glass())
			elif key == "flame":
				mesh_instance.set_surface_override_material(i, Materials.glow(Color(1.0, 0.62, 0.2)))
			elif source is BaseMaterial3D:
				mesh_instance.set_surface_override_material(i, Materials.imported(source as BaseMaterial3D))


## Swaps the model's shared toon materials for copies of its own, for per-object tweaks (blood, grime), and returns them.
static func own_materials(root: Node) -> Array[ShaderMaterial]:
	var own: Array[ShaderMaterial] = []
	for node in root.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		for i in mesh.get_surface_override_material_count():
			var mat := mesh.get_surface_override_material(i) as ShaderMaterial
			if mat and mat.shader == Materials.TOON:
				mat = mat.duplicate() as ShaderMaterial
				mesh.set_surface_override_material(i, mat)
				own.append(mat)
	return own


## Named part lookup for procedural animation. Missing parts are simply absent from the result.
static func parts(root: Node, names: PackedStringArray) -> Dictionary:
	var found: Dictionary = {}
	for part_name in names:
		var node := root.find_child(part_name, true, false) as Node3D
		if node:
			found[part_name] = node
	return found
