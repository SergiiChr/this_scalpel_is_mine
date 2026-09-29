class_name Interactable
extends Area3D
## Something you walk up to and press interact on. Room builds these and hands them a callback.

const LAYER := 64

var prompt := ""
var action: Callable


static func create(parent: Node3D, label: String, size: Vector3, pos: Vector3, callback: Callable) -> Interactable:
	var area := Interactable.new()
	area.name = label.to_pascal_case().validate_node_name()
	area.prompt = label
	area.action = callback
	area.collision_layer = LAYER
	area.collision_mask = 0
	area.monitoring = false
	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = size
	shape.shape = box
	area.add_child(shape)
	parent.add_child(area)
	area.position = pos
	return area


func interact(surgeon: Surgeon) -> void:
	if action.is_valid():
		action.call(surgeon)
