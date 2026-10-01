extends Node
## Close up scalpel test: drives the scalpel through the surgeon's hand like a player (lower it, pick a depth level,
## move it along the blade's edge) on intact skin of an arm, a thigh and a belly, step by step.
## After every step it checks what should be open, saves a screenshot and logs frame times.
## Run with a renderer for screenshots:
## xvfb-run -a godot --path . --rendering-method gl_compatibility res://tests/slicing_test.tscn -- --out=build/slicing
## Headless it runs the same checks without screenshots. --case=arm|thigh|belly runs one case.

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
## How far from the cut (site uv) the gap is measured: severed springs lie up to half a grid cell off it.
const GAP_RADIUS := 0.03

var _out := "user://slicing"
var _shots := true
var _camera: Camera3D
var _surgery: Surgery
var _hand: SurgeonHand
var _scalpel: SurgicalTool
## The cut runs from _start_uv along _edge_uv (site uv per meter), MOVE per step.
var _start_uv := Vector2.ZERO
var _edge_uv := Vector2.ZERO
var _moved := 0
## Fastest the hand went during the last move (m/s): over 0.25 a cut comes out jagged.
var _top_speed := 0.0
## Wall clock frame times (seconds) while the scalpel works, screenshots left out.
var _measuring := false
var _last_frame_usec := 0
var _frame_times: PackedFloat32Array = []
var _report: Array[String] = []


func _ready() -> void:
	var only := ""
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--case="):
			only = arg.get_slice("=", 1)
		elif arg.begins_with("--out="):
			_out = arg.get_slice("=", 1)
	_shots = DisplayServer.get_name() != "headless"
	DirAccess.make_dir_recursive_absolute(_out)
	_camera = Camera3D.new()
	_camera.fov = 50.0
	add_child(_camera)
	for case in CASES:
		if only.is_empty() or case.id == only:
			await _run(case)
	print("\n".join(_report))
	print("slicing_test: done")
	get_tree().quit()


func _run(case: Dictionary) -> void:
	print("--- ", case.id)
	await _start(case.scenario, case.along)
	var fat: bool = case.fat
	var inside: String = case.inside
	var patient := _surgery.patient

	# Untouched, intact skin.
	_check(patient.wounds.is_empty() and not _tissue().any_severed(), "the skin starts intact")
	await _shot(case.id, "00_intact")

	# Lowered with no depth picked: the blade rests on the skin.
	_hand.level = 0
	_hand.lowered = true
	_hand.trigger = true
	await _hold(PRESS_TIME)
	_check(patient.wounds.is_empty() and not _tissue().any_severed(), "a lowered blade at level 0 does nothing")
	await _shot(case.id, "01_lowered")

	# Low: through the skin.
	await _press(1)
	_check(patient.wounds.size() > 0 or _tissue().any_severed(), "pressed at low it goes through the skin")
	await _shot(case.id, "02_pressed_low")
	await _move(case.id, "03_moved_low")
	var first := _segment(0)
	_check(_open(first, TissueSim.Depth.SKIN), "after the first 2 cm the skin is open")
	if fat:
		_check(not _open(first, TissueSim.Depth.FAT), "after the first 2 cm the fat shows, not cut (%s)" % _layers(first))
	else:
		_check(_open(first, TissueSim.Depth.FAT) and not _open(first, TissueSim.Depth.MUSCLE),
				"after the first 2 cm the muscle shows: no fat in the way, muscle not cut (%s)" % _layers(first))

	# Medium: deeper, then 2 cm more.
	var before := _tip_depth()
	await _press(2)
	_check(_tip_depth() > before, "pressed at medium it digs deeper (%.4f -> %.4f m)" % [before, _tip_depth()])
	await _shot(case.id, "04_pressed_medium")
	await _move(case.id, "05_moved_medium")
	var second := _segment(1)
	if fat:
		_check(_open(second, TissueSim.Depth.FAT) and not _open(second, TissueSim.Depth.MUSCLE),
				"after the second 2 cm the fat is cut and the muscle shows (%s)" % _layers(second))
	else:
		_check(_open(second, TissueSim.Depth.MUSCLE), "after the second 2 cm the muscle is cut, %s shows (%s)" % [inside, _layers(second)])

	# High.
	before = _tip_depth()
	var pain := patient.vitals.pain
	var scraped: float = patient.flags.get("bone_scraped", 0.0)
	await _press(3)
	await _shot(case.id, "06_pressed_high")
	if not fat:
		_check(patient.flags.get("bone_scraped", 0.0) > scraped, "pressed at high it hits the bone")
		_check(patient.vitals.pain > pain + 0.1, "hitting the bone hurts through the local block (pain %.2f -> %.2f, block %.2f)" % [pain, patient.vitals.pain, patient.vitals.local_block])
	else:
		_check(_tip_depth() > before, "pressed at high it digs deeper (%.4f -> %.4f m)" % [before, _tip_depth()])
		await _move(case.id, "07_moved_high")
		var third := _segment(2)
		_check(_open(third, TissueSim.Depth.MUSCLE), "after the third 2 cm the muscle is cut, %s shows (%s)" % [inside, _layers(third)])
	_hand.lowered = false
	_hand.trigger = false
	await _frames(30)
	await _close_up(case.id, "08_after")
	_report_frames(case.id)
	_surgery.queue_free()
	await _frames(3)


## Builds the scenario with the site untouched, an awake patient numbed with lidocaine and the scalpel in the right hand
## over the site, its edge along the limb or across the table.
func _start(scenario_id: String, along_limb: bool) -> void:
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
	me.global_position = Vector3(site.x, me.global_position.y, site.z) - along * 0.3 + Vector3(0, 0, side * 0.3)
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
	_start_uv = Vector2(0.5, 0.5) - edge_uv * 1.5
	_moved = 0
	_place_hand(_start_uv)
	await _frames(30)
	print("    blade along the site's long side: %.2f, patient awake: %s, local block %.2f" % [absf(edge.dot(along)), patient.vitals.is_awake(), patient.vitals.local_block])
	await _close_up("", "")


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


## Moves the hand MOVE along the blade's edge over MOVE_TIME, with a screenshot halfway (the cut opening behind it).
func _move(case_id: String, file: String) -> void:
	var body := _surgery.patient.body
	var me := _surgery.local_surgeon
	var from := body.uv_to_world(_uv_at(_moved))
	var to := body.uv_to_world(_uv_at(_moved + 1))
	var step := (to - from) * Vector3(1, 0, 1)
	var frames := int(MOVE_TIME * Engine.physics_ticks_per_second)
	_top_speed = 0.0
	for i in frames:
		_hand.local_target += me.global_basis.inverse() * (step / frames)
		await _measured_frame()
		_top_speed = maxf(_top_speed, _hand.speed)
		if i == frames / 2:
			await _shot(case_id, file + "_half")
	_moved += 1
	await _hold(0.5)
	await _shot(case_id, file)
	var tears := _surgery.patient.wounds.filter(func(w: Wound) -> bool: return w.kind == Wound.Kind.TEAR)
	_check(tears.is_empty() and _top_speed < 0.25, "the incision stays clean: %d tears, hand at most %.3f m/s" % [tears.size(), _top_speed])


func _uv_at(moves: int) -> Vector2:
	return _start_uv + _edge_uv * MOVE * moves


## The uv points along the n-th 2 cm of the cut, a little in from both ends.
func _segment(n: int) -> PackedVector2Array:
	var a := _uv_at(n)
	var b := _uv_at(n + 1)
	return PackedVector2Array([a.lerp(b, 0.25), a.lerp(b, 0.5), a.lerp(b, 0.75)])


func _tissue() -> TissueSim:
	return _surgery.patient.body.tissue


## The cut is open down through `depth` along these points.
func _open(points: PackedVector2Array, depth: int) -> bool:
	return _tissue().gap_along(points, GAP_RADIUS, depth) > TissueSim.OPEN_GAP


func _layers(points: PackedVector2Array) -> String:
	var sim := _tissue()
	return "gap through skin %.4f, fat %.4f, muscle %.4f m" % [
		sim.gap_along(points, GAP_RADIUS, TissueSim.Depth.SKIN),
		sim.gap_along(points, GAP_RADIUS, TissueSim.Depth.FAT),
		sim.gap_along(points, GAP_RADIUS, TissueSim.Depth.MUSCLE)]


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
	_check(fps >= MIN_FPS and low >= MIN_FPS, "%s holds %d fps" % [case_id, int(MIN_FPS)])
	_frame_times.clear()


## Points the close-up camera at the middle of the cut from across the table, so the surgeon's arm isn't in the way.
func _close_up(case_id: String, file: String) -> void:
	var body := _surgery.patient.body
	var middle := body.uv_to_world(_uv_at(1).lerp(_uv_at(2), 0.5))
	var me := _surgery.local_surgeon.global_position
	var across := Vector3(0, 0, -signf(me.z - middle.z))
	_camera.global_position = middle + body.site.global_basis.y.normalized() * 0.1 + across * 0.04
	_camera.look_at(middle)
	_camera.current = true
	if file:
		await _shot(case_id, file)


func _shot(case_id: String, file: String) -> void:
	if not _shots:
		return
	_measuring = false
	# Rendered frames, not physics ones: a slow renderer runs several physics frames per drawn frame.
	for i in 3:
		await get_tree().process_frame
	get_viewport().get_texture().get_image().save_png(_out.path_join("%s_%s.png" % [case_id, file]))


func _frames(count: int) -> void:
	_measuring = false
	for i in count:
		await get_tree().physics_frame
