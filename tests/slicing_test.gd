extends Node
## Close up scalpel test: drives the scalpel through the surgeon's hand like a player (lower it, pick a depth level,
## move it along the blade's edge) on intact skin of an arm, a thigh and a belly, step by step.
## After every stage it checks what should be open, saves a screenshot from straight above the cut and one from 45°
## off the side, and logs frame times.
## Run with a renderer for screenshots:
## xvfb-run -a godot --path . --rendering-method gl_compatibility res://tests/slicing_test.tscn -- --out=build/slicing
## Then a skin graft on each: a circle cut out through the skin and lifted off with forceps.
## Headless it runs the same checks without screenshots. --case=arm|thigh|belly runs one case, arm_graft and so on one
## skin graft. --fps-report prints the frame rates without checking them (run_tests.sh: they depend on the machine).

const SURGERY := preload("res://scenes/surgery.tscn")
## Each move goes this far along the blade's edge, over MOVE_TIME seconds (slow enough for a clean cut).
const MOVE := 0.02
const MOVE_TIME := 1.0
## Pressing in place is held this long.
const PRESS_TIME := 1.0
const MIN_FPS := 60.0
## fat: a layer of fat lies between skin and muscle. inside: what a cut through the muscle shows.
## along: the cut runs along the limb (the surgeon turns to face along it), not across the table toward the surgeon.
const CASES: Array[Dictionary] = [
	{"id": "arm", "scenario": "hand_stitch", "fat": false, "inside": "bone", "along": true},
	{"id": "thigh", "scenario": "leg_extension", "fat": true, "inside": "bone", "along": true},
	{"id": "belly", "scenario": "appendectomy", "fat": true, "inside": "organs", "along": false},
]
## A circle of skin cut out as a graft: its radius (meters), how far past its start the blade goes round to close it
## (radians) and how fast the hand goes round (m/s).
const GRAFT_RADIUS := 0.015
const GRAFT_OVERLAP := 0.4
const GRAFT_SPEED := 0.02
## How far (meters) the forceps lift the piece to show it pinched in place, and to take it off.
const PINCH_LIFT := 0.004
const TAKE_LIFT := 0.03
## How far from the cut (site uv) the gap is measured: severed springs lie up to half a grid cell off it.
const GAP_RADIUS := 0.03
## Most the simulated skin may lie off the body model where one takes over from the other (meters): more shows a step.
const SEAM_MAX := 0.0003
## The cameras: straight down from this far over the middle of the cut, and as far off at 45° from across the table.
## Far enough that the scalpel's handle doesn't fill the view, with a narrow lens for a close-up of the cut.
const CAMERA_DISTANCE := 0.22
const CAMERA_FOV := 20.0

var _out := "user://slicing"
var _shots := true
var _camera: Camera3D
var _surgery: Surgery
var _hand: SurgeonHand
var _scalpel: SurgicalTool
## Where the blade's tip was when the cut started and after each move (site uv), and the way it moves (uv per meter).
var _path := PackedVector2Array()
var _edge_uv := Vector2.ZERO
## The middle of the whole cut as planned, where the cameras look.
var _center_uv := Vector2.ZERO
## Fastest the hand went during the last move (m/s): over 0.25 a cut comes out jagged.
var _top_speed := 0.0
## Wall clock frame times (seconds) while the scalpel works, screenshots left out.
var _measuring := false
var _last_frame_usec := 0
var _frame_times: PackedFloat32Array = []
var _report: Array[String] = []
var _check_fps := true


func _ready() -> void:
	var only := ""
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--case="):
			only = arg.get_slice("=", 1)
		elif arg.begins_with("--out="):
			_out = arg.get_slice("=", 1)
	_shots = DisplayServer.get_name() != "headless"
	_check_fps = not OS.get_cmdline_user_args().has("--fps-report")
	DirAccess.make_dir_recursive_absolute(_out)
	_camera = Camera3D.new()
	_camera.fov = CAMERA_FOV
	_camera.near = 0.01
	add_child(_camera)
	for case in CASES:
		if only.is_empty() or case.id == only:
			await _run(case)
	for case in CASES:
		if only.is_empty() or case.id + "_graft" == only:
			await _run_graft(case)
	print("\n".join(_report))
	print("slicing_test: done")
	get_tree().quit()


func _run(case: Dictionary) -> void:
	print("--- ", case.id)
	await _start(case.scenario, case.along, case.inside == "bone")
	var fat: bool = case.fat
	var inside: String = case.inside
	var patient := _surgery.patient

	_check(patient.wounds.is_empty() and not _tissue().any_severed(), "the skin starts intact")
	_check(fat == (patient.body.fat_thickness > 0.0), "the %s has %s under the skin (%.1f mm)" % [case.id, "fat" if fat else "no fat", patient.body.fat_thickness * 1000.0])
	await _shots_of(case.id, "00_intact")

	# Lowered with no depth picked: the blade rests on the skin.
	_hand.level = 0
	_hand.lowered = true
	_hand.trigger = true
	await _hold(PRESS_TIME)
	_check(patient.wounds.is_empty() and not _tissue().any_severed(), "a lowered blade at level 0 does nothing")
	_check(_scalpel.blood < 0.05, "a blade resting on whole skin stays clean")
	await _shots_of(case.id, "01_lowered")

	# Low: the point goes through the skin.
	await _press(1)
	_path = PackedVector2Array([_tip_uv()])
	_check(_tissue().any_severed() and _deepest() == TissueSim.Depth.SKIN, "pressed at low it goes through the skin only")
	await _shots_of(case.id, "02_pressed_low")
	await _move()
	var first := _segment(0)
	_check(_open(first, TissueSim.Depth.SKIN) and not _open(first, TissueSim.Depth.FAT), "after the first 2 cm the skin is open, nothing under it is cut (%s)" % _layers(first))
	_check(_zipper(), "the incision opens in the middle and closes at its ends (%s)" % _profile())
	await _shots_of(case.id, "03_moved_low")

	# Medium: deeper, then 2 cm more.
	var before := _tip_depth()
	await _press(2)
	_check(_tip_depth() > before, "pressed at medium it digs deeper (%.4f -> %.4f m)" % [before, _tip_depth()])
	await _shots_of(case.id, "04_pressed_medium")
	await _move()
	var second := _segment(1)
	if fat:
		_check(_open(second, TissueSim.Depth.FAT) and not _open(second, TissueSim.Depth.MUSCLE), "after the second 2 cm the fat is cut and the muscle shows (%s)" % _layers(second))
	else:
		_check(_open(second, TissueSim.Depth.MUSCLE), "after the second 2 cm the muscle is cut (%s)" % _layers(second))
		_check(_under(second, "bone"), "a bone lies under the cut muscle")
	_check(_zipper(), "the incision opens in the middle and closes at its ends (%s)" % _profile())
	await _shots_of(case.id, "05_moved_medium")

	# High.
	before = _tip_depth()
	var pain := patient.vitals.pain
	var scraped: float = patient.flags.get("bone_scraped", 0.0)
	await _press(3)
	if not fat:
		_check(patient.flags.get("bone_scraped", 0.0) > scraped, "pressed at high it hits the bone")
		_check(patient.vitals.pain > pain + 0.1, "hitting the bone hurts through the local block (pain %.2f -> %.2f, block %.2f)" % [pain, patient.vitals.pain, patient.vitals.local_block])
		await _shots_of(case.id, "06_pressed_high")
	else:
		_check(_tip_depth() > before, "pressed at high it digs deeper (%.4f -> %.4f m)" % [before, _tip_depth()])
		await _shots_of(case.id, "06_pressed_high")
		await _move()
		var third := _segment(2)
		_check(_open(third, TissueSim.Depth.MUSCLE), "after the third 2 cm the muscle is cut (%s)" % _layers(third))
		_check(_under(third, inside), "%s lie under the cut muscle" % inside)
		_check(not patient.wounds.any(func(w: Wound) -> bool: return w.kind == Wound.Kind.INTERNAL), "cutting through the muscle nicks nothing inside")
		_check(_zipper(), "the incision opens in the middle and closes at its ends (%s)" % _profile())
		await _shots_of(case.id, "07_moved_high")
	_hand.lowered = false
	_hand.trigger = false
	var off := seam(_surgery.patient.body)
	_check(off < SEAM_MAX, "the simulated skin meets the body model without a step (%.2f mm off at its edge)" % (off * 1000.0))
	_report_frames(case.id)
	_surgery.queue_free()
	await _frames(3)


## A circle of skin cut out through the skin only and taken off with forceps, as a graft: screenshots halfway round,
## with the circle closed, pinched in place, and of the wound once the piece is lifted away.
func _run_graft(case: Dictionary) -> void:
	var id: String = case.id + "_graft"
	print("--- ", id)
	await _start(case.scenario, case.along, false, true)
	var patient := _surgery.patient
	var body := patient.body
	var center := body.uv_to_world(_center_uv)
	var middle := _tissue().nearest(_center_uv)
	await _shots_of(id, "00_intact")

	_hand.level = 0
	_hand.lowered = true
	_hand.trigger = true
	await _hold(PRESS_TIME)
	await _press(1)
	var edge := ToolActions.blade_direction(_scalpel)
	var across := body.site.global_basis.y.normalized().cross(edge).normalized()
	await _cut_arc(center, edge, across, 0.0, PI)
	_check(_tissue().any_severed() and _deepest() == TissueSim.Depth.SKIN, "halfway round the skin is cut, nothing under it")
	_check(_tissue().piece_of(middle).is_empty(), "halfway round the skin inside is still joined")
	await _shots_of(id, "01_half_cut")
	# A little past where it started, so the circle surely closes.
	await _cut_arc(center, edge, across, PI, TAU + GRAFT_OVERLAP)
	var piece := _tissue().piece_of(middle).size()
	_check(piece > 0 and _deepest() == TissueSim.Depth.SKIN, "the closed circle frees a piece of skin (%d grid points), cut through the skin only" % piece)
	_check(_tears() == 0, "the circle is cut cleanly: %d tears, hand at most %.3f m/s" % [_tears(), _top_speed])
	await _shots_of(id, "02_cut")

	# The scalpel is lifted out of the way and the left hand takes forceps to the middle of the piece.
	_hand.lowered = false
	_hand.trigger = false
	_hand.local_target += Vector3(0.12, 0.08, 0.1)
	var me := _surgery.local_surgeon
	var forceps: SurgicalTool = null
	for tool: SurgicalTool in _surgery.tools.tools.values():
		if tool.def.id == "forceps" and tool.state == SurgicalTool.State.FREE:
			forceps = tool
			break
	_surgery.tools._req_grab(forceps.uid, 0)
	me.active = 0
	var left := me.hands[0]
	await _reach_with(left, forceps, _center_uv)
	left.level = 1
	left.lowered = true
	await _hold(0.3)
	left.trigger = true
	await _hold(0.3)
	_check(forceps.grip_info.get("piece", false), "the forceps pinch the piece")
	await _lift(left, PINCH_LIFT, 0.3)
	_check(_tissue().piece_of(middle).size() == piece, "pinched and lifted %d mm, the piece is still in place" % roundi(PINCH_LIFT * 1000.0))
	await _shots_of(id, "03_picked_up")
	await _lift(left, TAKE_LIFT - PINCH_LIFT, 1.0)
	var graft := _surgery.tools.carried_by(forceps)
	_check(_tissue().excised.count(1) == piece, "lifted higher, the piece comes off whole")
	_check(graft != null and graft.def.id == "skin_graft" and graft.charges == 1, "the forceps hold it as a skin graft")
	# Taken well clear of the wound: up, to the side and back toward the surgeon, out of the cameras' view.
	left.local_target += Vector3(-0.15, 0.12, 0.12)
	await _hold(0.8)
	var under := "fat" if case.fat else "muscle"
	_check(body.layer_at(_center_uv) == under, "the wound shows the %s under the skin (%s)" % [under, body.layer_at(_center_uv)])
	_check(_tears() == 0 and patient.flags.has("graft_taken"), "no tears around the wound")
	var off := seam(_surgery.patient.body)
	_check(off < SEAM_MAX, "the simulated skin meets the body model without a step (%.2f mm off at its edge)" % (off * 1000.0))
	await _shots_of(id, "04_wound")
	left.trigger = false
	_report_frames(id)
	_surgery.queue_free()
	await _frames(3)


## Cuts the circle around center from `from` to `to` (radians round from where it starts), steering the blade along
## the curve like a player: the hand rolls the scalpel about its length (Rotate) so the blade keeps facing the way it
## moves, and drifts back onto the line when the point strays.
func _cut_arc(center: Vector3, edge: Vector3, across: Vector3, from: float, to: float) -> void:
	var me := _surgery.local_surgeon
	var frames := int((to - from) * GRAFT_RADIUS / GRAFT_SPEED * Engine.physics_ticks_per_second)
	_top_speed = 0.0
	for i in frames:
		var a := lerpf(from, to, float(i) / frames)
		var b := lerpf(from, to, float(i + 1) / frames)
		_steer_blade(edge * cos(b) + across * sin(b))
		var step := _circle_point(center, edge, across, b) - _circle_point(center, edge, across, a)
		var drift := (_circle_point(center, edge, across, a) - _scalpel.tip_position()) * Vector3(1, 0, 1)
		_hand.local_target += me.global_basis.inverse() * (step * Vector3(1, 0, 1) + drift * 0.3)
		await _measured_frame()
		_top_speed = maxf(_top_speed, _hand.speed)
	await _hold(0.3)


## The point of the circle `angle` round from where it starts (the side toward -across).
static func _circle_point(center: Vector3, edge: Vector3, across: Vector3, angle: float) -> Vector3:
	return center + (edge * sin(angle) - across * cos(angle)) * GRAFT_RADIUS


## Rolls the scalpel a little (as far as a hand turns it in a frame) so its blade lines up with `direction` either way.
func _steer_blade(direction: Vector3) -> void:
	var yaw := (_hand.get_parent() as Node3D).global_rotation.y
	var best := _hand.twist
	var best_dot := -1.0
	for i in range(-30, 31):
		var twist := _hand.twist + i * 0.005
		var side := Basis(Vector3.UP, yaw) * Basis(Vector3.RIGHT, _hand.tilt) * Basis(Vector3.FORWARD, twist) * Vector3.RIGHT
		var dot := absf(side.cross(Vector3.UP).normalized().dot(direction.normalized()))
		if dot > best_dot:
			best_dot = dot
			best = twist
	_hand.twist = best


## Brings `hand`, holding `tool`, over uv: its tip there, a little above the skin.
func _reach_with(hand: SurgeonHand, tool: SurgicalTool, uv: Vector2) -> void:
	var me := _surgery.local_surgeon
	var spot := _surgery.patient.body.uv_to_world(uv)
	await _frames(10)
	for i in 6:
		hand.local_target += me.global_basis.inverse() * ((spot - tool.tip_position()) * Vector3(1, 0, 1))
		await _frames(20)


## Raises a hand by `height` over `seconds`, steadily. Holding onto something, it's pulled up (Lift); once it lets go,
## it's raised off the skin instead of coming back down onto it.
func _lift(hand: SurgeonHand, height: float, seconds: float) -> void:
	var frames := int(seconds * Engine.physics_ticks_per_second)
	for i in frames:
		if hand.attached:
			hand.target.y += height / frames
		else:
			hand.lowered = false
			hand.local_target.y += height / frames
		await _measured_frame()
	await _hold(0.3)


func _tears() -> int:
	return _surgery.patient.wounds.filter(func(w: Wound) -> bool: return w.kind == Wound.Kind.TEAR).size()


## Builds the scenario with the site untouched, an awake patient numbed with lidocaine and the scalpel in the right hand
## over the site, its edge along the limb or across the table. over_bone: the cut runs right over the bone nearest the
## middle of the site (a forearm's middle lies between its two bones).
func _start(scenario_id: String, along_limb: bool, over_bone: bool, graft: bool = false) -> void:
	var scenario := Db.scenario(scenario_id)
	scenario.wounds = []
	scenario.burns = []
	scenario.internal = []
	scenario.targets = []
	scenario.events = []
	Net.leave()
	Net.scenario_id = scenario_id
	Net.session_seed = 42
	Net.roster = {1: {"name": "Tester", "quirks": [{"id": "normal_dude", "variant": ""}], "ready": true}}
	Net.patient_quirks = []
	Net.run_modifiers = []
	_surgery = SURGERY.instantiate()
	add_child(_surgery)
	await _frames(10)
	_surgery.hud.visible = false
	var patient := _surgery.patient
	var body := patient.body
	patient.administer("lidocaine", "direct")
	var me := _surgery.local_surgeon
	# A blade's edge runs the way the surgeon faces. Stand at the table's side by the site, facing the table, or
	# turned to face along the limb like a player cutting along it.
	var site := body.site.global_position
	var side := signf(me.global_position.z - site.z)
	var along := Vector3(0, 0, -side)
	if along_limb:
		along = ((body.uv_to_world(Vector2(0.6, 0.5)) - body.uv_to_world(Vector2(0.4, 0.5))) * Vector3(1, 0, 1)).normalized()
	me.global_position = Vector3(site.x, me.global_position.y, site.z) - along * 0.2 + Vector3(0, 0, side * 0.3)
	me.rotation.y = atan2(-along.x, -along.z)
	for tool: SurgicalTool in _surgery.tools.tools.values():
		if tool.def.id == "scalpel" and tool.state == SurgicalTool.State.FREE:
			_scalpel = tool
			break
	_surgery.tools._req_grab(_scalpel.uid, 1)
	me.active = 1
	_hand = me.hands[1]
	_place_hand(Vector2(0.5, 0.5))
	await _frames(30)
	# The edge in site uv, three moves of MOVE centered on the site.
	var edge := ToolActions.blade_direction(_scalpel)
	var center := body.uv_to_world(Vector2(0.5, 0.5))
	var edge_uv := body.world_to_uv(center + edge * MOVE) - Vector2(0.5, 0.5)
	_edge_uv = edge_uv / MOVE
	_center_uv = Vector2(0.5, 0.5)
	if over_bone:
		# The middle of each run of v under which a bone lies just below the muscle; the one nearest the site's middle.
		var runs: Array[Vector2] = []
		var inside := false
		for i in 81:
			var v := 0.1 + i * 0.01
			var bone := body.bone_at(body.uv_to_world(Vector2(0.5, v), body.muscle_bottom() + 0.006), 0.001) != ""
			if bone and not inside:
				runs.append(Vector2(v, v))
			elif bone:
				runs[-1].y = v
			inside = bone
		var best := INF
		for run in runs:
			var middle := (run.x + run.y) * 0.5
			if absf(middle - 0.5) < best:
				best = absf(middle - 0.5)
				_center_uv = Vector2(0.5, middle)
	# The tip lands where the hand's height and tilt put it: nudge the hand until it's over the planned start.
	var start := _center_uv - edge_uv * 1.5
	var travel := edge_uv * 3.0
	if graft:
		# The circle's far side no further over the table than the middle of the site (a hand at full stretch can't
		# steer), as long as the circle stays on the site (a forearm is narrow).
		var across := body.site.global_basis.y.normalized().cross(edge).normalized()
		var toward := signf(across.dot(me.global_position - center))
		for shift: float in [1.0, 0.5, 0.0]:
			var moved := center + across * toward * GRAFT_RADIUS * shift
			if range(16).all(func(n: int) -> bool: return body.probe(_circle_point(moved, edge, across, TAU * n / 16)).zone == "site"):
				center = moved
				break
		_center_uv = body.world_to_uv(center)
		# Round the circle from its point on the -across side, where it runs along the blade.
		start = body.world_to_uv(center - across * GRAFT_RADIUS)
		travel = body.world_to_uv(center + across * GRAFT_RADIUS) - start
	_place_hand(start)
	var last_reach := INF
	for i in 12:
		await _frames(20)
		# The hand at the start or the end of the three moves out of reach from here: a step closer, like a player
		# would, while that still helps (the table stops them). With room to spare: at high the hand drops into the
		# opening, stretching the arm further.
		var finish := _hand.target + (body.uv_to_world(start + travel) - body.uv_to_world(start)) * Vector3(1, 0, 1)
		var shoulder := me.shoulder(1)
		var reach := maxf(_hand.target.distance_to(shoulder), finish.distance_to(shoulder))
		if reach > Surgeon.REACH - 0.04 and reach < last_reach - 0.005:
			last_reach = reach
			var keep := _hand.target
			me.global_position += ((body.uv_to_world(_center_uv) - me.global_position) * Vector3(1, 0, 1)).normalized() * 0.05
			# The body settles against the table over a frame or two: the hand stays where it was in the world.
			await _frames(2)
			_hand.local_target = me.to_local(keep)
			continue
		var miss := body.uv_to_world(start) - _scalpel.tip_position()
		_hand.local_target += me.global_basis.inverse() * (miss * Vector3(1, 0, 1))
	await _frames(20)
	print("    blade along the site's long side: %.2f, tip at %s for %s, patient awake: %s, local block %.2f" % [absf(edge.dot(along)), _tip_uv(), start, patient.vitals.is_awake(), patient.vitals.local_block])


## Puts the hand where the scalpel's tip comes down on uv.
func _place_hand(uv: Vector2) -> void:
	var me := _surgery.local_surgeon
	var spot := _surgery.patient.body.uv_to_world(uv) + Vector3(0, 0.06, 0)
	_hand.target = spot - (_scalpel.tip_position() - _hand.global_position) * Vector3(1, 0, 1)
	_hand.local_target = me.to_local(_hand.target)


func _press(level: int) -> void:
	_hand.level = level
	_hand.lowered = true
	_hand.trigger = true
	await _hold(PRESS_TIME)


## Moves the hand MOVE along the blade's edge over MOVE_TIME, then checks the cut stayed clean.
func _move() -> void:
	var body := _surgery.patient.body
	var me := _surgery.local_surgeon
	var at := _path[-1]
	var step := (body.uv_to_world(at + _edge_uv * MOVE) - body.uv_to_world(at)) * Vector3(1, 0, 1)
	var frames := int(MOVE_TIME * Engine.physics_ticks_per_second)
	_top_speed = 0.0
	for i in frames:
		# Like a player watching the blade: along the edge, and steered back onto the line if the tip drifts off it
		# (pressed deeper on a round limb, an angled blade's tip shifts sideways).
		var planned := body.uv_to_world(at + _edge_uv * MOVE * (i + 1) / frames)
		var drift := (planned - _scalpel.tip_position()) * Vector3(1, 0, 1)
		drift -= step.normalized() * drift.dot(step.normalized())
		_hand.local_target += me.global_basis.inverse() * (step / frames + drift * 0.3)
		await _measured_frame()
		_top_speed = maxf(_top_speed, _hand.speed)
	await _hold(0.5)
	_path.append(_tip_uv())
	var tears := _surgery.patient.wounds.filter(func(w: Wound) -> bool: return w.kind == Wound.Kind.TEAR)
	_check(tears.is_empty() and _top_speed < 0.25, "the incision stays clean: %d tears, hand at most %.3f m/s" % [tears.size(), _top_speed])


func _tip_uv() -> Vector2:
	return _surgery.patient.body.world_to_uv(_scalpel.tip_position())


## The uv points along the n-th 2 cm of the cut, a little in from both ends.
func _segment(n: int) -> PackedVector2Array:
	var a := _path[n]
	var b := _path[n + 1]
	return PackedVector2Array([a.lerp(b, 0.25), a.lerp(b, 0.5), a.lerp(b, 0.75)])


func _tissue() -> TissueSim:
	return _surgery.patient.body.tissue


## The cut is visibly open down through `depth` along these points: pulled apart as far as the meshes show it open.
func _open(points: PackedVector2Array, depth: int) -> bool:
	return _tissue().gap_along(points, GAP_RADIUS, depth) > TissueSim.OPEN_GAP * 0.5


func _layers(points: PackedVector2Array) -> String:
	var sim := _tissue()
	return "gap through skin %.1f, fat %.1f, muscle %.1f mm" % [
		sim.gap_along(points, GAP_RADIUS, TissueSim.Depth.SKIN) * 1000.0,
		sim.gap_along(points, GAP_RADIUS, TissueSim.Depth.FAT) * 1000.0,
		sim.gap_along(points, GAP_RADIUS, TissueSim.Depth.MUSCLE) * 1000.0]


## Deepest any spring is cut so far.
func _deepest() -> int:
	var deepest := 0
	for s in _tissue().severed():
		deepest = maxi(deepest, _tissue().cut_depth(s))
	return deepest


## How wide the skin gapes along the whole cut so far, from where it starts (the blade pressed in there cut as wide
## as itself, half of it behind the first move) to the blade (mm), every 4 mm.
func _gaps() -> PackedFloat32Array:
	var out := PackedFloat32Array()
	var size := _surgery.patient.body.site_size
	var start := _path[0] - _edge_uv * ToolActions.STAB_LENGTH * 0.5
	out.append(_tissue().gap_along(PackedVector2Array([start]), 0.5 / _tissue().res_x, TissueSim.Depth.SKIN) * 1000.0)
	for n in range(1, _path.size()):
		var a := _path[n - 1]
		var b := _path[n]
		var steps := maxi(1, roundi(((b - a) * size).length() / 0.004))
		for i in steps + (1 if n == _path.size() - 1 else 0):
			var uv := a.lerp(b, float(i) / steps)
			out.append(_tissue().gap_along(PackedVector2Array([uv]), 0.5 / _tissue().res_x, TissueSim.Depth.SKIN) * 1000.0)
	return out


func _profile() -> String:
	return " ".join(Array(_gaps()).map(func(g: float) -> String: return "%.1f" % g)) + " mm"


## Like opening a zipper: the cut gapes widest somewhere along it and narrows toward both ends. The end at the blade
## is measured at the springs nearest it, up to half a cell behind it on a coarse grid (a belly's), so it needn't be shut.
func _zipper() -> bool:
	var gaps := _gaps()
	var widest := 0.0
	for g in gaps:
		widest = maxf(widest, g)
	return widest > TissueSim.OPEN_GAP * 500.0 and gaps[0] < widest * 0.7 and gaps[-1] < widest * 0.7


## How far the simulated skin lies off the body model (meters) along the edge of the region, where it hands over to
## the model: its grid points next to one the model draws, straight down the site's normal onto the model. Breathing
## lifts the site and the trunk together, but not the body's colliders: that lift is added back.
static func seam(body: PatientBody) -> float:
	var sim := body.tissue
	var region := sim.region()
	var up := body.site.global_basis.y.normalized()
	var lift := body.site.position.y - float(Db.patient_sites[body.site_id].pos[1])
	var worst := 0.0
	for k in region.size():
		if region[k] == 0 or sim.off[k] == 1 or sim.excised[k] == 1:
			continue
		var at := sim.cell_of(k)
		var edge := false
		for step: Vector2i in [Vector2i.LEFT, Vector2i.RIGHT, Vector2i.UP, Vector2i.DOWN]:
			var n := (at + step).clamp(Vector2i.ZERO, Vector2i(sim.res_x, sim.res_y))
			edge = edge or region[sim.index(n.x, n.y)] == 0
		if not edge:
			continue
		var skin := body.site.to_global(body.layer_point(0, k))
		var query := PhysicsRayQueryParameters3D.create(skin + up * 0.05, skin - up * 0.05, PatientBody.SURFACE_LAYER)
		var hit := body.get_world_3d().direct_space_state.intersect_ray(query)
		if not hit.is_empty():
			worst = maxf(worst, absf(body.site.to_local(hit.position).y + lift - body.layer_point(0, k).y))
	return worst


## There's a bone (or organs) right under the muscle along these points.
func _under(points: PackedVector2Array, what: String) -> bool:
	var body := _surgery.patient.body
	for uv in points:
		var below := body.uv_to_world(uv, body.muscle_bottom() + 0.006)
		if what == "bone" and body.bone_at(below, 0.01) != "":
			return true
		if what == "organs" and body.organ_at(below, 0.03) >= 0:
			return true
	return false


## How deep under the site's surface the scalpel's tip is now.
func _tip_depth() -> float:
	return -_surgery.patient.body.height_above_site(_scalpel.tip_position())


func _check(ok: bool, what: String) -> void:
	print(("    ok   " if ok else "FAIL: ") + what)
	_log_state()


func _log_state() -> void:
	var patient := _surgery.patient
	var probe := patient.body.probe(_scalpel.tip_position())
	var depths := {}
	for wound in patient.wounds:
		depths[Wound.Kind.keys()[wound.kind]] = maxf(depths.get(Wound.Kind.keys()[wound.kind], 0.0), wound.depth)
	print("         tip %s %.4f m, wounds %s, pain %.2f, bone scraped %.2f" % [probe.zone, probe.depth, depths, patient.vitals.pain, patient.flags.get("bone_scraped", 0.0)])


func _hold(seconds: float) -> void:
	for i in int(seconds * Engine.physics_ticks_per_second):
		await _measured_frame()


## A physics frame, timed. Untimed waits (_frames(), _shot()) stop the timing until the next one.
func _measured_frame() -> void:
	if not _measuring:
		_last_frame_usec = Time.get_ticks_usec()
		_measuring = true
	await get_tree().physics_frame


## Wall clock time between frames: delta is smoothed and capped, so it hides stalls.
func _process(_delta: float) -> void:
	var now := Time.get_ticks_usec()
	if _measuring:
		_frame_times.append((now - _last_frame_usec) / 1000000.0)
	_last_frame_usec = now


func _report_frames(case_id: String) -> void:
	if _frame_times.is_empty():
		return
	var sorted := _frame_times.duplicate()
	sorted.sort()
	var total := 0.0
	for t in sorted:
		total += t
	var fps := sorted.size() / total
	# 1% low: the frame time 99% of frames beat.
	var low := 1.0 / sorted[int(sorted.size() * 0.99)]
	var line := "%s: %.1f fps average, %.1f fps 1%% low, %.1f fps worst frame (%d frames, %s)" % [
		case_id, fps, low, 1.0 / sorted[-1], sorted.size(), "no rendering" if not _shots else RenderingServer.get_current_rendering_method()]
	_report.append(line)
	print("    " + line)
	if _check_fps:
		_check(fps >= MIN_FPS and low >= MIN_FPS, "%s holds %d fps" % [case_id, int(MIN_FPS)])
	_frame_times.clear()


## Both views of the middle of the cut: straight down, and 45° off the side from across the table, so the surgeon's
## arm isn't in the way.
func _shots_of(case_id: String, file: String) -> void:
	if not _shots:
		return
	var body := _surgery.patient.body
	var middle := body.uv_to_world(_center_uv)
	var up := body.site.global_basis.y.normalized()
	var along := (body.uv_to_world(_center_uv + _edge_uv * 0.01) - middle).normalized()
	# Square to the cut, on the side away from the surgeon.
	var across := up.cross(along).normalized()
	if across.dot(middle - _surgery.local_surgeon.global_position) < 0.0:
		across = -across
	# The hands would hide the cut from straight above; the scalpel stays in view.
	for hand in _surgery.local_surgeon.hands:
		hand.visible = false
	_camera.current = true
	_camera.global_position = middle + up * CAMERA_DISTANCE
	_camera.look_at(middle, along)
	await _shot(case_id, file + "_top")
	_camera.global_position = middle + (up + across).normalized() * CAMERA_DISTANCE
	_camera.look_at(middle, up)
	await _shot(case_id, file + "_side")
	for hand in _surgery.local_surgeon.hands:
		hand.visible = true


func _shot(case_id: String, file: String) -> void:
	_measuring = false
	# Rendered frames, not physics ones: a slow renderer runs several physics frames per drawn frame.
	for i in 3:
		await get_tree().process_frame
	get_viewport().get_texture().get_image().save_png(_out.path_join("%s_%s.png" % [case_id, file]))


func _frames(count: int) -> void:
	_measuring = false
	for i in count:
		await get_tree().physics_frame
