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
	driver.press("grab")
	await driver.seconds(2.0)
	assert_eq(retractor.state, SurgicalTool.State.STANDING, "let go of with Grab, the retractor stays hooked")
	assert_false(hand.attached, "and the hand is free")
	assert_lt(retractor.tip_position().distance_to(hooked), 0.002, "the hook stays where it held the skin")
	assert_gt(tissue.gap_at(middle, MIDDLE), opened - 0.002, "left alone, it keeps the cut pulled open")
	var handle := retractor.global_position - retractor.tip_position()
	assert_lt(absf(handle.normalized().y), 0.35, "it lies along the body (%.0f degrees off level), not standing up" % rad_to_deg(asin(absf(handle.normalized().y))))
	assert_gt(handle.normalized().dot(aside.normalized()), 0.9, "pointing away from the cut, the way it pulled")
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
