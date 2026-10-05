extends GutTest
## The Gelpi retractor (self-retaining) as a player uses it: set across the middle of a cut with Use tool, opened on
## the wheel so the cut's edges move apart, let go of and left holding the wound open, taken back, closed and taken
## out. Opened too far it tears the skin. Headless assertions in smoke; with key frames also the site from above and
## obliquely and what the surgeon sees (the < > aim at the tips). Review them for the points sitting on the cut's
## edges, the opening widening with the tips and not past them, no skin passing through the jaws and the < > on the tips.

const TAGS = ["smoke", "tool_gelpi", "tissue_modification", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")
const KEY_FRAMES := "res://build/test-artifacts/screenshots/gelpi"
const LENGTH := 0.05
## How far (meters) from the cut's middle its gap is measured.
const MIDDLE := 0.005
const OPEN := 0.032

var driver: Driver
var shots: KeyFrames
## The oblique key frames look along the cut, so the jaws stand apart across it in view.
var along_cut := Vector3.ZERO


func test_gelpi_set_in_a_cut_holds_it_open_until_taken_out() -> void:
	await _start("appendectomy", "open")
	var patient := driver.patient
	var tissue := driver.body.tissue
	var from := Vector2(0.4, 0.45)
	var to := from + Vector2(driver.body.meters_to_uv(LENGTH), 0.0)
	var middle := (from + to) * 0.5
	along_cut = driver.site_point(to) - driver.site_point(from)
	await driver.player_cuts_skin(from, to, 2)
	await driver.player_puts_down()
	await driver.seconds(1.0)
	var own_gape := tissue.gap_at(middle, MIDDLE)
	var wounds := patient.wounds.size()

	var gelpi := await driver.player_sets_gelpi(from, to)
	var hand := driver.me.hands[driver.me.active]
	assert_true(gelpi.in_wound and hand.attached, "Use tool on the cut sets the retractor in it\n%s" % driver.recent())
	if not gelpi.in_wound:
		await _finish()
		return
	var sides := _jaw_sides(gelpi, middle)
	assert_true(sides[0] * sides[1] < 0.0, "each jaw holds the edge on its own side of the cut (%s)" % str(sides))
	var marks := _aim_marks()
	assert_true(marks.size() == 2, "the aim is a < and a > instead of the dot")
	await driver.capture("set")

	var set_at := gelpi.global_transform
	await driver.player_opens_gelpi(OPEN)
	assert_almost_eq(gelpi.spread, OPEN, 0.001, "the wheel opens the retractor notch by notch")
	var opened := tissue.gap_at(middle, MIDDLE)
	var widened := OPEN - ToolActions.SPREAD_RANGE.x
	assert_gt(opened - own_gape, widened * 0.7, "the cut opens with the tips (%.1f mm wider, the tips %.1f mm)" % [(opened - own_gape) * 1000.0, widened * 1000.0])
	assert_lt(opened, OPEN + 0.004, "but no wider than the tips hold it (%.1f mm)" % (opened * 1000.0))
	assert_eq(patient.wounds.size(), wounds, "opened as far as the cut gives, nothing tears")
	assert_true(gelpi.global_transform.is_equal_approx(set_at), "set, the retractor stays where it went in")
	var opened_marks := _aim_marks()
	if marks.size() == 2 and opened_marks.size() == 2:
		assert_gt(opened_marks[0].distance_to(opened_marks[1]), marks[0].distance_to(marks[1]) + 10.0, "the < and > move apart with the tips")
	await driver.capture("opened")

	driver.press("grab")
	await driver.seconds(2.0)
	assert_eq(gelpi.state, SurgicalTool.State.STANDING, "let go of, the retractor stands in the wound")
	assert_false(hand.attached, "and the hand is free")
	assert_gt(tissue.gap_at(middle, MIDDLE), opened - 0.002, "left alone, it still holds the cut open")
	await driver.capture("let_go")

	await driver.player_reaches(ToolManager.middle(gelpi))
	driver.press("grab")
	await driver.frames(10)
	assert_eq(driver.me.held_tool(driver.me.active), gelpi, "taken back in hand")
	assert_true(gelpi.in_wound and hand.attached and gelpi.global_transform.is_equal_approx(set_at), "still set where it went in")
	await driver.player_opens_gelpi(ToolActions.SPREAD_RANGE.x)
	assert_lt(tissue.gap_at(middle, MIDDLE), opened - widened * 0.7, "closed on the wheel, the edges come back")
	driver.use()
	await driver.frames(10)
	driver.use(false)
	await driver.seconds(2.0)
	assert_false(gelpi.in_wound or hand.attached, "Use tool again takes it out")
	assert_true(tissue.grips().all(func(grip: Array) -> bool: return grip[0] >= 0), "its jaws let go of the skin")
	assert_lt(tissue.gap_at(middle, MIDDLE), own_gape + 0.002, "the cut falls back to its own gape")
	await driver.capture("taken_out")
	await driver.player_puts_down()
	assert_true(driver.lies_on_tray(gelpi), "the retractor is put back on the tray")
	await _finish()


func test_gelpi_opened_too_far_tears_the_skin() -> void:
	await _start("appendectomy", "too_far")
	var patient := driver.patient
	var from := Vector2(0.4, 0.45)
	var to := from + Vector2(driver.body.meters_to_uv(0.03), 0.0)
	along_cut = driver.site_point(to) - driver.site_point(from)
	await driver.player_cuts_skin(from, to, 2)
	await driver.player_puts_down()
	var gelpi := await driver.player_sets_gelpi(from, to)
	assert_true(gelpi.in_wound, "set in a 3 cm cut")
	await driver.player_opens_gelpi(ToolActions.SPREAD_RANGE.y)
	await driver.seconds(1.0)
	assert_true(patient.flags.has("tears"), "opened %.0f cm across a 3 cm cut, the skin tears" % (gelpi.spread * 100.0))
	assert_true(driver.surgery.scoring.entries.has("skin_tear"), "a tear costs points")
	await driver.capture("torn")
	await _finish()


## Which side of the cut's middle each jaw holds the skin on, across the cut (it runs along u): opposite signs for two
## edges.
func _jaw_sides(gelpi: SurgicalTool, middle: Vector2) -> Array[float]:
	var tissue := driver.body.tissue
	var sides: Array[float] = [0.0, 0.0]
	for grip: Array in tissue.grips():
		for side in 2:
			if grip[0] == Patient.spreader_key(gelpi.uid, side):
				var at: Vector3 = tissue.pos[grip[1]]
				sides[side] = at.z / tissue.size.y + 0.5 - middle.y
	return sides


## Where the HUD's < and > point (screen), when they're shown.
func _aim_marks() -> Array[Vector2]:
	var marks: Array[Vector2] = []
	for jaw: Line2D in driver.surgery.hud._jaws:
		if jaw.visible and jaw.points.size() == 3:
			marks.append(jaw.points[1])
	return marks


## Starts `scenario_id` with the patient asleep (the cuts don't make them flinch: the setting, not what's tested), and
## in a run with key frames saves the case's under KEY_FRAMES/`case_name`, starting untouched: the site from above and
## obliquely along the cut, and what the surgeon sees.
func _start(scenario_id: String, case_name: String) -> void:
	RenderingServer.render_loop_enabled = false
	driver = Driver.new()
	add_child(driver)
	await driver.start(scenario_id)
	along_cut = Vector3.ZERO
	SurgeryState.patient_is_asleep(driver.patient)
	if KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(driver.surgery, KEY_FRAMES.path_join(case_name))
		driver.on_key_frame = func(key_frame: String) -> void:
			assert_true(await shots.capture(key_frame, along_cut), "%s: saved key frame %s" % [case_name, key_frame])
			assert_true(await shots.capture_view(key_frame), "%s: saved the surgeon's view %s" % [case_name, key_frame])
		await driver.capture("untouched")
	# Loading the room isn't gameplay: the frame budget counts from here.
	driver.budget.clear()


func _finish() -> void:
	driver.budget.check(self, shots != null)
	if shots:
		gut.p("key frames: %s" % shots.out_dir)
		shots.end()
		shots.queue_free()
		shots = null
	await driver.stop()
	driver.queue_free()
	RenderingServer.render_loop_enabled = true
