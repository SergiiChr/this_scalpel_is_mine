class_name Surgeon
extends CharacterBody3D
## A player in the room. The owning peer reads input, moves the body and hands, and streams its state.
## Everyone else (including the host) sees a puppet that follows the stream.
##
## Controls: the mouse looks around, holding a hand's key (Q/E) moves that hand instead and makes it the active one.
## Use tool (LMB, held) rests the active hand's tool on its spot and works it, the wheel sets its effort level
## (see ToolActions.LEVEL_NAMES and TRIGGER_NAMES). A syringe has its own wheel: down pulls the plunger out, up pushes it
## in, 1 ml a notch, with or without Use tool held. Grab (RMB) picks up and puts down. Zoom (Shift) toggles
## between two zoom levels; the closer one with a syringe or IV catheter in hand fades the hands, and once a syringe's
## needle is in with Use tool held it frames the needle and what it's in.
## Holding Aim tool (MMB) the mouse turns the active hand's tool instead, the wrist with it: pitch and swing left and
## right. C/V roll it about its length.
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
## Radians the held tool turns per pixel while the mouse aims it (Aim tool held).
const AIM_SENSITIVITY := 0.004
## Gap between a resting tool tip and the surface under it.
const HOVER_GAP := 0.01
## The same for a needle (a syringe, the IV catheter): its tip sits on the aim.
const NEEDLE_HOVER := 0.002
## A tool tip at most this far above a surface shows the aim on it, where Use tool brings it down (see aim_point()).
const AIM_DROP := 0.1
## How far above what's inside an opening (meters) a lowered blade stays at each effort level: at full effort it goes
## all the way down to it, close enough to grate on a bone (Patient.BLADE_REACH), short of cutting an organ.
const BLADE_IN_OPENING: Array[float] = [HOVER_GAP, 0.008, 0.005, 0.002]
## Holding Lift while holding onto something pulls it up this fast (m/s): slow and steady, so nothing rips.
const PULL_SPEED := 0.05
## Room between the hand (or forearm) and the surface under it: about half a hand's thickness.
const HAND_CLEARANCE := 0.03
## Where hands hang when nothing within reach is under them (the floor while standing): about waist height.
const CARRY_HEIGHT := 1.05
## Crouching lowers eyes and shoulders this much and slows walking to a careful step.
const CROUCH_DROP := 0.75
const CROUCH_SPEED := 0.35
## Zoom steps, cycled by the zoom key: camera field of view, widest first. Hand motion scales with the magnification,
## so the hand crosses the screen as fast at every step.
const ZOOM_FOV: Array[float] = [70.0, 35.0]
## How far in front of the eyes a tool is held up to look at it (Inspect).
const INSPECT_DISTANCE := 0.26
## Zoomed all the way in with a syringe whose needle is in, the camera looks at it from the side and this far above
## (radians), with this much room around the syringe and its target (meters). Zoomed in with a needle in hand at all,
## the hands are this see-through (see _frame_needle()).
const NEEDLE_VIEW_ELEVATION := 0.6
const NEEDLE_VIEW_MARGIN := 0.025
const NEEDLE_SEE_THROUGH := 0.65
## Tools the last zoom step fades the hands for: a syringe and the IV catheter.
const NEEDLE_ACTIONS: PackedStringArray = ["syringe", "iv_line"]
## A syringe's needle in the patient (Use tool held) keeps its tip where it went in: the mouse only tilts the syringe
## about it. A pull it can't follow (sideways, or past how far the hand tilts) stretches the skin by this share of the
## motion, and stretched this far (meters) the needle tears out and leaves a small wound.
const NEEDLE_DRAG := 0.2
const NEEDLE_TEAR := 0.015
## Seconds Use tool is held before the needle counts as in: the hand comes down onto the skin first.
const NEEDLE_SETTLE := 0.1
## A syringe whose tip comes this close (meters, across the floor) to the middle of the IV bag on the stand, or of a
## vial, snaps its needle into it; it lets go this much further out (see _snap_spot()). Snapping in or out takes this
## long (seconds).
const DRIP_SNAP := 0.12
const VIAL_SNAP := 0.03
## A vial's cap faces a surgeon when it points up this much (standing, the share of straight up), or lying, it points
## at them this much (the cosine of how far off it points across the floor).
const VIAL_UPRIGHT := 0.7
const VIAL_FACING := 0.5
## Furthest a syringe's tip may be above or below a vial's cap (meters) to snap into it.
const VIAL_ABOVE := 0.06
const UNSNAP_MARGIN := 0.015
const SNAP_TIME := 0.25
## A syringe snapped into the IV bag points this far up (radians): the bag hangs high and the forearm rises to it, so
## level or lower the wrist would bend back.
const DRIP_TILT := 0.5
## A syringe's thumb press sits this far behind its finger grip when empty, and the plunger pulls out this share of the
## syringe's length when full (tools/assetgen/instruments.py builds them so).
const SYRINGE_PRESS := 0.017
const SYRINGE_TRAVEL := 0.62 * 0.85
## How a syringe picked up is held (radians): tilted this far down and turned this far in toward the body's middle.
const SYRINGE_TILT := -0.6
const SYRINGE_TURN := 0.4
## Seconds Grab is held on a bottle to stand it upright where it is instead of putting it down.
const STAND_HOLD := 1.0
## Fastest a syringe's hand rises or sinks to follow what's under it (m/s): it glides over a vial's edge, not hops.
const SYRINGE_GLIDE := 0.45
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
## Knocked out, the surgeon tips over sideways from the feet and lies on their side, facing the table: the body
## model this far up off the floor (half the shoulders' width), the hands on the floor in front of the chest (local,
## for a fall to the left), and the camera's pitch, enough to see the table from the floor. On the way down they
## stagger back to this far (m) from the middle of the table, walls allowing, so the table isn't right overhead.
const LYING_LIFT := 0.2
const LYING_HAND := Vector3(-0.8, 0.05, -0.25)
const LYING_PITCH := 0.3
const LYING_DISTANCE := 1.4
## How much floor (m) a falling surgeon looks for beside them, to pick the side they fall to.
const FALL_ROOM := 2.0
## Close enough to a glove's middle (m) for a needle to be in the hand; around the spine for it to be in the body.
const GLOVE_REACH := 0.05
const TORSO_RADIUS := 0.16
## Where the spine runs in the body model (local, standing): hips to neck.
const SPINE: Array[Vector3] = [Vector3(0.0, 0.95, 0.0), Vector3(0.0, 1.45, 0.0)]

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
## How long Grab has been held on a bottle (seconds), -1 when it isn't: held STAND_HOLD, the bottle is stood upright.
var _stand_hold := -1.0
## The zoom step before Use tool zoomed in on a syringe's needle, to go back to when it's let go (-1: it didn't).
var _zoom_before := -1
## Uid of the tool each hand held last frame: a new tool starts at effort level 0.
var _held_uid: Array[int] = [0, 0]
## How far the hands have faded for the last zoom step with a needle in hand (0..1), how far the camera has moved over
## to the needle view (0..1), and the last needle view, to move back from.
var _needle_fade := 0.0
var _needle_framing := 0.0
var _needle_view := Transform3D.IDENTITY
## The hand whose syringe the needle view rolls to show its scale to the camera (-1: none). Out of it, the syringe turns
## its scale back to the eyes (see _physics_process()).
var _rolled_hand := -1
## Where the active hand's needle tip went into the patient (INF: it isn't in), how far the skin around it is pulled,
## and whether it tore out since Use tool was pressed (it then moves freely until Use tool is let go).
var _needle_anchor := Vector3.INF
var _needle_pull := Vector3.ZERO
var _needle_torn := false
var _needle_pressed := 0.0
## The hand the needle view leaves solid: the one the needle is going into (-1: none).
var _solid_hand := -1
## Hands whose syringe is snapped into a vial or the IV bag, or easing in or out: hand index -> {"own": the hand's own
## tilt and turn, to give back after, "weight": how far snapped (0..1), "on": still over it, and where it snaps to,
## as _snap_spot() gives it}.
var _snaps: Dictionary = {}
## Each hand's own tilt, turn and twist while it holds a syringe (see _face_syringe()), to give back after.
var _unfaced: Dictionary = {}

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
## How far over onto the floor a knocked out surgeon has gone (0 standing, 1 lying).
var _down := 0.0
## The side a knocked out surgeon falls to (1 their left, -1 their right, 0 standing), synced.
var _fall_side := 0.0
## Mouse moves held back by a sedative: [msec when due, hand, step].
var _delayed: Array = []
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
## A tool hovering over something shows the spot right under its tip: Use tool lowers the hand straight down, so that's
## where the tip lands, whatever angle the tool is held at and wherever the eyes look from.
func aim_point() -> Vector3:
	var hand := hands[active]
	var tool := held_tool(active)
	var tip := tool.tip_position() if tool else hand.global_position + hand.tip_offset(0.05)
	# A grip with a working angle (a needle holder) pitches about its tip as it's lowered: the tip is where it works.
	if tool == null or hand.lowered or SurgeonHand.GRIPS.get(tool.def.grip, {}).has("work_tilt"):
		return tip
	var under := _surface_below(tip)
	if tool.def.action in NEEDLE_ACTIONS:
		under = _glove_below(hand, tip, under)
	if float(under.y) < tip.y and tip.y - float(under.y) < AIM_DROP:
		tip.y = under.y
	return tip


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
	var side := -1.0 if hand == 0 else 1.0
	var standing := to_global(Vector3(SHOULDER.x * side, SHOULDER.y - crouch * CROUCH_DROP, SHOULDER.z))
	# Lying, the shoulders are where the fallen body has them.
	return standing.lerp(_body.global_transform * Vector3(SHOULDER.x * side, SHOULDER.y, SHOULDER.z), _down)


## Whether this surgeon is knocked out, the same on every peer.
func is_down() -> bool:
	return _fall_side != 0.0


## Where a needle at `tip` would go into this surgeon: {"part": "hand", "at": the glove's middle} or {"part": "body",
## "at": tip}, or {} if it's in neither. `holding` is the hand with the needle: one of this surgeon's own leaves only
## the other hand to go into.
func needle_part(tip: Vector3, holding: SurgeonHand) -> Dictionary:
	for hand in hands:
		if hand != holding and hand.global_position.distance_to(tip) < GLOVE_REACH:
			return {"part": "hand", "at": hand.global_position}
	if holding in hands:
		return {}
	var hips := _body.global_transform * SPINE[0]
	var neck := _body.global_transform * SPINE[1]
	if Geometry3D.get_closest_point_to_segment(tip, hips, neck).distance_to(tip) < TORSO_RADIUS:
		return {"part": "body", "at": tip}
	return {}


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
	var out := status.passed_out > 0.0 if is_local else _remote_out
	_collapse = move_toward(_collapse, 1.0 if out else 0.0, delta * 2.5)
	if is_local:
		_fall_side = (_fall_side if _fall_side != 0.0 else _roomier_side()) if status.is_knocked_out() else 0.0
	_down = move_toward(_down, 1.0 if is_down() else 0.0, delta * 1.5)
	# Crouching folds the legs forward and drops the whole body, the torso leaning over the knees.
	_pose("LegL", Vector3(sin(_walk_phase) * stride - crouch * 1.3, 0, 0))
	_pose("LegR", Vector3(-sin(_walk_phase) * stride - crouch * 1.3, 0, 0))
	_pose("Torso", Vector3(-_collapse * 1.3 - crouch * 0.35 + absf(sin(_walk_phase)) * stride * 0.05, 0, 0))
	# Knocked out, the whole body tips over sideways from the feet onto the floor; the eyes go where its head lies.
	var side := _fall_side if _fall_side != 0.0 else 1.0
	_body.rotation.z = side * _down * PI / 2.0
	_body.position.y = -crouch * 0.42 + _down * LYING_LIFT
	var standing_eyes := Vector3(0.0, EYE_HEIGHT - _collapse * 1.1 - crouch * CROUCH_DROP, 0.0)
	_head.position = standing_eyes.lerp(_body.transform * Vector3(0.0, EYE_HEIGHT, -0.06), _down)
	# Lying there, the head turns to the patient on the table.
	var look := 0.0
	if _down > 0.0:
		var to_patient := (Surgery.current.patient.global_position - to_global(_head.position)) * Vector3(1, 0, 1)
		look = wrapf(atan2(-to_patient.x, -to_patient.z) - rotation.y, -PI, PI) * _down
	_head.rotation.y = look


## The side (1 left, -1 right) with more clear floor beside the surgeon, to fall to.
func _roomier_side() -> float:
	var space := get_world_3d().direct_space_state
	var from := to_global(Vector3(0.0, 0.3, 0.0))
	var room := {}
	for side: float in [1.0, -1.0]:
		var query := PhysicsRayQueryParameters3D.create(from, to_global(Vector3(-side * FALL_ROOM, 0.3, 0.0)), 1, [get_rid()])
		var hit := space.intersect_ray(query)
		room[side] = FALL_ROOM if hit.is_empty() else from.distance_to(hit.position)
	return 1.0 if room[1.0] >= room[-1.0] else -1.0


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
		if Input.is_action_pressed("aim_tool"):
			aim_tool(motion)
		elif moving_hand() < 0:
			rotation.y -= motion.x * LOOK_SENSITIVITY
			pitch = clampf(pitch - motion.y * LOOK_SENSITIVITY, LOOK_PITCH.x, LOOK_PITCH.y)
		elif _switch_timer <= 0.0:
			# Sedated, the move reaches the hand late (see _apply_delayed()).
			var delay := status.input_delay()
			if delay > 0.0:
				_delayed.append([Time.get_ticks_msec() + int(delay * 1000.0), active, motion])
			else:
				steer_hand(motion)
	elif event.is_action_pressed("move_left_hand") or event.is_action_pressed("move_right_hand"):
		var index := 0 if event.is_action_pressed("move_left_hand") else 1
		if index != active:
			_switch_hand()
	elif event.is_action_pressed("level_up") or event.is_action_pressed("level_down"):
		var up := event.is_action_pressed("level_up")
		var tool := held_tool(active)
		if tool and tool.def.action == "syringe":
			Surgery.current.tools.request_plunger(active, -1 if up else 1)
		elif tool and tool.def.action == "sew":
			# As on a syringe, the wheel works the tool itself: down pulls the thread tight, up pays more out.
			Surgery.current.tools.request_suture_tension(active, 1 if up else -1)
		elif tool and tool.def.action == "spread":
			# Up opens the spreader, down closes it, set in a wound or not.
			Surgery.current.tools.request_spread(active, 1 if up else -1)
		elif uses_level(active):
			hand.level = clampi(hand.level + (1 if up else -1), 0, 3)
	elif event.is_action_pressed("zoom"):
		zoom = (zoom + 1) % ZOOM_FOV.size()
	elif event.is_action_pressed("use_tool"):
		# One button lowers the tool and fires its single action (a clamp pinches, the defibrillator charges).
		_set_lowered(hand, true)
		hand.trigger = true
		var tool := held_tool(active)
		if tool and tool.def.action == "syringe" and _zoom_before < 0:
			# Putting a needle in zooms all the way in until Use tool is let go (the needle view, see _frame_needle()).
			_zoom_before = zoom
			zoom = ZOOM_FOV.size() - 1
	elif event.is_action_released("use_tool"):
		_set_lowered(hand, false)
		hand.trigger = false
		_end_needle_zoom()
	elif event.is_action_pressed("grab"):
		var tool := held_tool(active)
		if tool and tool.def.tray == "bottles" and not hand.attached:
			# A bottle: let go quickly it's put down as anything is, held on it's stood upright (see _local_update()).
			_stand_hold = 0.0
		else:
			_grab_or_release()
	elif event.is_action_released("grab") and _stand_hold >= 0.0:
		_stand_hold = -1.0
		_grab_or_release()
	elif event.is_action_pressed("interact") and focused:
		focused.interact(self)
	elif event.is_action_pressed("drink"):
		Surgery.current.request_drink(active)
	else:
		for i in MAX_BELT:
			if event.is_action_pressed("belt_%d" % (i + 1)):
				Surgery.current.tools.request_belt(active, i)


## Back to the zoom step from before Use tool zoomed in on a syringe's needle, if it did.
func _end_needle_zoom() -> void:
	if _zoom_before >= 0:
		zoom = _zoom_before
		_zoom_before = -1


## Moves a hand (the active one unless `index` says) by a mouse motion (pixels, sensitivity applied). Zoomed in, the
## same motion moves it as much less as the view is magnified, so it crosses the screen at the same speed: finer
## control where you're looking closely. A needle stuck in the patient tilts about its tip instead.
func steer_hand(motion: Vector2, index: int = -1) -> void:
	index = active if index < 0 else index
	var magnified := tan(deg_to_rad(ZOOM_FOV[zoom]) * 0.5) / tan(deg_to_rad(ZOOM_FOV[0]) * 0.5)
	var step := motion * HAND_SENSITIVITY * status.hand_speed() * magnified
	var move := _screen_to_floor(step) if _needle_framing > 0.5 else Basis(Vector3.UP, rotation.y) * Vector3(step.x, 0, step.y)
	if index == active and _needle_anchor != Vector3.INF:
		_bend_needle(move)
	else:
		_move_hand(hands[index], move)


## A hand move (world, across the floor) with the needle stuck: along the syringe it tilts it about its tip, the hand
## swinging round it. What the tilt can't take (sideways, or past its range) pulls on the skin.
func _bend_needle(move: Vector3) -> void:
	var hand := hands[active]
	var length := held_tool(active).def.length
	var back := Basis(Vector3.UP, rotation.y + hand.turn) * Vector3.BACK
	var along := move.dot(back)
	var tilt_before := hand.tilt
	hand.tilt = clampf(hand.tilt + along / length, SurgeonHand.TILT_RANGE.x, SurgeonHand.TILT_RANGE.y)
	var unbent := along - (hand.tilt - tilt_before) * length
	_needle_pull += (move - back * along + back * unbent) * NEEDLE_DRAG


## Changes a tool's working pose about its tip, so pressing it never moves the aim point away from the skin. Most grips
## have no working angle and need no correction; the horizontal needle grip pitches down while the hand stays clear.
func _set_lowered(hand: SurgeonHand, value: bool) -> void:
	if hand.lowered == value:
		return
	var tool := held_tool(hand.index)
	var before := hand.tip_offset(tool.def.length) if tool else Vector3.ZERO
	hand.lowered = value
	var shift := before - hand.tip_offset(tool.def.length) if tool else Vector3.ZERO
	# Only a grip that pitches as it's lowered moves the hand. The hand's own spot moves by as much, not to where the
	# hand is held now (a syringe snapped into a vial would lose its snap).
	if not shift.is_zero_approx():
		hand.target += shift
		if not hand.attached:
			hand.local_target += global_basis.inverse() * shift


## Turns the active hand's tool by a mouse motion, the wrist going with it: up and down pitches it, left and right
## swings it. It turns about the grip; a needle stuck in the patient turns about its tip instead (see _hold_needle()).
## Snapped into a vial or the bag, the snap holds the angles: the turn goes to the hand's own, for when it lets go.
func aim_tool(motion: Vector2) -> void:
	var hand := hands[active]
	var state: Dictionary = _snaps.get(active, {})
	var own: Vector2 = state.get("own", Vector2(hand.tilt, hand.turn))
	own.x = clampf(own.x - motion.y * AIM_SENSITIVITY, SurgeonHand.TILT_RANGE.x, SurgeonHand.TILT_RANGE.y)
	own.y = clampf(own.y - motion.x * AIM_SENSITIVITY, -SurgeonHand.TURN_RANGE, SurgeonHand.TURN_RANGE)
	if state.is_empty():
		hand.tilt = own.x
		hand.turn = own.y
	else:
		state.own = own


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
	# The camera pitches fully, the visible head only half as much so it doesn't look broken-necked. Lying on the
	# side, the face lies sideways with the body; the camera doesn't roll.
	_face.rotation = Vector3(-pitch * 0.5 * (1.0 - _down), 0.0, _fall_side * _down * PI / 2.0)
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
			if tool and tool.def.grip == "needle":
				hands[i].tilt = hands[i].default_tilt()
				hands[i].turn = hands[i].default_turn()
			if is_local:
				_face_syringe(i, tool)
		if is_local and _unfaced.has(i) and _rolled_hand != i:
			# Moved about, it keeps turning its scale to the eyes.
			var facing := hands[i].twist_facing(_camera.global_position - hands[i].global_position)
			hands[i].twist = lerp_angle(hands[i].twist, facing, minf(delta * 8.0, 1.0))
		# The thumb rides the plunger's press: behind the finger grip by the press's own offset plus the pull.
		hands[i].press = SYRINGE_PRESS + tool.def.length * SYRINGE_TRAVEL * (tool.ml + tool.air) / tool.def.volume if tool and tool.def.action == "syringe" else NAN
		hands[i].soak(tool.blood if tool else 0.0, delta)
		hands[i].update_pose(shoulder(i), delta)
	_stain_scrubs(delta)


## Picked up, a syringe is held ready to inject: a little down and pointing in toward the body's middle (SYRINGE_TILT,
## SYRINGE_TURN), the hand off to its outer side, its printed scale toward the eyes so it doesn't need turning to be
## read (and kept there while held, see _physics_process()). `tool`: what the hand now holds.
func _face_syringe(hand: int, tool: SurgicalTool) -> void:
	_unface(hand)
	if tool and tool.def.action == "syringe":
		var h := hands[hand]
		_unfaced[hand] = Vector3(h.tilt, h.turn, h.twist)
		h.tilt = SYRINGE_TILT
		h.turn = SYRINGE_TURN * (1.0 if hand == 1 else -1.0)
		h.twist = h.twist_facing(_camera.global_position - h.global_position)
		# Snapped already this frame (beside a vial), it eases in from these angles, not the last tool's.
		_snaps.erase(hand)


## A hand that let go of a syringe holds things the way it did before it took it.
func _unface(hand: int) -> void:
	if _unfaced.has(hand):
		var own: Vector3 = _unfaced[hand]
		hands[hand].tilt = own.x
		hands[hand].turn = own.y
		hands[hand].twist = own.z
		_unfaced.erase(hand)
		# Snapped into a vial or the bag as it went, it has nothing to ease back from.
		_snaps.erase(hand)


## Zoomed all the way in with a syringe or IV catheter, the hands fade so they don't hide where the needle goes. The
## camera stays at the eyes, so the mouse moves the hand the way it always does while aiming.
## Once a syringe's needle is in something with Use tool held, the camera moves over beside it so the needle and what
## it's in (a vial, the dish, the bag, the arm's vein) are both in view, and the hand rolls the syringe so its printed
## scale faces the camera. Use tool let go, both go back.
func _frame_needle(delta: float) -> void:
	var tool := held_tool(active)
	var zoomed := zoom == ZOOM_FOV.size() - 1 and tool != null and tool.def.action in NEEDLE_ACTIONS and not hands[active].inspecting
	var target := ToolActions.needle_target(tool, Surgery.current.patient) if zoomed else {}
	var framing: bool = zoomed and tool.def.action == "syringe" and hands[active].lowered and target.kind != "air"
	var faded := _needle_fade
	_needle_fade = move_toward(_needle_fade, 1.0 if zoomed else 0.0, delta * 4.0)
	_needle_framing = move_toward(_needle_framing, 1.0 if framing else 0.0, delta * 4.0)
	# The other hand stays solid while the needle is in it.
	var solid := 1 - active if target.get("peer", 0) == peer_id else -1
	if _needle_fade != faded or solid != _solid_hand:
		_solid_hand = solid
		for hand in hands:
			hand.set_see_through(0.0 if hand.index == solid else _needle_fade * NEEDLE_SEE_THROUGH)
	_rolled_hand = active if framing else -1
	if framing:
		_needle_view = needle_view(tool)
		var facing := hands[active].twist_facing(_needle_view.origin - ToolManager.middle(tool))
		hands[active].twist = lerp_angle(hands[active].twist, facing, minf(delta * 8.0, 1.0))
	if _needle_framing <= 0.0:
		_camera.transform = Transform3D.IDENTITY
		return
	_camera.global_transform = _head.global_transform.interpolate_with(_needle_view, smoothstep(0.0, 1.0, _needle_framing))


## A mouse move as seen through the camera, laid flat on the floor: right moves right on screen, up moves away.
## The needle view looks from the side, so moving the hand the body's way would look sideways there.
func _screen_to_floor(step: Vector2) -> Vector3:
	var right := _camera.global_basis.x * Vector3(1, 0, 1)
	var away := _camera.global_basis.y * Vector3(1, 0, 1)
	if away.length() < 0.1:
		away = -_camera.global_basis.z * Vector3(1, 0, 1)
	return right.normalized() * step.x - away.normalized() * step.y


## Where the camera looks at a syringe or catheter from: side on and a little above (square on to it, however it's
## tilted), from the side the eyes are on, far enough back that the whole tool and the vial, dish or bag its needle is in fit the view.
func needle_view(tool: SurgicalTool) -> Transform3D:
	var points: Array[Vector3] = [tool.global_position, tool.tip_position()]
	var target := ToolActions.needle_target(tool, Surgery.current.patient)
	if target.kind == "container":
		points.append(ToolManager.middle(target.container))
	elif target.kind == "surgeon" and target.part == "hand":
		points.append(target.at)
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
	# Square on to the syringe however it's tilted: one standing in a vial is seen from level, not from above.
	var axis := (points[1] - points[0]).normalized()
	direction = (direction - axis * direction.dot(axis)).normalized()
	var distance := (radius + NEEDLE_VIEW_MARGIN) / tan(deg_to_rad(ZOOM_FOV[zoom]) * 0.5)
	return Transform3D(Basis.IDENTITY, center + direction * distance).looking_at(center, Vector3.UP)


func _local_update(delta: float) -> void:
	_switch_timer = maxf(_switch_timer - delta, 0.0)
	var can_act := not input_locked and not status.is_out()
	_apply_delayed(can_act)
	if status.is_knocked_out():
		_lie_still(delta)
		_handle_status_events(status.update(delta, _status_context()))
		return
	crouch = move_toward(crouch, 1.0 if can_act and Input.is_action_pressed("crouch") else 0.0, delta * 4.0)
	var dir := Input.get_vector("move_left", "move_right", "move_forward", "move_back") if can_act else Vector2.ZERO
	var speed := lerpf(WALK_SPEED, CROUCH_SPEED, crouch) * status.move_speed()
	var move := global_basis * Vector3(dir.x, 0, dir.y) * speed
	velocity = Vector3(move.x, velocity.y - 9.8 * delta if not is_on_floor() else 0.0, move.z)
	move_and_slide()
	var hand := hands[active]
	if can_act:
		# A syringe keeps its scale to the eyes on its own (_face_syringe()): it doesn't roll.
		if not _unfaced.has(active):
			var twist_input := Input.get_axis("twist_left", "twist_right")
			hand.twist = wrapf(hand.twist + twist_input * delta * 2.0, -PI, PI)
		hand.lifted = Input.is_action_pressed("lift") and not hand.attached
		if hand.attached and Input.is_action_pressed("lift"):
			hand.target.y += PULL_SPEED * delta
		status.holding_breath = Input.is_action_pressed("steady") and status.breath > 0.0
	_hold_needle(delta)
	if not can_act:
		# Locked, in a menu or out cold, the release of Grab or Use tool may never come: a tap mustn't stand a bottle, and
		# the zoom goes back.
		_stand_hold = -1.0
		_end_needle_zoom()
	if _stand_hold >= 0.0:
		_stand_hold += delta
		if _stand_hold >= STAND_HOLD:
			_stand_hold = -1.0
			hand.lowered = false
			hand.trigger = false
			Surgery.current.tools.request_stand(active)
	for i in 2:
		var h := hands[i]
		var tool := held_tool(i)
		h.inspecting = can_act and i == active and tool != null and not h.attached and Input.is_action_pressed("inspect")
		if tool and tool.def.action == "spread":
			# Held upright, a spreader's jaws open flat across the skin whichever way it's rolled (C/V turn them).
			h.tilt = SurgeonHand.TILT_RANGE.x
		if i == active and _needle_anchor != Vector3.INF and not h.inspecting:
			# The tip stays where it went in, steady and unlifted: the hand goes wherever the tilt puts it.
			h.lifted = false
			h.target = _needle_anchor - h.tip_offset(tool.def.length)
			h.local_target = to_local(h.target)
			h.tremor = Vector3.ZERO
			_strain[i] = false
			continue
		if tool and tool.in_wound and not h.inspecting:
			_hold_in_wound(h, tool)
			continue
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
		var was := h.target.y
		var snap := _ease_snap(h, tool, delta)
		_constrain(h)
		# Over a tray, a vial or the table, a syringe glides up and down rather than hops. Onto the patient or a glove it
		# rises at once: rising slowly, the needle would sit under the skin, where the wheel injects.
		if tool and tool.def.action == "syringe" and (h.target.y < was or not _surface_below(h.target + h.tip_offset(tool.def.length)).soft):
			h.target.y = move_toward(was, h.target.y, SYRINGE_GLIDE * delta)
			if not h.attached:
				h.local_target = to_local(h.target)
		if not snap.is_empty():
			# The tip goes straight from where the hand's own way of holding it would put it to the snapped spot, the hand
			# turning round it: turned first, the tip would swing wide. The hand's own spot (local_target) stays where the
			# mouse put it, so moving on from there pulls it out again.
			var own_tip := to_global(h.local_target) + own_tip_offset(i)
			var free_tip := Vector3(own_tip.x, h.target.y + h.tip_offset(tool.def.length).y, own_tip.z)
			h.target = free_tip.lerp(snap.at, snap.weight) - h.tip_offset(tool.def.length)
		var amount := status.tremor_amount() if i == active or mods.mult("switch_delay_mult") > 0.0 else 0.0
		var t := Time.get_ticks_msec() * 0.001
		h.tremor = Vector3(sin(t * 23.0 + i), sin(t * 31.0 + 2.0 * i), cos(t * 19.0 + i)) * amount + _jolt
		h.shiver = Vector3(sin(t * 41.0 + 3.0 * i), sin(t * 37.0 + i), cos(t * 43.0 + 2.0 * i)) * status.shiver()
		h.trail = status.calm
	_jolt = _jolt.lerp(Vector3.ZERO, minf(delta * 8.0, 1.0))
	_check_bumps()
	_update_focus()
	_update_hover()
	_handle_status_events(status.update(delta, _status_context()))


## A syringe pressed into the patient sticks where its tip went in. Pulled on too hard, or walked away from out of
## reach, it tears out.
func _hold_needle(delta: float) -> void:
	var hand := hands[active]
	var tool := held_tool(active)
	if not hand.lowered or tool == null or tool.def.action != "syringe":
		_needle_torn = false
		_needle_anchor = Vector3.INF
		_needle_pressed = 0.0
	elif _needle_anchor == Vector3.INF:
		_needle_pressed += delta
		# Once in, it stays in until Use tool is let go: breathing lifting the skin doesn't free it.
		var settled := _needle_pressed >= NEEDLE_SETTLE
		if settled and not _needle_torn and ToolActions.needle_target(tool, Surgery.current.patient).kind in ["vein", "tissue"]:
			_needle_anchor = tool.tip_position()
			_needle_pull = Vector3.ZERO
	elif _needle_pull.length() > NEEDLE_TEAR or (_needle_anchor - hand.tip_offset(tool.def.length)).distance_to(shoulder(active)) > REACH:
		var pulled := _needle_pull.normalized() * NEEDLE_TEAR if _needle_pull.length() > 0.0 else Vector3.ZERO
		Surgery.current.tools.request_needle_tear(active, _needle_anchor, _needle_anchor + pulled)
		Surgery.current.hud.toast("The needle tears out of the skin.")
		_needle_torn = true
		_needle_anchor = Vector3.INF


## A spreader set in a wound stays where it went in, steady, and the hand holding it goes to it, as far as the arm
## turns that way. Walked away from out of reach, the host leaves it standing in the wound (Surgery.overstretched()).
func _hold_in_wound(hand: SurgeonHand, tool: SurgicalTool) -> void:
	var angles := tool.global_basis.get_euler(EULER_ORDER_YXZ)
	hand.turn = clampf(wrapf(angles.y - global_rotation.y, -PI, PI), -SurgeonHand.TURN_RANGE, SurgeonHand.TURN_RANGE)
	hand.tilt = clampf(angles.x, SurgeonHand.TILT_RANGE.x, SurgeonHand.TILT_RANGE.y)
	hand.twist = -angles.z
	hand.lifted = false
	hand.target = tool.global_position
	hand.tremor = Vector3.ZERO
	_strain[hand.index] = hand.target.distance_to(shoulder(hand.index)) > REACH + 0.06
	hand.target = _in_reach(hand.target, shoulder(hand.index))
	hand.local_target = to_local(hand.target)


## Mouse moves a sedative held back, once they're due. Out cold or locked, they're dropped.
func _apply_delayed(can_act: bool) -> void:
	var now := Time.get_ticks_msec()
	while not _delayed.is_empty() and (not can_act or int(_delayed[0][0]) <= now):
		var move: Array = _delayed.pop_front()
		if can_act:
			steer_hand(move[2], int(move[1]))


## Knocked out: lying on the floor facing the table, hands limp in front, nothing the player can do.
func _lie_still(delta: float) -> void:
	crouch = move_toward(crouch, 0.0, delta * 4.0)
	pitch = lerpf(pitch, LYING_PITCH, minf(delta * 3.0, 1.0))
	var table := (Surgery.current.patient.global_position - global_position) * Vector3(1, 0, 1)
	velocity = -table.normalized() * WALK_SPEED if _down < 1.0 and table.length() < LYING_DISTANCE else Vector3.ZERO
	move_and_slide()
	rotation.y = lerp_angle(rotation.y, atan2(-table.x, -table.z), minf(delta * 3.0, 1.0))
	for i in 2:
		var h := hands[i]
		h.lowered = false
		h.trigger = false
		h.lifted = false
		h.inspecting = false
		h.tremor = Vector3.ZERO
		h.shiver = Vector3.ZERO
		h.trail = 0.0
		h.target = to_global((LYING_HAND + Vector3(-0.25 * i, 0, 0)) * Vector3(_fall_side, 1, 1))
		h.local_target = to_local(h.target)


## Moves the hand by `step` (world space), within reach. A free hand moves on from its own spot, not from where
## something holds it for now (a needle snapped into the IV bag, a tool held up to look at).
func _move_hand(hand: SurgeonHand, step: Vector3) -> void:
	hand.target = (hand.target if hand.attached else to_global(hand.local_target)) + step
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
	var from := shoulder(hand.index)
	# A hand holding onto something keeps its height; Lift pulls it up (see _local_update()).
	var surface := {"y": -INF} if hand.attached else _surface_below(hand.target + offset)
	if tool and tool.def.action in NEEDLE_ACTIONS and not hand.attached:
		surface = _glove_below(hand, hand.target + offset, surface)
	hand.on_hard = false
	# A needle hovers right over what's under it, so its tip is on the aim, not a centimeter above it.
	var hover := NEEDLE_HOVER if tool and tool.def.action in NEEDLE_ACTIONS else HOVER_GAP
	if surface.y != -INF:
		if surface.open:
			# A lowered blade goes into an opening as deep as its level: onto what's inside only at full effort. Any other
			# tool lowered comes down onto what's inside (a saw onto the bone), like onto anything hard.
			var gap := hover
			if hand.lowered and tool:
				gap = BLADE_IN_OPENING[hand.level] if tool.def.action == "cut" else 0.001
			hand.target.y = surface.y + gap - offset.y
		elif hand.lowered and surface.soft:
			# Skin gives: a lowered tip presses into it, deeper with effort.
			hand.target.y = surface.y - 0.002 - hand.level * 0.004 - offset.y
		elif surface.soft and hover == NEEDLE_HOVER:
			# A needle over skin (or a glove) rests by its tip alone: its box reaches below the thin needle and would hold
			# the tip well off the aim.
			hand.target.y = surface.y + hover - offset.y
		elif tool:
			# Anything hard (a tray, the table, a tool lying there) doesn't: every corner of the tool clears
			# whatever is under that corner, not only its tip.
			var gap := 0.001 if hand.lowered else hover
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


## How far from the hand its tool's tip is at the hand's own angles, as if nothing snapped it anywhere (_ease_snap()):
## where the mouse aims it.
func own_tip_offset(hand: int) -> Vector3:
	var tool := held_tool(hand)
	var own: Vector2 = _snaps.get(hand, {}).get("own", Vector2(hands[hand].tilt, hands[hand].turn))
	return hands[hand].tip_offset_at(tool.def.length if tool else 0.05, own.x, own.y)


## Eases a hand's syringe into the vial or IV bag it's over (_snap_spot()), or back out: the hand turns the syringe from
## its own angles toward the snapped ones as it goes. Returns {"at": where the needle tip goes, "weight": how far (0..1)
## to move the hand there from its own spot}, or {} when it isn't snapped at all. Let go of, the hand gets its angles
## back at once.
func _ease_snap(hand: SurgeonHand, tool: SurgicalTool, delta: float) -> Dictionary:
	var state: Dictionary = _snaps.get(hand.index, {})
	var own: Vector2 = state.get("own", Vector2(hand.tilt, hand.turn))
	var spot := _snap_spot(hand, tool, own, state.get("on", false)) if tool else {}
	var weight := move_toward(float(state.get("weight", 0.0)), 1.0 if not spot.is_empty() else 0.0, delta / SNAP_TIME)
	if tool == null or weight <= 0.0:
		if not state.is_empty():
			hand.tilt = own.x
			hand.turn = own.y
			_snaps.erase(hand.index)
		return {}
	if not spot.is_empty():
		# From one vial straight to the next, the needle glides over rather than jumps.
		var from: Vector3 = state.get("at", spot.at)
		state.merge(spot, true)
		state.at = from.lerp(spot.at, minf(delta / SNAP_TIME, 1.0))
	state.own = own
	state.weight = weight
	state.on = not spot.is_empty()
	_snaps[hand.index] = state
	var eased := smoothstep(0.0, 1.0, weight)
	hand.tilt = lerpf(own.x, state.tilt, eased)
	hand.turn = lerp_angle(own.y, state.turn, eased)
	return {"at": state.at, "weight": eased}


## Where a syringe held at its own angles (`own`: tilt, turn) snaps to, before Use tool is pressed and after:
## {"at": the needle tip, "tilt", "turn"}, or {} when it's over nothing to snap to (or that's out of reach).
## Its tip over a vial's cap (within VIAL_SNAP across the floor), the needle goes in through it along the vial, if the
## cap faces this surgeon: up, or lying, toward them.
## Over the IV bag (within DRIP_SNAP of its middle), it goes into the middle of the bag's face on the hand's side, a
## little upward (DRIP_TILT).
## Already snapped (`held`), it lets go only UNSNAP_MARGIN further out, so passing over doesn't hold it for long.
func _snap_spot(hand: SurgeonHand, tool: SurgicalTool, own: Vector2, held: bool) -> Dictionary:
	if tool.def.action != "syringe" or hand.attached:
		return {}
	var tip := hand.target + own_tip_offset(hand.index)
	var margin := UNSNAP_MARGIN if held else 0.0
	var spot := {}
	var nearest := VIAL_SNAP + margin
	for vial: SurgicalTool in Surgery.current.tools.tools.values():
		if vial.def.action != "vial" or vial.state != SurgicalTool.State.FREE:
			continue
		# In through the cap at the vial's tip, along the vial: down into one standing, level into one lying with its cap
		# toward this surgeon. A cap facing down or away can't be lined up from here.
		var cap := vial.tip_position()
		var into := (vial.global_position - cap).normalized()
		var across := Vector2(tip.x - cap.x, tip.z - cap.z).length()
		var toward := Vector2(global_position.x - cap.x, global_position.z - cap.z).normalized()
		var facing := -into.y > VIAL_UPRIGHT or Vector2(-into.x, -into.z).normalized().dot(toward) > VIAL_FACING
		# Carried over it higher up (lifted, or at chest height), it isn't pulled down onto it.
		var above := tip.y - cap.y
		if across >= nearest or not facing or absf(above) > VIAL_ABOVE:
			continue
		var turn := own.y
		if Vector2(into.x, into.z).length() > 0.1:
			turn = wrapf(atan2(-into.x, -into.z) - rotation.y, -PI, PI)
		# Lined up past where the wrist turns, it can't be.
		if absf(turn) > SurgeonHand.TURN_RANGE:
			continue
		nearest = across
		spot = {"at": cap + into * 0.004, "tilt": asin(clampf(into.y, -1.0, 1.0)), "turn": turn}
	var bag := Surgery.current.tools.drip_bag()
	if spot.is_empty() and bag:
		var port := ToolManager.middle(bag)
		if Vector2(tip.x - port.x, tip.z - port.z).length() < DRIP_SNAP + margin:
			# The bag hangs along its length: its thinnest side across is the way through its faces.
			var face := (bag.global_basis.x if bag.bounds.size.x < bag.bounds.size.y else bag.global_basis.y) * Vector3(1, 0, 1)
			face *= signf(face.dot(hand.target - port))
			var turn := clampf(wrapf(atan2(face.x, face.z) - rotation.y, -PI, PI), -SurgeonHand.TURN_RANGE, SurgeonHand.TURN_RANGE)
			spot = {"at": port, "tilt": DRIP_TILT, "turn": turn}
	if spot.is_empty() or (spot.at - hand.tip_offset_at(tool.def.length, spot.tilt, spot.turn)).distance_to(shoulder(hand.index)) > REACH:
		return {}
	return spot


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
	var layer := (hit.collider as CollisionObject3D).collision_layer
	var soft := (layer & (4 | PatientBody.SURFACE_LAYER | Drape.DRAPE_LAYER)) != 0
	# The body's collider is its rest shape: skin lifted by a grip lies above it. The site's own collider is a flat
	# plane over the site, above skin that curves away under it (a belly), and the gown's collider isn't cut away over
	# the site: there the skin as drawn now is what's touched.
	if not site_hit.is_empty():
		var body := Surgery.current.patient.body
		var uv := body.world_to_uv(p)
		var local := body.site.to_local(p)
		var skin := body.site.to_global(Vector3(local.x, body.skin_height(uv), local.z))
		var over_site := (hit.collider as Node).has_meta("site") or (layer & PatientBody.SURFACE_LAYER) != 0
		if skin.y > hit.position.y or over_site and body.on_body(uv):
			return {"y": skin.y, "open": false, "soft": true}
	return {"y": hit.position.y, "open": false, "soft": soft}


## A needle over a glove (this surgeon's other hand or anyone's) rests on it like on skin, so it can go in.
func _glove_below(hand: SurgeonHand, p: Vector3, surface: Dictionary) -> Dictionary:
	for other: Surgeon in Surgery.current.surgeons.values():
		for glove in other.hands:
			var top := glove.global_position.y + 0.015
			var across := (glove.global_position - p) * Vector3(1, 0, 1)
			if glove != hand and across.length() < GLOVE_REACH and top > float(surface.y):
				return {"y": top, "open": false, "soft": true}
	return surface


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
	_stand_hold = -1.0
	_needle_anchor = Vector3.INF
	_switch_timer = SWITCH_DELAY * mods.mult("switch_delay_mult")


## A partner's empty hand close enough to take what this hand holds: [Surgeon, hand index], or [] if none.
## A partner knocked out on the floor takes nothing.
func pass_target(hand: int) -> Array:
	var from := hands[hand].global_position
	for other: Surgeon in Surgery.current.surgeons.values():
		if other == self or other.is_down():
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
	var tool := held_tool(active)
	# A needle held into a partner's hand or body is close on purpose.
	if mine.lifted or tool and tool.def.action == "syringe" and ToolActions.needle_target(tool, Surgery.current.patient).kind == "surgeon":
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
			"knocked_out":
				drop_everything()
				_delayed.clear()
				hud.toast("Your legs give way. The floor is very comfortable.")
				Surgery.current.report_incident("knocked_out")
			"came_round":
				hud.toast("You come round, groggy.")
			"moan":
				_moan.rpc()


# --- Networking ------------------------------------------------------------------------------------


func _pack_state() -> Array:
	var hand_data: Array = []
	for h in hands:
		hand_data.append([h.effective_position(), h.tilt, h.twist, h.lowered, h.trigger, h.level, h.lifted, h.inspecting, h.turn])
	return [global_position, rotation.y, pitch, active, hand_data, _strain, status.passed_out > 0.0, crouch, _fall_side]


@rpc("authority", "call_remote", "unreliable_ordered")
func _sync_state(data: Array) -> void:
	_net_position = data[0]
	_net_yaw = data[1]
	pitch = data[2]
	active = data[3]
	_remote_out = data[6]
	crouch = data[7]
	_fall_side = data[8]
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
		h.turn = d[8]
	if multiplayer.is_server():
		var strain: Array = data[5]
		for i in 2:
			if strain[i]:
				Surgery.current.overstretched(peer_id, i)


## Knocked out, a moan now and then, heard by everyone.
@rpc("authority", "call_local", "unreliable")
func _moan() -> void:
	Sfx.play("surgeon_moan", _head.global_position)


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
