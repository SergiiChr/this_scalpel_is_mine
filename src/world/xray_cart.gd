class_name XrayCart
extends Node3D
## Mobile X-ray unit. Push it next to the table, take an exposure, and an instant print slides out
## that develops over a few seconds. It shows metal (bullets, knives, retained tools), bone and masses.
## The host owns its position and the print; everyone can read the print.

const EXPOSE_TIME := 2.5
const COOLDOWN := 30.0
const REACH := 1.5
const PUSH_OFFSET := 0.85
const SYNC_INTERVAL := 0.1
const PRINT_SPOT := Vector3(0.0, 0.4, 0.45)

## Last print: {"shapes": Array, "site": String}. Empty until the first exposure.
var print_data: Dictionary = {}
var printed_at_msec := 0
var pusher := 0
var _cooldown := 0.0
var _exposing := 0.0
var _sync_acc := 0.0
var _print: Node3D
var _surgery: Surgery


func build(surgery: Surgery) -> void:
	_surgery = surgery
	ModelSlot.instantiate("props", "xray", self)
	_print = ModelSlot.instantiate("props", "xray_print", self, {"tint": Materials.glow(Color(0.05, 0.06, 0.07))})
	_print.position = PRINT_SPOT
	_print.visible = false
	var body := AnimatableBody3D.new()
	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = Vector3(0.6, 1.7, 0.7)
	shape.shape = box
	shape.position.y = 0.85
	body.add_child(shape)
	add_child(body)
	Interactable.create(self, "Push / let go of the X-ray cart", Vector3(0.5, 0.2, 0.2), Vector3(0, 0.93, 0.38), func(_s: Surgeon) -> void: _req_push.rpc_id(1))
	Interactable.create(self, "Take an X-ray", Vector3(0.35, 0.25, 0.1), Vector3(0, 0.6, 0.38), func(_s: Surgeon) -> void: _req_expose.rpc_id(1))
	Interactable.create(self, "Look at the X-ray print", Vector3(0.2, 0.1, 0.2), PRINT_SPOT, func(_s: Surgeon) -> void: _open_print())


func _physics_process(delta: float) -> void:
	if not multiplayer.is_server():
		return
	_cooldown = maxf(_cooldown - delta, 0.0)
	if _exposing > 0.0:
		_exposing -= delta
		if _exposing <= 0.0:
			_develop()
	var surgeon: Surgeon = _surgery.surgeons.get(pusher)
	if surgeon == null:
		return
	var target := surgeon.global_position - surgeon.global_basis.z * PUSH_OFFSET
	global_position = global_position.lerp(Vector3(target.x, 0.0, target.z), minf(delta * 8.0, 1.0))
	rotation.y = lerp_angle(rotation.y, surgeon.rotation.y + PI, minf(delta * 8.0, 1.0))
	_sync_acc += delta
	if _sync_acc >= SYNC_INTERVAL:
		_sync_acc = 0.0
		_sync_position.rpc(global_position, rotation.y)


func _open_print() -> void:
	if print_data.is_empty():
		_surgery.hud.toast("No print yet. Take an X-ray first.")
	else:
		_surgery.hud.open_xray(self)


## 0..1, how far the print has developed.
func developed() -> float:
	return clampf((Time.get_ticks_msec() - printed_at_msec) / 6000.0, 0.0, 1.0)


func _develop() -> void:
	var patient := _surgery.patient
	var shapes: Array = []
	for target in patient.targets:
		if not target.extracted or target.remove_with in ["saw", "smash"]:
			shapes.append([target.kind, target.uv, target.depth, target.rotation.y])
	for tool: SurgicalTool in _surgery.tools.tools.values():
		if tool.state == SurgicalTool.State.INSIDE:
			shapes.append(["tool", patient.body.world_to_uv(tool.global_position), 0.05, tool.global_rotation.y])
	for organ in patient.body.organs:
		shapes.append(["organ", Vector2(organ.position.x / patient.body.site_size.x + 0.5, organ.position.z / patient.body.site_size.y + 0.5), -organ.position.y])
	_surgery.scoring.add("xray_used", true)
	_print_ready.rpc(shapes, patient.scenario.site)


@rpc("any_peer", "call_local", "reliable")
func _req_push() -> void:
	var peer := Net._sender()
	pusher = 0 if pusher == peer else peer


@rpc("any_peer", "call_local", "reliable")
func _req_expose() -> void:
	var peer := Net._sender()
	if _exposing > 0.0 or _cooldown > 0.0:
		_surgery.tell(peer, "The X-ray is warming up (%d s)." % ceili(_cooldown + _exposing))
		return
	var flat_distance := Vector2(global_position.x, global_position.z).distance_to(Vector2(_surgery.patient.global_position.x, _surgery.patient.global_position.z))
	if flat_distance > REACH:
		_surgery.tell(peer, "Push the cart up to the table first.")
		return
	_exposing = EXPOSE_TIME
	_cooldown = COOLDOWN
	_surgery.sound("xray_expose", global_position)
	_surgery.announce("X-ray! Everyone step back.")


@rpc("authority", "call_local", "reliable")
func _print_ready(shapes: Array, site: String) -> void:
	print_data = {"shapes": shapes, "site": site}
	printed_at_msec = Time.get_ticks_msec()
	_print.visible = true
	Sfx.play("print_whir", global_position)
	_surgery.hud.toast("The print slides out of the X-ray. Give it a few seconds.")


@rpc("authority", "call_remote", "unreliable_ordered")
func _sync_position(pos: Vector3, yaw: float) -> void:
	global_position = pos
	rotation.y = yaw
