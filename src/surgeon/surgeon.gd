class_name Surgeon
extends CharacterBody3D
## A player in the room. The owning peer reads input, moves the body and hands, and streams its state.
## Everyone else (including the host) sees a puppet that follows the stream.
##
## Controls: the mouse looks around, holding a hand's key (Q/E) moves that hand instead and makes it the active one.
## Use tool (LMB, held) rests the active hand's tool on its spot and works it, the wheel sets its effort level
## (see ToolActions.LEVEL_NAMES and TRIGGER_NAMES). A syringe has its own wheel: down pulls the plunger out, up pushes it
## in, 1 ml a notch, with or without Use tool held. Grab (RMB) picks up and puts down. Zoom (Shift) steps through
## three zoom levels; the last one with a syringe or IV catheter in hand frames the needle and what it's in, the
## hands faded.
## WASD moves the body.
## Hands turn and walk with the body, unless they hold onto something (attached): then they stay put.
## The inactive hand stays exactly where it was, still doing what it was doing.
## Hands have no height control: the tool tip rests just above whatever is under it (skin, tray, organs, a target
## in an open cavity), or higher while lifted. Crouching brings everything down within reach of the floor.

const WALK_SPEED := 1.6
const REACH := 0.72
## Closest a hand comes to its shoulder (meters): an elbow folds only so far, closer the forearm would squash.
const MIN_REACH := 0.18
const EYE_HEIGHT := 1.62
const SHOULDER := Vector3(0.19, 1.4, -0.08)
const HAND_SENSITIVITY := 0.0009
const LOOK_SENSITIVITY := 0.003
## Gap between a resting tool tip and the surface under it.
const HOVER_GAP := 0.01
## Holding Lift while holding onto something pulls it up this fast (m/s): slow and steady, so nothing rips.
const PULL_SPEED := 0.05
## Room between the hand (or forearm) and the surface under it: about half a hand's thickness.
const HAND_CLEARANCE := 0.03
## Where hands hang when nothing within reach is under them (the floor while standing): about waist height.
const CARRY_HEIGHT := 1.05
## Crouching lowers eyes and shoulders this much and slows walking to a careful step.
const CROUCH_DROP := 0.75
const CROUCH_SPEED := 0.35
## Zoom steps, cycled by the zoom key: camera field of view, widest first. Hand motion scales with it for precision.
const ZOOM_FOV: Array[float] = [70.0, 45.0, 28.0]
## How far in front of the eyes a tool is held up to look at it (Inspect).
const INSPECT_DISTANCE := 0.26
## Zoomed all the way in with a syringe, the camera looks at it from the side and this far above (radians), with
## this much room around the syringe and its target (meters), and the hands this see-through (see _frame_needle()).
const NEEDLE_VIEW_ELEVATION := 0.6
const NEEDLE_VIEW_MARGIN := 0.025
const NEEDLE_SEE_THROUGH := 0.65
## Tools the last zoom step frames like this: a syringe and the IV catheter.
const NEEDLE_ACTIONS: PackedStringArray = ["syringe", "iv_line"]
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
## A remote surgeon silent this long counts as frozen: the host pauses their tools until they're heard from again,
## so a lag spike doesn't leave a cautery burning or a saw running on its own.
const STALL_SECONDS := 0.75
## Scrubs stain per second per fully bloody glove: hands get wiped on them without thinking.
const STAIN_RATE := 0.004

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
## Tool the active hand would pick up right now (local surgeon only), shown highlighted.
var hovered: SurgicalTool = null
## 0 standing, 1 fully crouched. Synced so everyone sees you duck.
var crouch := 0.0
var zoom := 0
## Uid of the tool each hand held last frame: a new tool starts at effort level 0.
var _held_uid: Array[int] = [0, 0]
## How far the camera has moved over to the needle view (0..1), and the last needle view, to move back from.
var _needle_framing := 0.0
var _needle_view := Transform3D.IDENTITY
## The hand whose syringe the needle view rolls to show its scale (-1: none), and its own twist to roll back to.
var _rolled_hand := -1
var _own_twist := 0.0

var _head: Node3D
var _body: Node3D
## Shared by the body and both sleeves, so blood wiped off the gloves stains them all.
var _scrubs: ShaderMaterial
var _stains := 0.0
var _face: Node3D
var _camera: Camera3D
var _joints: Dictionary = {}
var _rest: Dictionary = {}
var _walk_phase := 0.0
var _ground_speed := 0.0
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
		hand.build(i, _scrubs)
		hand.local_target = Vector3(-0.17 if i == 0 else 0.17, 1.18, -0.45)
		hand.puppet = not is_local
		hand.target = to_global(hand.local_target)
		hands.append(hand)
	if is_local:
		for hand in hands:
			hand.hide_upper_arm()
		_camera.make_current()
		Sfx.deaf = mods.flag("deaf")


## Where the active hand is working: its tool's tip, or just past the fingers when empty (what Grab reaches for).
func aim_point() -> Vector3:
	var hand := hands[active]
	var tool := held_tool(active)
	return hand.global_position + hand.tip_offset(tool.def.length if tool else 0.05)


## How fast the body moves across the floor (m/s), measured the same way on every peer.
func walk_speed() -> float:
	return _ground_speed


func camera() -> Camera3D:
	return _camera


func belt_capacity() -> int:
	return clampi(MAX_BELT + int(mods.num("belt_slots")), 0, MAX_BELT)


func belt_transform(belt_slot: int) -> Transform3D:
	var offset := Vector3(-0.2 + belt_slot * 0.13, 0.95, -0.12)
	return Transform3D(global_basis * Basis(Vector3.RIGHT, -PI / 2), to_global(offset))


func shoulder(hand: int) -> Vector3:
	return to_global(Vector3(SHOULDER.x * (-1.0 if hand == 0 else 1.0), SHOULDER.y - crouch * CROUCH_DROP, SHOULDER.z))


func held_tool(hand: int) -> SurgicalTool:
	return Surgery.current.tools.tool_in_hand(peer_id, hand)


## What the host needs to drive a tool held in this hand.
func hand_state(hand: int) -> Dictionary:
	var h := hands[hand]
	var out := status.is_out()
	return {"lowered": h.lowered and not out, "trigger": h.trigger and not out, "level": h.level, "speed": h.speed, "peer": peer_id, "mods": mods}


func is_stalled() -> bool:
	return not is_local and Net.silence(peer_id) > STALL_SECONDS


## Empty string if this surgeon can use the tool, otherwise why not.
func blocked_reason(def: ToolDef) -> String:
	if def.size == "fine" and mods.flag("fine_tools_blocked"):
		return "Your fingers are too big for the %s." % def.name
	if def.size == "heavy" and mods.flag("heavy_tools_blocked"):
		return "Your hands are too small to handle the %s." % def.name
	return ""


func _stain_scrubs(delta: float) -> void:
	var stains := minf(_stains + (hands[0].blood + hands[1].blood) * STAIN_RATE * delta, 1.0)
	# Shader parameters only change in visible steps.
	if snappedf(stains, 0.02) != snappedf(_stains, 0.02):
		_scrubs.set_shader_parameter("stains", stains)
	_stains = stains


func _scrubs_color() -> Color:
	return Materials.SCRUBS[0 if peer_id == 1 else 1]


func _build_visuals() -> void:
	_scrubs = Materials.family_unique("cloth", _scrubs_color(), 0.9)
	_scrubs.next_pass = Materials.outline_for(0.003)
	_body = ModelSlot.instantiate("surgeon", "body", self, {"tint": _scrubs})
	_head = Node3D.new()
	_head.name = "Head"
	_head.position.y = EYE_HEIGHT
	add_child(_head)
	_face = ModelSlot.instantiate("surgeon", "head", _head, {"tint": Materials.toon(_scrubs_color().darkened(0.2), 0.35), "skin": Materials.family_unique("skin", Color(0.8, 0.64, 0.54), 0.6)})
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
	_ground_speed = speed
	_last_position = global_position
	_walk_phase += delta * speed * 7.0
	var stride := clampf(speed / WALK_SPEED, 0.0, 1.0) * 0.45
	var out := status.is_out() if is_local else _remote_out
	_collapse = move_toward(_collapse, 1.0 if out else 0.0, delta * 2.5)
	# Crouching folds the legs forward and drops the whole body, the torso leaning over the knees.
	_pose("LegL", Vector3(sin(_walk_phase) * stride - crouch * 1.3, 0, 0))
	_pose("LegR", Vector3(-sin(_walk_phase) * stride - crouch * 1.3, 0, 0))
	_pose("Torso", Vector3(-_collapse * 1.3 - crouch * 0.35 + absf(sin(_walk_phase)) * stride * 0.05, 0, 0))
	_body.position.y = -crouch * 0.42
	_head.position.y = EYE_HEIGHT - _collapse * 1.1 - crouch * CROUCH_DROP


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
		if moving_hand() < 0:
			rotation.y -= motion.x * LOOK_SENSITIVITY
			pitch = clampf(pitch - motion.y * LOOK_SENSITIVITY, LOOK_PITCH.x, LOOK_PITCH.y)
		elif _switch_timer <= 0.0:
			# Zoomed in, the same mouse motion moves the hand less: finer control where you're looking closely.
			var step := motion * HAND_SENSITIVITY * status.hand_speed() * ZOOM_FOV[zoom] / ZOOM_FOV[0]
			_move_hand(hand, Vector3(step.x, 0, step.y))
	elif event.is_action_pressed("move_left_hand") or event.is_action_pressed("move_right_hand"):
		var index := 0 if event.is_action_pressed("move_left_hand") else 1
		if index != active:
			_switch_hand()
	elif event.is_action_pressed("level_up") or event.is_action_pressed("level_down"):
		var up := event.is_action_pressed("level_up")
		var tool := held_tool(active)
		if tool and tool.def.action == "syringe":
			Surgery.current.tools.request_plunger(active, -1 if up else 1)
		elif uses_level(active):
			hand.level = clampi(hand.level + (1 if up else -1), 0, 3)
	elif event.is_action_pressed("zoom"):
		zoom = (zoom + 1) % ZOOM_FOV.size()
	elif event.is_action_pressed("use_tool"):
		# One button lowers the tool and fires its single action (a clamp pinches, the defibrillator charges).
		hand.lowered = true
		hand.trigger = true
	elif event.is_action_released("use_tool"):
		hand.lowered = false
		hand.trigger = false
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


## The hand the mouse moves right now (its key held), or -1 while the mouse looks around.
func moving_hand() -> int:
	if Input.is_action_pressed("move_left_hand"):
		return 0
	return 1 if Input.is_action_pressed("move_right_hand") else -1


## The wheel sets this hand's effort level: its tool takes one (ToolActions.LEVEL_NAMES). It can be set before
## lowering the tool, so a blade goes in at the depth picked.
func uses_level(hand: int) -> bool:
	var tool := held_tool(hand)
	return tool != null and ToolActions.LEVEL_NAMES.has(tool.def.action)


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
	if is_local:
		_camera.fov = lerpf(_camera.fov, ZOOM_FOV[zoom], minf(delta * 12.0, 1.0))
		_frame_needle(delta)
	# The camera pitches fully, the visible head only half as much so it doesn't look broken-necked.
	_face.rotation.x = -pitch * 0.5
	_animate_body(delta)
	for i in 2:
		var tool := held_tool(i)
		hands[i].holding = tool != null
		var uid := tool.uid if tool else 0
		if uid != _held_uid[i]:
			_held_uid[i] = uid
			hands[i].level = 0
		hands[i].grip = tool.def.grip if tool else "pencil"
		hands[i].fit = Db.grip_fit(tool.def, i) if tool else {}
		hands[i].soak(tool.blood if tool else 0.0, delta)
		hands[i].update_pose(shoulder(i), delta)
	_stain_scrubs(delta)


## Zoomed all the way in with a syringe or IV catheter, the camera moves over beside it so the needle and what it's in
## (a vial, the dish, the arm's vein) are both in view, and the hands fade so they don't block it.
## The hand rolls a syringe so its printed scale faces the camera, and rolls it back after.
func _frame_needle(delta: float) -> void:
	var tool := held_tool(active)
	var framing := zoom == ZOOM_FOV.size() - 1 and tool != null and tool.def.action in NEEDLE_ACTIONS and not hands[active].inspecting
	var before := _needle_framing
	_needle_framing = move_toward(_needle_framing, 1.0 if framing else 0.0, delta * 4.0)
	if _needle_framing != before:
		for hand in hands:
			hand.set_see_through(_needle_framing * NEEDLE_SEE_THROUGH)
	if framing:
		_needle_view = needle_view(tool)
	if framing and tool.def.action == "syringe":
		if _rolled_hand != active:
			_rolled_hand = active
			_own_twist = hands[active].twist
		var facing := hands[active].twist_facing(_needle_view.origin - ToolManager.middle(tool))
		hands[active].twist = lerp_angle(hands[active].twist, facing, minf(delta * 8.0, 1.0))
	elif _rolled_hand >= 0:
		var hand := hands[_rolled_hand]
		hand.twist = lerp_angle(hand.twist, _own_twist, minf(delta * 8.0, 1.0)) if _needle_framing > 0.0 else _own_twist
		if _needle_framing <= 0.0:
			_rolled_hand = -1
	if _needle_framing <= 0.0:
		_camera.transform = Transform3D.IDENTITY
		return
	_camera.global_transform = _head.global_transform.interpolate_with(_needle_view, smoothstep(0.0, 1.0, _needle_framing))


## Where the camera looks at a syringe or catheter from: side on and a little above, from the side the eyes are on, far
## enough back that the whole tool and the vial, dish or bag its needle is in fit the view.
func needle_view(tool: SurgicalTool) -> Transform3D:
	var points: Array[Vector3] = [tool.global_position, tool.tip_position()]
	var target := ToolActions.needle_target(tool, Surgery.current.patient)
	if target.kind == "container":
		points.append(ToolManager.middle(target.container))
	var center := Vector3.ZERO
	for p in points:
		center += p / points.size()
	var radius := 0.0
	for p in points:
		radius = maxf(radius, p.distance_to(center))
	var along := (points[1] - points[0]) * Vector3(1, 0, 1)
	var side := along.cross(Vector3.UP).normalized() if along.length() > 0.001 else _head.global_basis.z
	if side.dot(_head.global_position - center) < 0.0:
		side = -side
	var direction := side * cos(NEEDLE_VIEW_ELEVATION) + Vector3.UP * sin(NEEDLE_VIEW_ELEVATION)
	var distance := (radius + NEEDLE_VIEW_MARGIN) / tan(deg_to_rad(ZOOM_FOV[zoom]) * 0.5)
	return Transform3D(Basis.IDENTITY, center + direction * distance).looking_at(center, Vector3.UP)


func _local_update(delta: float) -> void:
	_switch_timer = maxf(_switch_timer - delta, 0.0)
	var can_act := not input_locked and not status.is_out()
	crouch = move_toward(crouch, 1.0 if can_act and Input.is_action_pressed("crouch") else 0.0, delta * 4.0)
	var dir := Input.get_vector("move_left", "move_right", "move_forward", "move_back") if can_act else Vector2.ZERO
	var speed := lerpf(WALK_SPEED, CROUCH_SPEED, crouch) * status.move_speed()
	var move := global_basis * Vector3(dir.x, 0, dir.y) * speed
	velocity = Vector3(move.x, velocity.y - 9.8 * delta if not is_on_floor() else 0.0, move.z)
	move_and_slide()
	var hand := hands[active]
	if can_act:
		var tilt_input := Input.get_axis("tilt_back", "tilt_forward")
		var twist_input := Input.get_axis("twist_left", "twist_right")
		hand.tilt = clampf(hand.tilt - tilt_input * delta * 1.5, SurgeonHand.TILT_RANGE.x, SurgeonHand.TILT_RANGE.y)
		hand.twist = wrapf(hand.twist + twist_input * delta * 2.0, -PI, PI)
		hand.lifted = Input.is_action_pressed("lift") and not hand.attached
		if hand.attached and Input.is_action_pressed("lift"):
			hand.target.y += PULL_SPEED * delta
		status.holding_breath = Input.is_action_pressed("steady") and status.breath > 0.0
	for i in 2:
		var h := hands[i]
		var tool := held_tool(i)
		h.inspecting = can_act and i == active and tool != null and not h.attached and Input.is_action_pressed("inspect")
		if h.inspecting:
			# Held up in front of the eyes, the grip off to the hand's side so the whole tool crosses the view.
			h.lowered = false
			h.trigger = false
			var side := 1.0 if i == 1 else -1.0
			h.target = _camera.global_transform * Vector3(side * tool.def.length * 0.5, -0.04, -INSPECT_DISTANCE)
			_strain[i] = false
			continue
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
	_update_hover()
	_handle_status_events(status.update(delta, _status_context()))


func _move_hand(hand: SurgeonHand, delta_local: Vector3, vertical: bool = false) -> void:
	var yaw_basis := Basis(Vector3.UP, rotation.y)
	var step := yaw_basis * delta_local if not vertical else delta_local
	hand.target += step
	var from := shoulder(hand.index)
	hand.target = _in_reach(hand.target, from)
	if not hand.attached:
		hand.local_target = to_local(hand.target)


## p kept between MIN_REACH and REACH from the shoulder at `from`.
func _in_reach(p: Vector3, from: Vector3) -> Vector3:
	var out := p - from
	if out.length() < 0.001:
		out = -global_basis.z * 0.001
	return from + out.normalized() * clampf(out.length(), MIN_REACH, REACH)


## Rests the tool tip just above whatever is under it. Lowered onto skin, it touches and presses in by effort level.
## Then keeps the hand within reach: far out, or down at the floor while standing, it stops short in the air.
func _constrain(hand: SurgeonHand) -> void:
	var tool := held_tool(hand.index)
	var offset := hand.tip_offset(tool.def.length) if tool else Vector3(0, -0.03, 0)
	# A hand holding onto something keeps its height; Lift pulls it up (see _local_update()).
	var surface := {"y": -INF} if hand.attached else _surface_below(hand.target + offset)
	var from := shoulder(hand.index)
	hand.on_hard = false
	if surface.y != -INF:
		if surface.open:
			hand.target.y = surface.y + HOVER_GAP - offset.y
		elif hand.lowered and surface.soft:
			# Skin gives: a lowered tip presses into it, deeper with effort.
			hand.target.y = surface.y - 0.002 - hand.level * 0.004 - offset.y
		elif tool:
			# Anything hard (a tray, the table, a tool lying there) doesn't: every corner of the tool clears
			# whatever is under that corner, not only its tip.
			var gap := 0.001 if hand.lowered else HOVER_GAP
			hand.on_hard = true
			var basis := hand.grip_transform().basis
			var needed: float = surface.y + gap - _lowest_point(hand, tool)
			for i in 8:
				var corner := basis * tool.bounds.get_endpoint(i)
				var under: Dictionary = _surface_below(hand.target + corner)
				if under.y != -INF and not under.open:
					needed = maxf(needed, float(under.y) + gap - corner.y)
			hand.target.y = needed
		else:
			hand.target.y = surface.y + HOVER_GAP - offset.y
		# Too far down to reach (the floor while standing): carry the hand instead of stretching for it.
		if hand.target.y < from.y - REACH * 0.9:
			hand.target.y = global_position.y + CARRY_HEIGHT - crouch * CROUCH_DROP
		# The hand and the end of the forearm stay out of whatever is under them (a leg, the table edge):
		# the hand rises instead, lifting the tool tip off if it has to.
		for point: Vector3 in [hand.target, from.lerp(hand.target, 0.75)]:
			var under: Dictionary = _surface_below(point)
			if under.y != -INF and not under.open:
				hand.target.y += maxf(float(under.y) + HAND_CLEARANCE - point.y, 0.0)
		# Fingers wrapped round a tool can reach below the hand's middle: they stay out of hard surfaces too.
		var under_hand: Dictionary = _surface_below(hand.target)
		if under_hand.y != -INF and not under_hand.open and not under_hand.soft:
			hand.target.y += maxf(float(under_hand.y) + 0.002 - (hand.target.y + hand.glove_drop), 0.0)
	hand.target = _in_reach(hand.target, from)
	if not hand.attached:
		hand.local_target = to_local(hand.target)


## How far below the hand the lowest point of its tool is (negative: below), the way the hand holds it now.
static func _lowest_point(hand: SurgeonHand, tool: SurgicalTool) -> float:
	var basis := hand.grip_transform().basis
	var lowest := INF
	for i in 8:
		lowest = minf(lowest, (basis * tool.bounds.get_endpoint(i)).y)
	return lowest


## {"y": surface height under p (or -INF), "open": true when p is over an opened incision, "soft": skin}
## Over an opening it finds what's inside: organs and targets, or the cavity floor.
## Rests on the patient's real skin (PatientBody.SURFACE_LAYER), the table, trays, tools lying there and the floor.
func _surface_below(p: Vector3) -> Dictionary:
	var space := get_world_3d().direct_space_state
	var query := PhysicsRayQueryParameters3D.create(p + Vector3.UP * 0.35, p + Vector3.DOWN * 2.0, 4)
	# An opening is looked for on the site plane first: the skin mesh around it would hide it from above.
	var site_hit := space.intersect_ray(query)
	if not site_hit.is_empty():
		var body := Surgery.current.patient.body
		if body.is_open(body.world_to_uv(site_hit.position)):
			query.collision_mask = PatientBody.CAVITY_LAYER | CavityTarget.LAYER
			var inside := space.intersect_ray(query)
			return {"y": inside.position.y if not inside.is_empty() else site_hit.position.y - 0.1, "open": true, "soft": true}
	# Tools lying about count too: set down on top of one, not into it (the two would be shoved apart, through the tray).
	query.collision_mask = 1 | 4 | PatientBody.SURFACE_LAYER | Drape.DRAPE_LAYER | SurgicalTool.TOOL_LAYER
	var hit := space.intersect_ray(query)
	if hit.is_empty():
		return {"y": -INF, "open": false, "soft": false}
	var soft := ((hit.collider as CollisionObject3D).collision_layer & (4 | PatientBody.SURFACE_LAYER | Drape.DRAPE_LAYER)) != 0
	# The body's collider is its rest shape: skin lifted by a grip lies above it. The site's own collider is a flat
	# plane over the site, above skin that curves away under it (a belly): there the skin as drawn now is what's touched.
	if not site_hit.is_empty():
		var body := Surgery.current.patient.body
		var local := body.site.to_local(p)
		var uv := body.world_to_uv(p)
		var skin := body.site.to_global(Vector3(local.x, body.skin_height(uv), local.z))
		if skin.y > hit.position.y or (hit.collider as Node).has_meta("site") and body.on_body(uv):
			return {"y": skin.y, "open": false, "soft": true}
	return {"y": hit.position.y, "open": false, "soft": soft}


## The tool the active hand would pick up: the free tool nearest the hand's tip, highlighted with its name shown.
func _update_hover() -> void:
	var hand := hands[active]
	var near: SurgicalTool = null
	if held_tool(active) == null and not input_locked:
		near = Surgery.current.tools.nearest_grabbable(hand.global_position + hand.tip_offset(0.05))
	if near != hovered:
		if is_instance_valid(hovered):
			hovered.set_highlight(false)
		if near:
			near.set_highlight(true)
		hovered = near


func _switch_hand() -> void:
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
		hand.lowered = false
		hand.trigger = false
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
		hands[active].lowered = false
		Surgery.current.tools.request_release(active, _jolt * 10.0)
		Surgery.current.hud.toast("It slips out of your hand!")


func drop_everything() -> void:
	for i in 2:
		hands[i].lowered = false
		hands[i].trigger = false
		if held_tool(i):
			Surgery.current.tools.request_release(i, Vector3.ZERO)


func _update_focus() -> void:
	var from := _camera.global_position
	var query := PhysicsRayQueryParameters3D.create(from, from - _camera.global_basis.z * INTERACT_RANGE, Interactable.LAYER)
	query.collide_with_areas = true
	query.collide_with_bodies = false
	var hit := get_world_3d().direct_space_state.intersect_ray(query)
	focused = hit.collider as Interactable if not hit.is_empty() else null
	if focused and not focused.offered_to(self):
		focused = null


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
		hand_data.append([h.effective_position(), h.tilt, h.twist, h.lowered, h.trigger, h.level, h.lifted, h.inspecting])
	return [global_position, rotation.y, pitch, active, hand_data, _strain, status.is_out(), crouch]


@rpc("authority", "call_remote", "unreliable_ordered")
func _sync_state(data: Array) -> void:
	_net_position = data[0]
	_net_yaw = data[1]
	pitch = data[2]
	active = data[3]
	_remote_out = data[6]
	crouch = data[7]
	for i in 2:
		var h := hands[i]
		var d: Array = data[4][i]
		h.target = d[0]
		h.tilt = d[1]
		h.twist = d[2]
		h.lowered = d[3]
		h.trigger = d[4]
		h.level = d[5]
		h.lifted = d[6]
		h.inspecting = d[7]
	if multiplayer.is_server():
		var strain: Array = data[5]
		for i in 2:
			if strain[i]:
				Surgery.current.overstretched(peer_id, i)


## The sink or a fresh pair takes the blood off the gloves. The scrubs keep their stains.
@rpc("any_peer", "call_local", "reliable")
func clean_gloves() -> void:
	if Net._sender() not in [1, peer_id]:
		return
	for hand in hands:
		hand.set_blood(0.0)


## Host tells the owner a hand is now holding onto something (or let go). Attached hands don't follow the body.
@rpc("any_peer", "call_local", "reliable")
func set_hand_attached(hand: int, value: bool) -> void:
	if Net._sender() != 1:
		return
	hands[hand].attached = value
	if not value:
		hands[hand].local_target = to_local(hands[hand].target)
