class_name SurgeonHand
extends Node3D
## One hand and its arm. The arm is solved with analytic two-bone IK from shoulder to hand every frame.
## Position is owned by the surgeon's peer and synced to everyone; the host uses it to drive tools.

const UPPER_ARM := 0.34
const FOREARM := 0.34
const TILT_RANGE := Vector2(-1.5, -0.2)
## How far the wrist turns the tool left and right of straight ahead (radians).
const TURN_RANGE := 0.9
## The tilt a glove is fitted onto its tool at (see _place_glove()). Tilted or turned from there, both turn together.
const REST_TILT := -1.1
const LIFT_HEIGHT := 0.12
## Seconds of game time the hand's speed is measured over.
const SPEED_WINDOW := 0.25
const FINGERS: PackedStringArray = ["Index", "Middle", "Ring", "Pinky", "Thumb"]
## Radians each finger joint bends at full curl, knuckle first.
const JOINT_BEND: PackedFloat32Array = [0.9, 1.2, 0.8]

## How a hand holds each kind of tool (ToolDef.grip), in the tool's frame (grip at the origin, tip toward -Z).
## basis: the glove's axes (fingers, back of the hand, pinky side) in tool space, for the right hand.
## at: the glove point (glove model space) that sits on `on`, a point of the tool.
## curl: how far each finger closes (index, middle, ring, pinky, thumb) while holding.
## oppose: radians the thumb swings across the palm toward the index, for pinching (none if missing).
const GRIPS: Dictionary = {
	# Pinched between the thumb pad on top and the curled index tip under it, the thumb swung across to meet the index.
	# The back of the tool runs up between the thumb and index into the web, the other fingers curled under it.
	"pencil": {
		"basis": Basis(Vector3(0.243, 0.527, -0.815), Vector3(0.0, 0.84, 0.543), Vector3(0.97, -0.132, 0.204)),
		"at": Vector3(0.1, -0.056, -0.031), "on": Vector3(0.0, 0.0, 0.0),
		"curl": [0.75, 0.95, 1.0, 1.0, 0.3], "oppose": 0.35,
	},
	# Thumb and ring finger through the rings at the back, index laid along the shaft.
	"rings": {
		"basis": Basis(Vector3(0.0, 0.2, -0.98), Vector3(0.0, 0.98, 0.2), Vector3(1.0, 0.0, 0.0)),
		"at": Vector3(0.1, -0.024, 0.0), "on": Vector3(0.0, 0.0, 0.055),
		"curl": [0.2, 0.7, 0.8, 0.85, 0.6],
	},
	# A needle holder crosses the player's view while its curved needle hangs toward the skin. The inward turn keeps
	# the hand and rings behind the working end instead of stacking them over the aim point.
	"needle": {
		"basis": Basis(Vector3(0.0, 0.2, -0.98), Vector3(0.0, 0.98, 0.2), Vector3(1.0, 0.0, 0.0)),
		"at": Vector3(0.1, -0.024, 0.0), "on": Vector3(0.0, 0.0, 0.055),
		"curl": [0.2, 0.7, 0.8, 0.85, 0.6], "tilt": -0.28, "work_tilt": REST_TILT, "turn": 0.55,
	},
	# Wrapped around a handle that runs across the palm, thumb toward the tip, knuckles on top.
	"fist": {
		"basis": Basis(Vector3(0.0, -1.0, 0.0), Vector3(1.0, 0.0, 0.0), Vector3(0.0, 0.0, 1.0)),
		"at": Vector3(0.06, -0.026, 0.0), "on": Vector3(0.0, 0.0, 0.0),
		"curl": [1.0, 1.0, 1.0, 1.0, 0.8],
	},
	# Pinched at the back edge between thumb and fingertips, palm down over it.
	"flat": {
		"basis": Basis(Vector3(0.0, 0.25, -0.97), Vector3(0.0, 0.97, 0.25), Vector3(1.0, 0.0, 0.0)),
		"at": Vector3(0.1, -0.03, 0.0), "on": Vector3(0.0, 0.004, 0.0),
		"curl": [0.55, 0.55, 0.6, 0.65, 0.5],
	},
}

var index := 0
## Where the hand wants to be, world space, before tremor and lift.
var target := Vector3.ZERO
## Target relative to the surgeon, used while not attached so the hand moves with the body.
var local_target := Vector3.ZERO
## The held tool's angles, the hand turning with it: tilt pitches the tip down (TILT_RANGE), turn swings it left and
## right of the body's facing (TURN_RANGE), twist rolls it about its own length.
var tilt := REST_TILT
var turn := 0.0
var twist := 0.0
## Use tool held: the tool rests on its spot instead of hovering over it.
var lowered := false
## Use tool held, for tools with a single action (ToolActions.TRIGGER_NAMES): on press, while held, on release.
var trigger := false
## Effort level 0..3 from the wheel (cut depth, stitch tension, heat, plunger...), see ToolActions.LEVEL_NAMES.
var level := 0
var lifted := false
var attached := false
## Held up in front of the eyes, turned across the view with its markings toward them (reading a syringe).
var inspecting := false
var tremor := Vector3.ZERO
## A twitch of the glove alone (the tool and the hand's position stay put), set by the surgeon while stress is low.
var shiver := Vector3.ZERO
## Afterimages following the glove (0 none, 1 strongest), set on the local view while a sedative works.
var trail := 0.0
## Set by the surgeon while the held tool rests on something hard (a tray, the table): the tremor can't push it in.
var on_hard := false
var speed := 0.0
## Remote copies receive the final position already including lift and tremor.
var puppet := false
## How bloody the glove is (0..1). Every peer soaks it from the held tool's synced blood, so it matches everywhere.
var blood := 0.0

var _lift := 0.0
## Recent [game time, position] samples. Speed over a short window ignores tremor and network jitter.
## Game time, not the wall clock: physics frames run back to back after a stall would read as a burst of speed.
var _history: Array = []
var _clock := 0.0
## Set by the surgeon each frame; drives how far the fingers curl.
var holding := false
## ToolDef.grip of the tool in this hand, set with `holding`. Empty hands follow the forearm, open.
var grip := "pencil"
## How this hand's grip is fitted to the tool it holds (data/grips.json, made by tests/support/fit_grips.tscn), so the tool
## doesn't pass through the glove: "lift" moves the glove off the tool toward the back of the hand and "shift" toward
## the pinky side, so the tool sits more in the web of the thumb (meters); "curl" replaces the grip's finger curl.
## Empty: the grip as it is.
var fit: Dictionary = {}
var _curl := 0.2
## [curl, grip, holding] the fingers were last posed for.
var _posed: Array = []
var _glove: Node3D
var _glove_rig: BoneRig
var _glove_materials: Array[ShaderMaterial] = []
## The toon shader's coat runs back from a tip at -Z, the glove's fingers point along +X.
const COAT_FRAME := Transform3D(Basis(Vector3.FORWARD, Vector3.UP, Vector3.RIGHT), Vector3.ZERO)
## Wrist to fingertips along the glove's X.
const GLOVE_LENGTH := 0.19
## About how thick a finger is around its bones, and the palm above and below its bone.
const FINGER_RADIUS := 0.009
const PALM_HALF_THICKNESS := 0.014
## Glove blood gained per second while the held tool is bloodier than the glove.
const SOAK_RATE := 0.15
## How far (meters) the forearm reaches past the wrist into the glove's cuff, so the cuff never shows as an open end.
const CUFF_DEPTH := 0.06
## Where the glove's cuff bone points at rest (glove model space): back from the wrist (tools/blender/hand.py).
const CUFF_REST := Vector3(-1.0, 0.0, 0.0)
## Most a held tool's angle in the fingers gives way to keep the wrist straight (radians). More and the fingers swing
## into the tool.
const MAX_TOOL_TIP := 0.3
## Most the hand turns about the tool to face the elbow (radians), from the grip's own pose with the back of the hand up.
const MAX_ROLL := 0.45
## How much a held tool's direction decides where the elbow goes, against the arm hanging down and out (0..1).
const FOREARM_PULL := 0.7
## How far under the shoulder a held tool can raise the elbow (meters).
const ELBOW_BELOW_SHOULDER := 0.1
## Empty hand: the glove point (glove model space) at the hand's position, the hollow of the fingers.
const GRIP_POINT := Vector3(0.07, -0.028, 0.0)
var _upper: Node3D
var _fore: Node3D
var _pusher: AnimatableBody3D
## Fraction of the forearm hidden at the elbow end (local player only, keeps the view clear).
var _forearm_start := 0.0
## Stands in for the hand's materials while it's see-through (see set_see_through()).
const GHOST_COLOR := Color(0.75, 0.82, 0.9)
var _ghost: StandardMaterial3D
## The glove's afterimages: how many, how far apart in time (s), the most see-through, their color, how far (m) from
## the glove a copy has to be to show, and the glove frames and bone poses the copies show (newest first).
const TRAIL_COPIES := 4
const TRAIL_STEP := 0.06
const TRAIL_ALPHA := 0.45
const TRAIL_COLOR := Color(0.5, 0.65, 0.95)
const TRAIL_GAP := 0.01
var _trail_root: Node3D
var _trail_frames: Array = []
var _trail_acc := 0.0


func build(hand_index: int, scrubs: ShaderMaterial) -> void:
	index = hand_index
	name = "LeftHand" if index == 0 else "RightHand"
	var sleeve := {"tint": scrubs}
	_glove = ModelSlot.instantiate("surgeon", "glove", self)
	_glove_materials = ModelSlot.own_materials(_glove)
	for mat in _glove_materials:
		mat.set_shader_parameter("coat_length", GLOVE_LENGTH)
	# The glove is modeled wrist at the origin, fingers along +X, palm facing -Y, thumb toward -Z.
	# Empty, it follows the forearm (see _place_glove()). Holding a tool, it sits on the tool by its grip (GRIPS).
	_glove.top_level = true
	_glove_rig = BoneRig.find(_glove)
	_upper = ModelSlot.instantiate("surgeon", "upper_arm", self, sleeve)
	_fore = ModelSlot.instantiate("surgeon", "forearm", self, sleeve)
	for segment in [_upper, _fore]:
		segment.top_level = true
	_pusher = AnimatableBody3D.new()
	_pusher.collision_layer = PatientBody.PUSHER_LAYER
	_pusher.collision_mask = 0
	var shape := CollisionShape3D.new()
	var sphere := SphereShape3D.new()
	sphere.radius = 0.025
	shape.shape = sphere
	_pusher.add_child(shape)
	_pusher.top_level = true
	add_child(_pusher)


## The local player sees forearms and gloves only; the upper arm would sit right under the camera.
func hide_upper_arm() -> void:
	_upper.visible = false
	_forearm_start = 0.2
	var cuff := _fore.find_child("Cuff", true, false) as Node3D
	if cuff:
		cuff.visible = false


## Fades the glove and sleeve (0 solid, 1 gone) so the hand doesn't hide what it works on. Set on the local view only.
## A plain see-through material stands in for the hand's own while it's faded: the compatibility renderer ignores
## GeometryInstance3D.transparency, and the ink outline would draw the hand's inside.
func set_see_through(amount: float) -> void:
	if _ghost == null:
		_ghost = StandardMaterial3D.new()
		_ghost.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		_ghost.albedo_color = GHOST_COLOR
	_ghost.albedo_color.a = 1.0 - amount
	for node in find_children("*", "GeometryInstance3D", true, false):
		if not (_trail_root and _trail_root.is_ancestor_of(node)):
			(node as GeometryInstance3D).material_override = _ghost if amount > 0.0 else null


## Final world position: target plus lift and tremor.
func effective_position() -> Vector3:
	if puppet:
		return target
	return target + Vector3(0, _lift, 0) + (Vector3(tremor.x, maxf(tremor.y, 0.0), tremor.z) if on_hard else tremor)


func grip_transform() -> Transform3D:
	if inspecting:
		return Transform3D(inspect_basis(), global_position)
	return Transform3D(_tool_basis(lowered), global_position)


func _tool_basis(working: bool) -> Basis:
	var yaw := (get_parent() as Node3D).global_rotation.y + turn
	var pitch := tilt
	if working:
		pitch = minf(pitch, float(GRIPS.get(grip, GRIPS.pencil).get("work_tilt", pitch)))
	return Basis(Vector3.UP, yaw) * Basis(Vector3.RIGHT, pitch) * Basis(Vector3.FORWARD, twist)


## The neutral tool angles for this grip. Asymmetric turns point a tool from either hand in toward the work.
func default_tilt() -> float:
	return float(GRIPS.get(grip, GRIPS.pencil).get("tilt", REST_TILT))


func default_turn() -> float:
	var inward := 1.0 if index == 1 else -1.0
	return float(GRIPS.get(grip, GRIPS.pencil).get("turn", 0.0)) * inward


## The twist that turns the held tool's underside (-Y, the side a syringe's scale is printed on) toward `direction`.
## Twist rolls the tool about its own length, so its tip stays where it is.
func twist_facing(direction: Vector3) -> float:
	var yaw := (get_parent() as Node3D).global_rotation.y + turn
	var local := (Basis(Vector3.UP, yaw) * Basis(Vector3.RIGHT, tilt)).inverse() * direction
	return atan2(-local.x, -local.y)


## A tool held up to look at: its tip across the view toward the other hand's side, and its top (+Y, where the back
## of the hand is in every grip) turned away from the eyes, so the hand is behind the tool and doesn't hide it.
func inspect_basis() -> Basis:
	var eyes := (get_parent() as Surgeon).camera().global_basis.orthonormalized()
	var along := eyes.x * (1.0 if index == 1 else -1.0)
	var top := -eyes.z
	return Basis(top.cross(along), top, along)


func tip_offset(tool_length: float) -> Vector3:
	return grip_transform().basis * Vector3(0, 0, -tool_length)


func update_pose(shoulder: Vector3, delta: float) -> void:
	_lift = move_toward(_lift, LIFT_HEIGHT if lifted else 0.0, delta * 0.8)
	global_position = effective_position()
	_track_speed(delta)
	global_basis = grip_transform().basis
	_pusher.global_position = global_position
	_solve_arm(shoulder)
	_animate_fingers(delta)
	glove_drop = _glove_lowest() - global_position.y
	_update_trail(delta)
	# The blood coat is drawn in the glove's own frame, so it has to follow the glove around.
	if blood > 0.0:
		for mat in _glove_materials:
			mat.set_shader_parameter("coat_inverse", Projection(COAT_FRAME * _glove.global_transform.affine_inverse()))


## How far below the hand's position the glove reaches as it's posed now (negative: below). The surgeon keeps that
## clear of tables and trays, not only the hand's middle.
var glove_drop := -0.03


## Poses the hand at once, fingers already closed as far as they go (no easing in). For fitting and checking grips.
func snap_pose(shoulder: Vector3) -> void:
	_curl = (1.1 if lowered or trigger else 1.0) if holding else 0.15
	update_pose(shoulder, 0.0)
	_pose_fingers()


func _glove_lowest() -> float:
	var lowest := global_position.y - 0.03
	for point: Array in bone_points():
		lowest = minf(lowest, (point[0] as Vector3).y - float(point[1]))
	return lowest


## Points inside the glove as it's posed now (world space), each with how thick the glove is around it:
## [[position, radius], ...]: along the finger bones and through the palm, so nothing solid should be at them.
## part: "Palm" or a finger (FINGERS) for just that part, empty for all of it.
func bone_points(part: String = "") -> Array:
	var points: Array = []
	if _glove_rig == null:
		return points
	var skeleton := _glove_rig.skeleton
	for i in skeleton.get_bone_count():
		var bone := skeleton.get_bone_name(i)
		if bone in ["Hand", "Cuff"] or part and not bone.begins_with(part):
			continue
		var at := skeleton.global_transform * skeleton.get_bone_global_pose(i).origin
		var children := skeleton.get_bone_children(i)
		var to := skeleton.global_transform * skeleton.get_bone_global_pose(children[0]).origin if not children.is_empty() else at
		for t: float in [0.0, 0.5, 1.0]:
			points.append([at.lerp(to, t), FINGER_RADIUS])
	if part and part != "Palm":
		return points
	# The palm is wide and flat: a grid through it, glove model space (fingers +X, back of the hand +Y).
	for i in 7:
		for j in 5:
			for y: float in [-PALM_HALF_THICKNESS * 0.5, 0.0, PALM_HALF_THICKNESS * 0.5]:
				var at := Vector3(0.085 * i / 6.0, y, -0.024 + 0.012 * j)
				points.append([_glove.global_transform * at, PALM_HALF_THICKNESS * 0.5])
	return points


## Blood works its way from a bloody tool onto the fingers, then the palm. It never drips off on its own.
func soak(tool_blood: float, delta: float) -> void:
	if tool_blood * 0.8 > blood:
		set_blood(minf(blood + SOAK_RATE * delta, tool_blood * 0.8))


func set_blood(amount: float) -> void:
	blood = amount
	for mat in _glove_materials:
		mat.set_shader_parameter("coat", amount)
		mat.set_shader_parameter("coat_reach", GLOVE_LENGTH * (0.3 + amount))


func _track_speed(delta: float) -> void:
	_clock += delta
	_history.append([_clock, global_position])
	while _history.size() > 2 and _clock - float(_history[0][0]) > SPEED_WINDOW:
		_history.pop_front()
	var oldest: Array = _history[0]
	var span := maxf(_clock - float(oldest[0]), 0.016)
	speed = global_position.distance_to(oldest[1]) / span


## Relaxed when empty, closed around a held tool as its grip says, squeezed a little tighter while using it.
func _animate_fingers(delta: float) -> void:
	var target := (1.1 if lowered or trigger else 1.0) if holding else 0.15
	_curl = move_toward(_curl, target, delta * 4.0)
	_pose_fingers()


## Bends the finger bones for the current curl. Posing 15 bones only matters while it changes, a fraction of the time.
func _pose_fingers() -> void:
	var pose: Array = [_curl, grip, holding, fit]
	if _glove_rig == null or pose == _posed:
		return
	_posed = pose
	var style: Dictionary = GRIPS.get(grip, GRIPS.pencil)
	var amounts: Array = fit.get("curl", style.curl) if holding else [1.0, 1.0, 1.0, 1.0, 1.0]
	# The thumb swings across under the index from its root, as far as the fingers are closed.
	var oppose := float(style.get("oppose", 0.0)) * minf(_curl, 1.0) if holding else 0.0
	for f in FINGERS.size():
		var finger := FINGERS[f]
		for joint in 3:
			var bone := "%s%d" % [finger, joint + 1]
			# Each joint bends its bone toward the palm (the glove's -Y); the thumb folds in less.
			var bend := minf(_curl * float(amounts[f]), 1.0) * JOINT_BEND[joint] * (0.55 if finger == "Thumb" else 1.0)
			var axis := _glove_rig.direction(bone).cross(Vector3.DOWN).normalized()
			var turn := Basis(axis, bend)
			if bone == "Thumb1":
				turn = Basis(Vector3.DOWN, oppose) * turn
			_glove_rig.rotate(bone, turn)


func _solve_arm(shoulder: Vector3) -> void:
	var to_hand := global_position - shoulder
	var dist := clampf(to_hand.length(), 0.05, UPPER_ARM + FOREARM - 0.001)
	var dir := to_hand.normalized()
	var along := (UPPER_ARM * UPPER_ARM - FOREARM * FOREARM + dist * dist) / (2.0 * dist)
	var height := sqrt(maxf(UPPER_ARM * UPPER_ARM - along * along, 0.0))
	var side := -1.0 if index == 0 else 1.0
	var owner_basis := (get_parent() as Node3D).global_basis
	var pole := (owner_basis.x * side * 0.6 + Vector3.DOWN).normalized()
	# Reaching straight along the pole (down and out) leaves no bend direction: bend the elbow back instead.
	if absf(pole.dot(dir)) > 0.98:
		pole = owner_basis.z
	pole = (pole - dir * pole.dot(dir)).normalized()
	var elbow := shoulder + dir * along + pole * height
	if holding:
		# The held tool sets which way the wrist points: the elbow goes toward the forearm's line from there, so the
		# wrist doesn't bend over backwards when the hand comes close. Only as far as it stays under the shoulder.
		# The tool as it's held before the wrist aims it: aiming bends the wrist, the elbow stays.
		_place_glove(elbow, owner_basis, false)
		var line := _glove.global_position - _glove.global_basis.x.normalized() * FOREARM - shoulder
		var toward := line - dir * line.dot(dir)
		if toward.length() > 0.001:
			for pull: float in [FOREARM_PULL, FOREARM_PULL * 0.75, FOREARM_PULL * 0.5, FOREARM_PULL * 0.25]:
				var bent := shoulder + dir * along + (toward.normalized() * pull + pole * (1.0 - pull)).normalized() * height
				if bent.y < shoulder.y - ELBOW_BELOW_SHOULDER:
					elbow = bent
					break
	_place_segment(_upper, shoulder, elbow)
	var wrist := _place_glove(elbow, owner_basis)
	var cuff_end := wrist + _aim_cuff(elbow, wrist) * CUFF_DEPTH
	_place_segment(_fore, elbow.lerp(cuff_end, _forearm_start), cuff_end)


## Aims the glove's cuff bone down the forearm and returns that direction (world space). However the wrist bends,
## the cuff stays on the sleeve and the glove stretches from the hand over it.
func _aim_cuff(elbow: Vector3, wrist: Vector3) -> Vector3:
	var back := (elbow - wrist).normalized()
	if _glove_rig == null or not _glove_rig.has("Cuff"):
		return back
	# The cuff bone starts at the wrist, where its parent does, so BoneRig.direction() can't tell where it points.
	var aim := (_glove.global_basis.inverse() * back).normalized()
	var axis := CUFF_REST.cross(aim)
	_glove_rig.rotate("Cuff", Basis(axis.normalized(), CUFF_REST.angle_to(aim)) if axis.length() > 0.0001 else Basis.IDENTITY)
	return back


## Places the glove and returns the wrist (the glove's origin). The left glove is the right one mirrored.
## Holding a tool, the glove sits on it by its grip, fitted as if the tool weren't tilted or turned (REST_TILT, no
## turn), then turned along with it (unless not `turned`): the wrist aims the tool, the tool doesn't move in the hand.
## Empty, it points along the forearm, palm down.
func _place_glove(elbow: Vector3, owner_basis: Basis, turned: bool = true) -> Vector3:
	if holding:
		var style: Dictionary = GRIPS.get(grip, GRIPS.pencil)
		var aimed := grip_transform()
		var yaw := (get_parent() as Node3D).global_rotation.y
		var rest_tilt := float(style.get("tilt", REST_TILT))
		var tool_frame := Transform3D(Basis(Vector3.UP, yaw) * Basis(Vector3.RIGHT, rest_tilt) * Basis(Vector3.FORWARD, twist), aimed.origin)
		var aim := aimed.basis * tool_frame.basis.inverse() if turned else Basis.IDENTITY
		var mirror := Vector3(-1, 1, 1) if index == 0 else Vector3.ONE
		var contact := tool_frame * ((style.on as Vector3) * mirror)
		var frame := tool_frame.basis * Basis.from_scale(mirror) * (style.basis as Basis)
		frame = _turn_to_forearm(frame, tool_frame.basis * Vector3.FORWARD, contact, elbow, grip != "fist")
		var wrist := contact - frame * (style.at as Vector3) + frame.y.normalized() * float(fit.get("lift", 0.0)) + frame.z.normalized() * float(fit.get("shift", 0.0))
		wrist = aimed.origin + aim * (wrist - aimed.origin) + shiver
		_glove.global_transform = Transform3D(aim * frame, wrist)
		return wrist
	var along := (global_position - elbow).normalized()
	var up := (Vector3.UP - along * Vector3.UP.dot(along)).normalized()
	if up.length_squared() < 0.5:
		up = owner_basis.z
	var side := along.cross(up)
	var frame := Basis(along, up, side if index == 1 else -side)
	var wrist := global_position - frame * GRIP_POINT + shiver
	_glove.global_transform = Transform3D(frame, wrist)
	return wrist


## Turns a held glove about the tool so the wrist faces the elbow, without letting go:
## first it rolls around the tool's own axis (the hand wraps the same way, just from another side),
## then, for grips that allow it, it tips the tool up to MAX_TOOL_TIP against the fingers, like changing a pen angle.
func _turn_to_forearm(frame: Basis, axis: Vector3, contact: Vector3, elbow: Vector3, can_tip: bool) -> Basis:
	var to_elbow := (elbow - contact).normalized()
	var wrist_dir := -frame.x.normalized()
	# Only as far as a forearm turns: turned further, the hand ends up palm up and twisted outward.
	var roll := clampf(_signed_angle(wrist_dir - axis * wrist_dir.dot(axis), to_elbow - axis * to_elbow.dot(axis), axis), -MAX_ROLL, MAX_ROLL)
	frame = Basis(axis, roll) * frame
	if can_tip:
		wrist_dir = -frame.x.normalized()
		var pivot := wrist_dir.cross(to_elbow)
		if pivot.length() > 0.001:
			frame = Basis(pivot.normalized(), minf(wrist_dir.angle_to(to_elbow), MAX_TOOL_TIP)) * frame
	return frame


static func _signed_angle(from: Vector3, to: Vector3, axis: Vector3) -> float:
	if from.length() < 0.0001 or to.length() < 0.0001:
		return 0.0
	return from.signed_angle_to(to, axis)


static func _place_segment(mesh: Node3D, a: Vector3, b: Vector3) -> void:
	var y := (b - a)
	var length := y.length()
	if length < 0.001:
		return
	y /= length
	var x := y.cross(Vector3.FORWARD if absf(y.dot(Vector3.FORWARD)) < 0.9 else Vector3.RIGHT).normalized()
	var z := x.cross(y)
	mesh.global_transform = Transform3D(Basis(x, y * length, z), (a + b) * 0.5)


## Copies of the glove trail behind it, each showing where the glove was a step earlier, fainter the older it is.
func _update_trail(delta: float) -> void:
	if trail <= 0.0:
		if _trail_root:
			_trail_root.queue_free()
			_trail_root = null
			_trail_frames.clear()
		return
	if _trail_root == null:
		_build_trail()
	var skeleton := _glove_rig.skeleton if _glove_rig else null
	_trail_acc += delta
	if _trail_acc >= TRAIL_STEP or _trail_frames.is_empty():
		_trail_acc = 0.0
		var poses: Array[Transform3D] = []
		for i in (skeleton.get_bone_count() if skeleton else 0):
			poses.append(skeleton.get_bone_pose(i))
		_trail_frames.push_front([_glove.global_transform, poses])
		_trail_frames.resize(mini(_trail_frames.size(), TRAIL_COPIES + 1))
	# The newest frame is about where the glove is now: the copies show the ones before it. A copy right on the glove
	# (a hand held still) would only speckle it.
	for i in _trail_root.get_child_count():
		var copy := _trail_root.get_child(i) as Node3D
		var frame: Array = _trail_frames[i + 1] if i + 1 < _trail_frames.size() else []
		copy.visible = not frame.is_empty() and (frame[0] as Transform3D).origin.distance_to(_glove.global_position) > TRAIL_GAP
		if not copy.visible:
			continue
		copy.global_transform = frame[0]
		var copy_skeleton := copy.get_meta("skeleton") as Skeleton3D
		if copy_skeleton:
			var poses: Array[Transform3D] = frame[1]
			for bone in poses.size():
				copy_skeleton.set_bone_pose(bone, poses[bone])
		var material := copy.get_meta("material") as StandardMaterial3D
		material.albedo_color.a = trail * TRAIL_ALPHA * (1.0 - float(i) / TRAIL_COPIES)


func _build_trail() -> void:
	_trail_root = Node3D.new()
	_trail_root.name = "Trail"
	add_child(_trail_root)
	for i in TRAIL_COPIES:
		var copy := _glove.duplicate() as Node3D
		copy.top_level = true
		_trail_root.add_child(copy)
		var material := StandardMaterial3D.new()
		material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
		material.albedo_color = TRAIL_COLOR
		copy.set_meta("material", material)
		for node in copy.find_children("*", "GeometryInstance3D", true, false):
			(node as GeometryInstance3D).material_override = material
		var skeletons := copy.find_children("*", "Skeleton3D", true, false)
		copy.set_meta("skeleton", skeletons[0] if not skeletons.is_empty() else null)
