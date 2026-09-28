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
	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = Vector3(maxf(def.width, 0.01), maxf(def.width * 0.6, 0.01), def.length)
	shape.shape = box
	shape.position.z = -def.length * 0.5
	add_child(shape)
	_model = ToolModel.build(def, self)
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


func tip_position() -> Vector3:
	return global_transform * Vector3(0, 0, -def.length)


func is_improvised() -> bool:
	return def.improvised


## Germaphobe quirk: unsterile tools glow for this player only.
func show_contamination(visible_to_me: bool) -> void:
	var glow := 0.0 if sterile or not visible_to_me else 1.0
	for mesh in _model.find_children("*", "MeshInstance3D", true, false):
		var mat := (mesh as MeshInstance3D).material_override
		if mat is ShaderMaterial and (mat as ShaderMaterial).shader == Materials.TOON:
			if glow > 0.0 and not mesh.has_meta("unique"):
				mat = (mat as ShaderMaterial).duplicate()
				(mesh as MeshInstance3D).material_override = mat
				mesh.set_meta("unique", true)
			(mat as ShaderMaterial).set_shader_parameter("contamination", glow)


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
