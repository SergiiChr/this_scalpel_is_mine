class_name SurgicalTool
extends RigidBody3D
## A grabbable item. Physics runs on the host only, clients get transforms from ToolManager.
## The grip is at the origin and the tip at -Z * length.

enum State { FREE, HELD, BELT, STANDING, INSIDE, CONSUMED }

const TOOL_LAYER := 8

var uid: int
var def: ToolDef
var state := State.FREE
## Peer id of whoever holds it (HELD, BELT) or last held it.
var holder := 0
## Hand index while HELD, belt slot while on the BELT.
var slot := -1
var sterile := true
## Fell on the floor: visibly dirty. Needs the sink before the sanitizer can make it sterile again.
var soiled := false
var charges := -1

# Host-side use state, see ToolActions.
var grip_info: Dictionary = {}
var engaged_before := false
var stroke := 0
var last_uv := Vector2(-1, -1)
var last_tip := Vector3.INF
var charge_time := 0.0
var reported: Dictionary = {}

var _model: Node3D
var _animator := ToolAnimator.new()
## This tool's own copies of its toon materials, made the first time it needs to look different from the rest.
var _own_materials: Array[ShaderMaterial] = []


func setup(tool_uid: int, tool_def: ToolDef) -> void:
	uid = tool_uid
	def = tool_def
	name = "Tool%d_%s" % [uid, def.id]
	sterile = def.sterile
	charges = def.charges
	mass = 1.2 if def.size == "heavy" else 0.2
	collision_layer = TOOL_LAYER
	collision_mask = 1 | 2 | 4 | TOOL_LAYER
	continuous_cd = true
	contact_monitor = true
	max_contacts_reported = 2
	_model = ToolModel.build(def, self)
	# The collision box wraps the model itself, so a bag or a flask rests on the tray instead of sinking into it.
	var bounds := _model_bounds()
	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = bounds.size.max(Vector3.ONE * 0.006)
	shape.shape = box
	shape.position = bounds.get_center()
	add_child(shape)
	_animator.setup(_model)
	freeze_mode = RigidBody3D.FREEZE_MODE_KINEMATIC
	freeze = not multiplayer.is_server()


func _process(delta: float) -> void:
	var active := false
	var closed := state == State.STANDING
	if state == State.HELD and Surgery.current and Surgery.current.surgeons.has(holder):
		var hand: SurgeonHand = (Surgery.current.surgeons[holder] as Surgeon).hands[slot]
		active = hand.engaged
		closed = hand.attached
	_animator.animate(active, closed, delta)


## The model's bounding box in the tool's own space. Falls back to a thin box along the tool if there's no model.
func _model_bounds() -> AABB:
	var bounds := AABB(Vector3(-def.width * 0.5, -def.width * 0.3, -def.length), Vector3(def.width, def.width * 0.6, def.length))
	var first := true
	for node in _model.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		var box := (global_transform.affine_inverse() * mesh.global_transform) * mesh.get_aabb()
		bounds = box if first else bounds.merge(box)
		first = false
	return bounds


func tip_position() -> Vector3:
	return global_transform * Vector3(0, 0, -def.length)


func is_improvised() -> bool:
	return def.improvised


## Germaphobe quirk: unsterile tools glow for this player only.
func show_contamination(visible_to_me: bool) -> void:
	var glow := 0.0 if sterile or not visible_to_me else 1.0
	if glow > 0.0 or not _own_materials.is_empty():
		for mat in _materials():
			mat.set_shader_parameter("contamination", glow)


## Floor dirt shows as heavy grime on the tool for everyone.
func set_soiled(value: bool) -> void:
	soiled = value
	if value or not _own_materials.is_empty():
		for mat in _materials():
			mat.set_shader_parameter("grime", 0.95 if value else mat.get_meta("grime", 0.1))


## The tool your hand would pick up glows faintly (local player only).
func set_highlight(on: bool) -> void:
	for mat in _materials():
		mat.set_shader_parameter("emission_color", Color(0.25, 0.3, 0.22) if on else Color.BLACK)


## Toon materials are shared between tools, so the first per-tool change swaps in copies of its own.
func _materials() -> Array[ShaderMaterial]:
	if _own_materials.is_empty():
		for node in _model.find_children("*", "MeshInstance3D", true, false):
			var mesh := node as MeshInstance3D
			for i in mesh.get_surface_override_material_count():
				var mat := mesh.get_surface_override_material(i) as ShaderMaterial
				if mat and mat.shader == Materials.TOON:
					mat = mat.duplicate() as ShaderMaterial
					mat.set_meta("grime", mat.get_shader_parameter("grime"))
					mesh.set_surface_override_material(i, mat)
					_own_materials.append(mat)
	return _own_materials


func set_state(new_state: State, new_holder: int, new_slot: int) -> void:
	state = new_state
	holder = new_holder
	slot = new_slot
	var physical := state == State.FREE
	visible = state != State.CONSUMED
	collision_layer = TOOL_LAYER if state in [State.FREE, State.STANDING, State.INSIDE] else 0
	freeze = not (physical and multiplayer.is_server())
	if state != State.HELD:
		engaged_before = false
		last_tip = Vector3.INF
