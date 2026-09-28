class_name ModelSlot
extends RefCounted
## Swappable art. If assets/models/<category>/<name>.glb (or .tscn) exists it is used,
## otherwise the caller's placeholder builder runs. Drop a file in and it replaces the primitive.

const ROOT := "res://assets/models"


static func instantiate(category: String, model_name: String, parent: Node3D, placeholder: Callable) -> Node3D:
	for extension: String in ["glb", "gltf", "tscn"]:
		var path := "%s/%s/%s.%s" % [ROOT, category, model_name, extension]
		if ResourceLoader.exists(path):
			var node := (load(path) as PackedScene).instantiate() as Node3D
			parent.add_child(node)
			return node
	var root := Node3D.new()
	root.name = model_name.to_pascal_case()
	parent.add_child(root)
	placeholder.call(root)
	return root
