extends GutTest
## The retractor as a player uses it: hooked onto one edge of a cut with Use tool, drawn aside by moving the hand so
## that edge pulls away from the other, let go of with Grab so it lies down on the body and keeps pulling, taken back
## and unhooked with Use tool so the cut falls back. Headless assertions in smoke; with key frames also the site from
## above and obliquely, hooked, pulled and left lying. Review them for the hook sitting on the edge it pulls, that edge
## stretched aside over a few centimeters and the other one staying put, and the retractor let go of lying on the skin
## pointing away from the cut, not standing up or sinking into the body.

const TAGS = ["smoke", "tool_retractor", "tissue_modification", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")
const KEY_FRAMES := "res://build/test-artifacts/screenshots/retractor"
const LENGTH := 0.05
## How far (meters) from the cut the hook goes in, and how far the hand then draws it aside.
const HOOK_OFF := 0.006
const PULL := 0.02
## How far (meters) from the cut's middle its gap is measured.
const MIDDLE := 0.005
## Most a retractor let go of may tilt up over the body it lies on (degrees).
const LYING_TILT := 8.0
## How deep (meters) a retractor lying on the body may press into it at most: about its stay's half thickness
## (tools/assetgen/instruments.py), so the stay still shows when breathing lifts the belly under it.
const LYING_PRESS := 0.0025
## How long (meters) the cut the four retractors hold open is.
const OPENING := 0.08

var driver: Driver
var shots: KeyFrames


func test_retractor_hooks_one_edge_and_pulls_the_cut_open() -> void:
	await _start("appendectomy", "pull")
	var patient := driver.patient
	var body := driver.body
	var tissue := body.tissue
	var from := Vector2(0.4, 0.45)
	var to := from + Vector2(body.meters_to_uv(LENGTH), 0.0)
	var middle := (from + to) * 0.5
	await driver.player_cuts_skin(from, to, 2)
	await driver.player_puts_down()
	await driver.seconds(1.0)
	var own_gape := tissue.gap_at(middle, MIDDLE)
	var lips_before := _lips(middle)
	var wounds := patient.wounds.size()

	var retractor := await driver.player_requests_item("retractor")
	var hook_at := driver.site_point(middle + Vector2(0.0, body.meters_to_uv(HOOK_OFF)))
	await driver.player_walks_to(hook_at)
	await driver.player_reaches(hook_at)
	driver.use()
	await driver.frames(10)
	driver.use(false)
	await driver.frames(3)
	var hand := driver.me.hands[driver.me.active]
	assert_true(retractor.grip_info.get("type", "") == "skin" and hand.attached, "Use tool beside the cut hooks the skin\n%s" % driver.recent())
	await driver.capture("hooked")

	var aside := driver.site_point(middle + Vector2(0.0, body.meters_to_uv(HOOK_OFF + PULL))) - hook_at
	await driver.player_sweeps_to(retractor.tip_position() + aside)
	await driver.seconds(1.0)
	var opened := tissue.gap_at(middle, MIDDLE)
	assert_gt(opened - own_gape, PULL * 0.5, "drawn %.0f cm aside, the cut opens (%.1f mm wider)" % [PULL * 100.0, (opened - own_gape) * 1000.0])
	var lips := _lips(middle)
	var hooked_moved := lips[1] - lips_before[1]
	var other_moved := absf(lips[0] - lips_before[0])
	assert_gt(hooked_moved, other_moved * 3.0, "the hooked edge moves aside (%.1f mm), the other one much less (%.1f mm)" % [hooked_moved * 1000.0, other_moved * 1000.0])
	assert_eq(patient.wounds.size(), wounds, "a 2 cm pull doesn't tear")
	await driver.capture("pulled")

	var hooked := retractor.tip_position()
	driver.tap("grab")
	await driver.seconds(2.0)
	assert_eq(retractor.state, SurgicalTool.State.STANDING, "let go of with Grab, the retractor stays hooked")
	assert_false(hand.attached, "and the hand is free")
	assert_lt((retractor.tip_position() - hooked).slide(Vector3.UP).length(), 0.002, "the hook stays where it held the skin")
	_assert_lies_on_body(retractor)
	assert_gt(tissue.gap_at(middle, MIDDLE), opened - 0.002, "left alone, it keeps the cut pulled open")
	var handle := retractor.global_position - retractor.tip_position()
	assert_lt(absf(handle.normalized().y), 0.35, "it lies along the body (%.0f degrees off level), not standing up" % rad_to_deg(asin(absf(handle.normalized().y))))
	_assert_points_away(retractor, aside)
	await driver.capture("let_go")

	await driver.player_reaches(retractor.tip_position())
	driver.press("grab")
	await driver.frames(10)
	assert_eq(driver.me.held_tool(driver.me.active), retractor, "taken back in hand")
	assert_true(retractor.grip_info.get("type", "") == "skin" and hand.attached, "still hooked")
	assert_lt(retractor.tip_position().distance_to(hooked), 0.01, "the hook stays on the skin it held as the hand takes it")
	assert_eq(patient.wounds.size(), wounds, "taking it back doesn't tear")

	driver.use()
	await driver.frames(10)
	driver.use(false)
	await driver.seconds(2.0)
	assert_true(retractor.grip_info.is_empty() and not hand.attached, "Use tool again lets go")
	assert_lt(tissue.gap_at(middle, MIDDLE), own_gape + 0.002, "let go of, the cut falls back to its own gape")
	await driver.player_puts_down()
	assert_true(driver.lies_on_tray(retractor), "the retractor is put back on the tray")
	await _finish()


func test_retractors_let_go_round_a_widened_thigh_wound_lie_along_the_limb() -> void:
	await _start("bullet_muscle", "thigh")
	var body := driver.body
	var wound: Wound = driver.patient.wounds[0]
	var middle := wound.midpoint()
	# The entry wound widened through every layer along the thigh, then three retractors round it: one on each edge,
	# one at its end, each drawn away from the wound and let go of.
	var half := Vector2(body.meters_to_uv(0.025), 0.0)
	await driver.player_cuts_skin(middle - half, middle + half, 3)
	await driver.player_puts_down()
	await driver.seconds(1.0)
	var wounds := driver.patient.wounds.size()
	var hooks: Array[SurgicalTool] = []
	for away: Vector2 in [Vector2(0, -1), Vector2(0, 1), Vector2(1, 0)]:
		# On each side at the edge of the opening, pressed into it as a player does: the hook catches the edge on that
		# side. At the end on the skin past it: drawn along the cut from inside it, it would tear it longer.
		var at := middle + away * body.meters_to_uv(0.003) if away.x == 0.0 else _edge_beside(middle + half * 0.8, away)
		SurgeryState.tool_is_on_tray(driver.surgery, "retractor")
		var retractor := await driver.player_requests_item("retractor")
		var hook_at := driver.site_point(at)
		await driver.player_walks_to(hook_at)
		await driver.player_reaches(hook_at)
		driver.use()
		await driver.frames(10)
		driver.use(false)
		await driver.frames(3)
		assert_eq(retractor.grip_info.get("type", ""), "skin", "retractor %d hooks the edge\n%s" % [hooks.size() + 1, driver.recent()])
		# The end only gently: the short strip of skin between the hook and the end of the cut tears pulled 2 cm.
		var pull := driver.site_point(at + away * body.meters_to_uv(PULL if away.x == 0.0 else PULL * 0.5)) - hook_at
		await driver.player_sweeps_to(retractor.tip_position() + pull)
		driver.tap("grab")
		await driver.seconds(1.0)
		assert_eq(retractor.state, SurgicalTool.State.STANDING, "retractor %d stays hooked when let go of" % (hooks.size() + 1))
		_assert_points_away(retractor, pull)
		assert_eq(driver.patient.wounds.size(), wounds, "retractor %d hooked and pulled without tearing" % (hooks.size() + 1))
		hooks.append(retractor)
	for retractor in hooks:
		_assert_lies_on_body(retractor)
	await driver.capture("let_go")
	await _finish()


func test_four_retractors_hold_the_abdomen_open_for_the_scalpel_and_forceps() -> void:
	await _start("appendectomy", "four")
	var patient := driver.patient
	var body := driver.body
	var appendix: CavityTarget = patient.targets[0]
	# A cut through every layer over the appendix, along the site's long side.
	var half := Vector2(body.meters_to_uv(OPENING * 0.5), 0.0)
	var from := appendix.uv - half
	var to := appendix.uv + half
	await driver.player_cuts_skin(from, to, 3)
	await driver.player_puts_down()
	await driver.seconds(1.0)
	var wounds := patient.wounds.size()
	await driver.capture("incised")

	# Two retractors on each edge, a third of the way in from each end, each hooked and drawn away from the cut.
	var hooks: Array[SurgicalTool] = []
	for spot: Vector2 in [Vector2(-1, -1), Vector2(1, -1), Vector2(-1, 1), Vector2(1, 1)]:
		var at := _edge_beside(appendix.uv + Vector2(half.x * spot.x / 3.0, 0.0), spot.y)
		SurgeryState.tool_is_on_tray(driver.surgery, "retractor")
		var retractor := await driver.player_requests_item("retractor")
		var hook_at := driver.site_point(at)
		await driver.player_walks_to(hook_at)
		await driver.player_reaches(hook_at)
		driver.use()
		await driver.frames(10)
		driver.use(false)
		await driver.frames(3)
		assert_eq(retractor.grip_info.get("type", ""), "skin", "retractor %d hooks the edge\n%s" % [hooks.size() + 1, driver.recent()])
		var aside := driver.site_point(at + Vector2(0.0, body.meters_to_uv(PULL) * spot.y)) - hook_at
		await driver.player_sweeps_to(retractor.tip_position() + aside)
		driver.tap("grab")
		await driver.seconds(1.0)
		assert_eq(retractor.state, SurgicalTool.State.STANDING, "retractor %d stays hooked when let go of" % (hooks.size() + 1))
		_assert_lies_on_body(retractor)
		_assert_points_away(retractor, aside)
		hooks.append(retractor)
	assert_true(hooks.all(func(r: SurgicalTool) -> bool: return r.state == SurgicalTool.State.STANDING and not r.grip_info.is_empty()), "all four hold the edges")
	assert_true(body.is_open(appendix.uv), "the abdomen stands open between them")
	var gap := body.tissue.gap_at(appendix.uv, MIDDLE, TissueSim.Depth.MUSCLE)
	assert_gt(gap, PULL, "%.1f cm wide through the muscle" % (gap * 100.0))
	assert_eq(patient.wounds.size(), wounds, "held open without tearing")
	await driver.capture("retracted")

	# The scalpel goes down into the opening between them and cuts the appendix's base free.
	await driver.player_requests_item("scalpel")
	var base := body.uv_to_world(appendix.uv, appendix.depth)
	await driver.player_walks_to(base)
	await driver.player_reaches(base)
	await driver.set_level(3)
	driver.use()
	var reached := false
	for i in 30:
		await driver.frames(1)
		reached = reached or driver.body.probe(driver.me.held_tool(driver.me.active).tip_position()).zone == "cavity"
	assert_true(reached, "the scalpel reaches into the opening, not onto a retractor")
	await driver.capture("scalpel_inside")
	await driver.wait_until(func() -> bool: return appendix.anchor <= 0.0, 20.0)
	driver.use(false)
	assert_eq(appendix.anchor, 0.0, "the scalpel cuts the appendix free inside the opening")
	await driver.player_puts_down()

	# Forceps take hold of it in the opening and lift it out past the retractors.
	await driver.player_extracts("appendix")
	assert_true(appendix.extracted, "the forceps lift the appendix out between the retractors\n%s" % driver.recent())
	assert_true(hooks.all(func(r: SurgicalTool) -> bool: return r.state == SurgicalTool.State.STANDING and not r.grip_info.is_empty()), "the retractors still hold the edges")
	await _finish()


## The skin just outside the opening's edge from `uv` (on the cut) toward `side` (-1 or 1 in v): where the skin is
## whole, HOOK_OFF further out.
func _edge_beside(uv: Vector2, side: Variant) -> Vector2:
	var away: Vector2 = side if side is Vector2 else Vector2(0.0, side)
	var step := away * driver.body.meters_to_uv(0.001)
	for i in 60:
		if driver.body.layer_at(uv) == "skin":
			break
		uv += step
	return uv + step * HOOK_OFF * 1000.0


## Where the cut's two edges are at its middle (site z, meters): the mean of the lips on the -v side and the +v side.
func _lips(middle: Vector2) -> Array[float]:
	var tissue := driver.body.tissue
	var sums: Array[float] = [0.0, 0.0]
	var counts: Array[int] = [0, 0]
	for s in tissue.severed():
		var crossed := tissue.uv_of(tissue.c_a[s]).lerp(tissue.uv_of(tissue.c_b[s]), tissue.c_cross[s])
		if absf(crossed.x - middle.x) > driver.body.meters_to_uv(0.01):
			continue
		for k in [tissue.c_a[s], tissue.c_b[s]]:
			var side := 1 if tissue.uv_of(k).y > middle.y else 0
			sums[side] += tissue.pos[k].z
			counts[side] += 1
	return [sums[0] / maxi(counts[0], 1), sums[1] / maxi(counts[1], 1)]


## Starts `scenario_id` with the patient asleep (the cut doesn't make them flinch: the setting, not what's tested), and
## in a run with key frames saves the case's under KEY_FRAMES/`case_name`, starting untouched.
func _start(scenario_id: String, case_name: String) -> void:
	RenderingServer.render_loop_enabled = false
	driver = Driver.new()
	add_child(driver)
	await driver.start(scenario_id)
	SurgeryState.patient_is_asleep(driver.patient)
	if KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(driver.surgery, KEY_FRAMES.path_join(case_name))
		driver.on_key_frame = func(key_frame: String) -> void:
			assert_true(await shots.capture(key_frame), "%s: saved key frame %s" % [case_name, key_frame])
		await driver.capture("untouched")
	# Loading the room isn't gameplay: the frame budget counts from here.
	driver.budget.clear()


## A retractor let go of lies on the body: its handle tilts up no more than LYING_TILT over the way the body rises from
## its hook to its handle (over level where the handle hangs past the edge of a limb), and it presses into the body
## nowhere deeper than LYING_PRESS.
func _assert_lies_on_body(retractor: SurgicalTool) -> void:
	var tip := retractor.tip_position()
	var grip := retractor.global_position
	var across := ((grip - tip) * Vector3(1, 0, 1)).length()
	var tilt := rad_to_deg(atan2(grip.y - tip.y, across))
	var under := _body_height(grip)
	var body_rise := rad_to_deg(atan2(under - _body_height(tip), across)) if under != -INF else 0.0
	assert_lt(tilt - maxf(body_rise, 0.0), LYING_TILT, "the retractor lies down (tilted %.0f degrees, the body rising %.0f), not standing up" % [tilt, body_rise])
	var deepest := 0.0
	for i in range(1, 11):
		var p := tip.lerp(grip, i / 10.0)
		deepest = maxf(deepest, _body_height(p) - p.y)
	assert_lt(deepest, LYING_PRESS, "pressed into the body at most %.1f mm" % (deepest * 1000.0))


## A retractor let go of points its handle straight away from where it hooked: the way it was pulled.
func _assert_points_away(retractor: SurgicalTool, pull: Vector3) -> void:
	var handle := (retractor.global_position - retractor.tip_position()) * Vector3(1, 0, 1)
	var facing := handle.normalized().dot((pull * Vector3(1, 0, 1)).normalized())
	assert_gt(facing, 0.97, "its handle points away from the hook, the way it pulled (%.0f degrees off)" % rad_to_deg(acos(clampf(facing, -1.0, 1.0))))


## The height of the body at rest under p (world y): the site's measured surface, elsewhere the body's own collider.
func _body_height(p: Vector3) -> float:
	var body := driver.body
	var uv := body.world_to_uv(p)
	if Rect2(0, 0, 1, 1).has_point(uv) and body.on_body(uv):
		return body.uv_to_world(uv).y
	var query := PhysicsRayQueryParameters3D.create(p + Vector3.UP * 0.2, p + Vector3.DOWN, PatientBody.SURFACE_LAYER)
	var hit := driver.get_viewport().world_3d.direct_space_state.intersect_ray(query)
	return (hit.position as Vector3).y if not hit.is_empty() else -INF


func _finish() -> void:
	driver.budget.check(self, shots != null, "", "known to go over the frame budget, not profiled yet")
	if shots:
		gut.p("key frames: %s" % shots.out_dir)
		shots.end()
		shots.queue_free()
		shots = null
	await driver.stop()
	driver.queue_free()
	RenderingServer.render_loop_enabled = true
