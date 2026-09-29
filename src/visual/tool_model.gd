class_name ToolModel
extends RefCounted
## Tool art: a generated model per tool (or shared via `model=` in tools.cfg). "tint" parts take the tool's color.


static func build(def: ToolDef, parent: Node3D) -> Node3D:
	return ModelSlot.instantiate("tools", def.model if def.model else def.id, parent, {"tint": Materials.toon(def.color, 0.15)})
