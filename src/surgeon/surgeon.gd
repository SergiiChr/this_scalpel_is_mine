class_name Surgeon
extends CharacterBody3D
## A player in the room. The owning peer reads input, moves the body and hands, and streams its state.
## Everyone else (including the host) sees a puppet that follows the stream.
##
## Controls: mouse moves the active hand, hold look to turn the head instead, WASD moves the body.
## The inactive hand stays exactly where it was, still doing what it was doing.

const WALK_SPEED := 1.6
const REACH := 0.72
const EYE_HEIGHT := 1.62
const SHOULDER := Vector3(0.19, 1.4, -0.08)
const HAND_SENSITIVITY := 0.0009
const LOOK_SENSITIVITY := 0.003
const HOVER_STEP := 0.01
const SYNC_INTERVAL := 1.0 / 30.0
const BUMP_DISTANCE := 0.07
const SWITCH_DELAY := 0.25
const MAX_BELT := 4
const LOOK_PITCH := Vector2(-1.3, 0.6)
const INTERACT_RANGE := 1.8
## How close a partner's empty hand must be to hand a tool over instead of dropping it.
const PASS_DISTANCE := 0.18
## Hand speed (m/s) above which a handoff fumbles.
const FUMBLE_SPEED := 0.35

var peer_id := 1
var display_name := "Doctor"
var quirk_rolls: Array = []
var mods := Modifiers.new()
var status: SurgeonStatus
var hands: Array[SurgeonHand] = []
var active := 1
var pitch := -0.55
var input_locked := false
var is_local := false
var focused: Interactable = null

var _head: Node3D
var _body: Node3D
var _face: Node3D
var _camera: Camera3D
var _joints: Dictionary = {}
var _rest: Dictionary = {}
var _walk_phase := 0.0
var _collapse := 0.0
var _last_position := Vector3.ZERO
var _remote_out := false
var _switch_timer := 0.0
var _sync_acc := 0.0
var _jolt := Vector3.ZERO
var _net_position := Vector3.ZERO
var _net_yaw := 0.0
var _strain := [false, false]


func setup(peer: int, player_name: String, rolls: Array, spawn: Transform3D) -> void:
	peer_id = peer
	name = "Surgeon%d" % peer
	display_name = player_name
	quirk_rolls = rolls
	mods = Modifiers.from_rolls(rolls, Db.surgeon_quirks)
	status = SurgeonStatus.new(mods)
	status.cold_tremor = 0.0015 if Surgery.current and Surgery.current.run_mods.flag("cold") else 0.0
	is_local = peer == multiplayer.get_unique_id()
	set_multiplayer_authority(peer)
	global_transform = spawn
	_net_position = spawn.origin
	_last_position = spawn.origin
	_net_yaw = rotation.y
	collision_layer = 16
	collision_mask = 1 | 16
	var shape := CollisionShape3D.new()
	var capsule := CapsuleShape3D.new()
	capsule.radius = 0.24
	capsule.height = 1.75
	shape.shape = capsule
	shape.position.y = 0.875
	add_child(shape)
	_build_visuals()
	for i in 2:
		var hand := SurgeonHand.new()
		add_child(hand)
		hand.build(i, _scrubs_color())
		hand.local_target = Vector3(-0.17 if i == 0 else 0.17, 1.18, -0.45)
		hand.puppet = not is_local
		hand.target = to_global(hand.local_target)
		hands.append(hand)
	if is_local:
		for hand in hands:
			hand.hide_upper_arm()
		_camera.make_current()
		Sfx.deaf = mods.flag("deaf")


func camera() -> Camera3D:
	return _camera


func belt_capacity() -> int:
	return clampi(MAX_BELT + int(mods.num("belt_slots")), 0, MAX_BELT)


func belt_transform(belt_slot: int) -> Transform3D:
	var offset := Vector3(-0.2 + belt_slot * 0.13, 0.95, -0.12)
	return Transform3D(global_basis * Basis(Vector3.RIGHT, -PI / 2), to_global(offset))


func shoulder(hand: int) -> Vector3:
	return to_global(Vector3(SHOULDER.x * (-1.0 if hand == 0 else 1.0), SHOULDER.y, SHOULDER.z))


func held_tool(hand: int) -> SurgicalTool:
	return Surgery.current.tools.tool_in_hand(peer_id, hand)


## What the host needs to drive a tool held in this hand.
func hand_state(hand: int) -> Dictionary:
	var h := hands[hand]
	return {"engaged": h.engaged and not status.is_out(), "pressure": h.pressure, "speed": h.speed, "peer": peer_id, "mods": mods}


## Empty string if this surgeon can use the tool, otherwise why not.
func blocked_reason(def: ToolDef) -> String:
	if def.size == "fine" and mods.flag("fine_tools_blocked"):
		return "Your fingers are too big for the %s." % def.name
	if def.size == "heavy" and mods.flag("heavy_tools_blocked"):
		return "Your hands are too small to handle the %s." % def.name
	return ""


func _scrubs_color() -> Color:
	return Color(0.2, 0.36, 0.34) if peer_id == 1 else Color(0.36, 0.26, 0.4)


func _build_visuals() -> void:
	var scrubs := {"tint": Materials.toon(_scrubs_color(), 0.35)}
	_body = ModelSlot.instantiate("surgeon", "body", self, scrubs)
	_head = Node3D.new()
	_head.name = "Head"
	_head.position.y = EYE_HEIGHT
	add_child(_head)
	_face = ModelSlot.instantiate("surgeon", "head", _head, {"tint": Materials.toon(_scrubs_color().darkened(0.2), 0.35), "skin": Materials.toon(Color(0.8, 0.64, 0.54), 0.1)})
	_camera = Camera3D.new()
	_camera.name = "Camera"
	_camera.fov = 70.0
	_camera.near = 0.03
	_head.add_child(_camera)
	for joint: String in ["Torso", "LegL", "LegR"]:
		var node := _body.find_child(joint, true, false) as Node3D
		if node:
			_joints[joint] = node
			_rest[joint] = node.transform
	if peer_id == multiplayer.get_unique_id():
		_face.visible = false
		_body.visible = false
	var tag := Shapes.label(self, "", Vector3(0, 2.0, 0), 48)
	tag.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	tag.text = display_name if peer_id != multiplayer.get_unique_id() else ""


## Walk cycle from actual movement speed, collapse while passed out. Works the same for local and remote surgeons.
func _animate_body(delta: float) -> void:
	var speed := Vector2(global_position.x - _last_position.x, global_position.z - _last_position.z).length() / maxf(delta, 0.0001)
	_last_position = global_position
	_walk_phase += delta * speed * 7.0
	var stride := clampf(speed / WALK_SPEED, 0.0, 1.0) * 0.45
	var out := status.is_out() if is_local else _remote_out
	_collapse = move_toward(_collapse, 1.0 if out else 0.0, delta * 2.5)
	_pose("LegL", Vector3(sin(_walk_phase) * stride, 0, 0))
	_pose("LegR", Vector3(-sin(_walk_phase) * stride, 0, 0))
	_pose("Torso", Vector3(-_collapse * 1.3 + absf(sin(_walk_phase)) * stride * 0.05, 0, 0))
	_head.position.y = EYE_HEIGHT - _collapse * 1.1


func _pose(joint: String, euler: Vector3) -> void:
	var node: Node3D = _joints.get(joint)
	if node:
		var rest: Transform3D = _rest[joint]
		node.transform = Transform3D(rest.basis * Basis.from_euler(euler), rest.origin)


# --- Local control ---------------------------------------------------------------------------------


func _unhandled_input(event: InputEvent) -> void:
	if not is_local or input_locked or status.is_out():
		return
	var hand := hands[active]
	if event is InputEventMouseMotion and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED:
		var motion := (event as InputEventMouseMotion).relative * Settings.mouse_sensitivity
		if Input.is_action_pressed("look"):
			rotation.y -= motion.x * LOOK_SENSITIVITY
			pitch = clampf(pitch - motion.y * LOOK_SENSITIVITY, LOOK_PITCH.x, LOOK_PITCH.y)
		elif _switch_timer <= 0.0:
			var step := motion * HAND_SENSITIVITY * status.hand_speed()
			_move_hand(hand, Vector3(step.x, 0, step.y))
	elif event.is_action_pressed("hand_up") or event.is_action_pressed("hand_down"):
		var up := 1 if event.is_action_pressed("hand_up") else -1
		var over_open: bool = _surface_below(hand.target).open
		if hand.engaged and not over_open:
			hand.pressure = clampi(hand.pressure + up, 1, 3)
		else:
			_move_hand(hand, Vector3(0, HOVER_STEP * up, 0), true)
	elif event.is_action_pressed("use_tool"):
		hand.engaged = true
	elif event.is_action_released("use_tool"):
		hand.engaged = false
	elif event.is_action_pressed("switch_hand"):
		_switch_hand()
	elif event.is_action_pressed("grab"):
		_grab_or_release()
	elif event.is_action_pressed("interact") and focused:
		focused.interact(self)
	elif event.is_action_pressed("drink"):
		Surgery.current.request_drink(active)
	else:
		for i in MAX_BELT:
			if event.is_action_pressed("belt_%d" % (i + 1)):
				Surgery.current.tools.request_belt(active, i)


func _physics_process(delta: float) -> void:
	if is_local:
		_local_update(delta)
		_sync_acc += delta
		if _sync_acc >= SYNC_INTERVAL:
			_sync_acc = 0.0
			_sync_state.rpc(_pack_state())
	else:
		global_position = global_position.lerp(_net_position, minf(delta * 15.0, 1.0))
		rotation.y = lerp_angle(rotation.y, _net_yaw, minf(delta * 15.0, 1.0))
	_head.rotation.x = pitch
	# The camera pitches fully, the visible head only half as much so it doesn't look broken-necked.
	_face.rotation.x = -pitch * 0.5
	_animate_body(delta)
	for i in 2:
		hands[i].holding = held_tool(i) != null
		hands[i].update_pose(shoulder(i), delta)


func _local_update(delta: float) -> void:
	_switch_timer = maxf(_switch_timer - delta, 0.0)
	var can_act := not input_locked and not status.is_out()
	var dir := Input.get_vector("move_left", "move_right", "move_forward", "move_back") if can_act else Vector2.ZERO
	var move := global_basis * Vector3(dir.x, 0, dir.y) * WALK_SPEED * mods.mult("move_speed_mult")
	velocity = Vector3(move.x, velocity.y - 9.8 * delta if not is_on_floor() else 0.0, move.z)
	move_and_slide()
	var hand := hands[active]
	if can_act:
		var tilt_input := Input.get_axis("tilt_back", "tilt_forward")
		var twist_input := Input.get_axis("twist_left", "twist_right")
		hand.tilt = clampf(hand.tilt - tilt_input * delta * 1.5, SurgeonHand.TILT_RANGE.x, SurgeonHand.TILT_RANGE.y)
		hand.twist = wrapf(hand.twist + twist_input * delta * 2.0, -PI, PI)
		hand.lifted = Input.is_action_pressed("lift")
		status.holding_breath = Input.is_action_pressed("steady") and status.breath > 0.0
	for i in 2:
		var h := hands[i]
		if not h.attached:
			h.target = to_global(h.local_target)
		_strain[i] = h.attached and h.target.distance_to(shoulder(i)) > REACH + 0.06
		_constrain(h)
		var amount := status.tremor_amount() if i == active or mods.mult("switch_delay_mult") > 0.0 else 0.0
		var t := Time.get_ticks_msec() * 0.001
		h.tremor = Vector3(sin(t * 23.0 + i), sin(t * 31.0 + 2.0 * i), cos(t * 19.0 + i)) * amount + _jolt
	_jolt = _jolt.lerp(Vector3.ZERO, minf(delta * 8.0, 1.0))
	_check_bumps()
	_update_focus()
	_handle_status_events(status.update(delta, _status_context()))


func _move_hand(hand: SurgeonHand, delta_local: Vector3, vertical: bool = false) -> void:
	var yaw_basis := Basis(Vector3.UP, rotation.y)
	var step := yaw_basis * delta_local if not vertical else delta_local
	hand.target += step
	var from := shoulder(hand.index)
	if hand.target.distance_to(from) > REACH:
		hand.target = from + (hand.target - from).normalized() * REACH
	if not hand.attached:
		hand.local_target = to_local(hand.target)


## Keeps the tool tip on top of whatever is under it. While using a tool on skin, presses into it by pressure level.
func _constrain(hand: SurgeonHand) -> void:
	var tool := held_tool(hand.index)
	var offset := hand.tip_offset(tool.def.length) if tool else Vector3(0, -0.03, 0)
	var tip := hand.target + offset
	var surface := _surface_below(tip)
	if surface.y == -INF:
		return
	var tip_y: float = surface.y + 0.004
	if hand.engaged and not surface.open:
		tip_y = surface.y - hand.pressure * 0.004
		hand.target.y = tip_y - offset.y
	elif tip.y < tip_y:
		hand.target.y += tip_y - tip.y
	else:
		return
	if not hand.attached:
		hand.local_target = to_local(hand.target)


## {"y": surface height under p (or -INF), "open": true when p is over an opened incision}
func _surface_below(p: Vector3) -> Dictionary:
	var space := get_world_3d().direct_space_state
	var query := PhysicsRayQueryParameters3D.create(p + Vector3.UP * 0.35, p + Vector3.DOWN * 0.4, 1 | 2 | 4)
	var hit := space.intersect_ray(query)
	if hit.is_empty():
		return {"y": -INF, "open": false}
	if (hit.collider as Object).has_meta("site"):
		var body := Surgery.current.patient.body
		if body.wound_map.is_open(body.world_to_uv(hit.position)):
			query.collision_mask = PatientBody.CAVITY_LAYER
			var floor_hit := space.intersect_ray(query)
			return {"y": floor_hit.position.y if not floor_hit.is_empty() else hit.position.y - 0.1, "open": true}
	return {"y": hit.position.y, "open": false}


func _switch_hand() -> void:
	hands[active].engaged = hands[active].engaged and held_tool(active) != null
	active = 1 - active
	_switch_timer = SWITCH_DELAY * mods.mult("switch_delay_mult")


## A partner's empty hand close enough to take what this hand holds: [Surgeon, hand index], or [] if none.
func pass_target(hand: int) -> Array:
	var from := hands[hand].global_position
	for other: Surgeon in Surgery.current.surgeons.values():
		if other == self:
			continue
		for i in 2:
			if other.held_tool(i) == null and other.hands[i].global_position.distance_to(from) < PASS_DISTANCE:
				return [other, i]
	return []


func _grab_or_release() -> void:
	var hand := hands[active]
	var tool := held_tool(active)
	if tool:
		hand.engaged = false
		if not pass_target(active).is_empty() and not hand.attached:
			Surgery.current.tools.request_pass(active)
		else:
			Surgery.current.tools.request_release(active, Vector3.ZERO)
		return
	var near := Surgery.current.tools.nearest_grabbable(hand.global_position + hand.tip_offset(0.05))
	if near:
		if mods.flag("contamination_vision") and not near.sterile:
			status.add_stress(mods.num("dirty_stress"))
		Surgery.current.tools.request_grab(near, active)


## Hands that aren't lifted knock into other surgeons' hands.
func _check_bumps() -> void:
	var mine := hands[active]
	if mine.lifted:
		return
	for other: Surgeon in Surgery.current.surgeons.values():
		if other == self:
			continue
		for their in other.hands:
			if their.lifted:
				continue
			var gap := mine.global_position - their.global_position
			if gap.length() < BUMP_DISTANCE:
				mine.target += gap.normalized() * 0.05
				if not mine.attached:
					mine.local_target = to_local(mine.target)
				jolt(0.35)
				Sfx.play("bump", mine.global_position)
				return


## Something shook this surgeon (seizure, pothole, a bump, a shove). May drop what's in hand.
func jolt(strength: float) -> void:
	if not is_local:
		return
	_jolt += Vector3(randf_range(-1, 1), randf_range(-0.5, 0.5), randf_range(-1, 1)) * strength * 0.03
	status.add_stress(strength * 0.05)
	var drop_chance := strength * 0.35 * (1.0 - clampf(mods.num("bump_resist"), 0.0, 1.0))
	if randf() < drop_chance and held_tool(active) and not hands[active].attached:
		hands[active].engaged = false
		Surgery.current.tools.request_release(active, _jolt * 10.0)
		Surgery.current.hud.toast("It slips out of your hand!")


func drop_everything() -> void:
	for i in 2:
		hands[i].engaged = false
		if held_tool(i):
			Surgery.current.tools.request_release(i, Vector3.ZERO)


func _update_focus() -> void:
	var from := _camera.global_position
	var query := PhysicsRayQueryParameters3D.create(from, from - _camera.global_basis.z * INTERACT_RANGE, Interactable.LAYER)
	query.collide_with_areas = true
	query.collide_with_bodies = false
	var hit := get_world_3d().direct_space_state.intersect_ray(query)
	focused = hit.collider as Interactable if not hit.is_empty() else null


func _status_context() -> Dictionary:
	var partner_breath := 0.0
	for other: Surgeon in Surgery.current.surgeons.values():
		if other != self and other.global_position.distance_to(global_position) < 0.9:
			partner_breath += other.mods.num("bad_breath")
	return {"bleed_rate": Surgery.current.patient.vitals.bleed_rate, "partner_breath": partner_breath}


func _handle_status_events(events: PackedStringArray) -> void:
	var hud := Surgery.current.hud
	for event in events:
		match event:
			"pass_out":
				drop_everything()
				hud.toast("Everything goes dark...")
				Surgery.current.report_incident("passed_out")
			"vomit":
				hud.toast("You vomit.")
				Sfx.play("vomit", global_position)
				Surgery.current.report_incident("vomit")
			"cough":
				Sfx.play("cough", global_position)
				jolt(0.4)
			"slip":
				if held_tool(active) and not hands[active].attached:
					hud.toast("Your sweaty glove slips!")
					Surgery.current.tools.request_release(active, Vector3.ZERO)
			"drip":
				if Surgery.current.patient.body.probe(hands[active].global_position + Vector3.DOWN * 0.1).zone in ["site", "cavity"]:
					hud.toast("A drop of sweat falls into the wound.")
					Surgery.current.report_incident("sweat_drip")
			"gasp":
				hud.toast("You gasp for air.")
				jolt(0.2)


# --- Networking ------------------------------------------------------------------------------------


func _pack_state() -> Array:
	var hand_data: Array = []
	for h in hands:
		hand_data.append([h.effective_position(), h.tilt, h.twist, h.engaged, h.pressure, h.lifted])
	return [global_position, rotation.y, pitch, active, hand_data, _strain, status.is_out()]


@rpc("authority", "call_remote", "unreliable_ordered")
func _sync_state(data: Array) -> void:
	_net_position = data[0]
	_net_yaw = data[1]
	pitch = data[2]
	active = data[3]
	_remote_out = data[6]
	for i in 2:
		var h := hands[i]
		var d: Array = data[4][i]
		h.target = d[0]
		h.tilt = d[1]
		h.twist = d[2]
		h.engaged = d[3]
		h.pressure = d[4]
		h.lifted = d[5]
	if multiplayer.is_server():
		var strain: Array = data[5]
		for i in 2:
			if strain[i]:
				Surgery.current.overstretched(peer_id, i)


## Host tells the owner a hand is now holding onto something (or let go). Attached hands don't follow the body.
@rpc("any_peer", "call_local", "reliable")
func set_hand_attached(hand: int, value: bool) -> void:
	if Net._sender() != 1:
		return
	hands[hand].attached = value
	if not value:
		hands[hand].local_target = to_local(hands[hand].target)
