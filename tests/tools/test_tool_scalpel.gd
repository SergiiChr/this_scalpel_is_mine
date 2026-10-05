extends GutTest
## The scalpel as a player uses it: asked for and taken off the tray, a light 5 cm cut along the blade's edge, the
## opening it leaves, and the scalpel put back on the tray.

const TAGS = ["smoke", "tool_scalpel", "tissue_modification", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const LENGTH := 0.05
const KeyFrames := preload("res://tests/support/key_frames.gd")
const KEY_FRAMES := "res://build/test-artifacts/screenshots/scalpel"

var _driver: Driver
var _scalpel: SurgicalTool
var _blade: Node3D
var _handle: Node3D
var _blade_rest: Transform3D
var _active_frames := 0
var _blade_drift := 0.0


func _process(_delta: float) -> void:
	if not is_instance_valid(_scalpel) or _scalpel.state != SurgicalTool.State.HELD:
		return
	var hand := _driver.me.hands[_scalpel.slot]
	if not ToolActions.in_use(_scalpel.def.action, hand.lowered, hand.trigger, hand.level):
		return
	_active_frames += 1
	var relative := _handle.global_transform.affine_inverse() * _blade.global_transform
	_blade_drift = maxf(_blade_drift, relative.origin.distance_to(_blade_rest.origin))


func test_scalpel_pickup_five_centimeter_cut_and_table_drop() -> void:
	RenderingServer.render_loop_enabled = false
	var driver: Driver = Driver.new()
	_driver = driver
	add_child(driver)
	await driver.start("appendectomy")
	var patient := driver.patient
	var body := driver.body
	# Asleep, so the cut doesn't make them flinch: the setting, not what's tested.
	SurgeryState.patient_is_asleep(patient)
	var scalpel := await driver.player_requests_item("scalpel")
	assert_eq(driver.me.held_tool(driver.me.active), scalpel, "player_requests_item(scalpel) puts it in hand")
	_scalpel = scalpel
	_blade = scalpel.find_child("Blade", true, false) as Node3D
	_handle = scalpel.find_child("Handle", true, false) as Node3D
	_blade_rest = _handle.global_transform.affine_inverse() * _blade.global_transform
	_active_frames = 0
	_blade_drift = 0.0

	var from := Vector2(0.4, 0.45)
	var to := from + Vector2(body.meters_to_uv(LENGTH), 0.0)
	var start := driver.site_point(from)
	var finish := driver.site_point(to)
	var middle := (start + finish) * 0.5
	var shots: KeyFrames
	if KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(driver.surgery, KEY_FRAMES)
		driver.on_key_frame = func(key_frame: String) -> void:
			assert_true(await shots.capture_at(key_frame, middle, 0.18), "saved scalpel key frame %s" % key_frame)
	await driver.player_walks_to(middle)
	await driver.player_turns_blade(finish - start)
	await driver.player_reaches(start)
	await driver.set_level(1)
	await driver.capture("untouched")
	driver.budget.clear()
	driver.note("presses the scalpel into skin")
	driver.use()
	await driver.seconds(0.3)
	driver.note("draws the first half of the scalpel cut")
	await driver.player_sweeps_to(middle)
	await driver.capture("cutting")
	driver.note("draws the second half of the scalpel cut")
	await driver.player_sweeps_to(finish)
	driver.note("releases the scalpel from skin")
	driver.use(false)
	await driver.frames(3)
	assert_gt(_active_frames, 60, "blade stability is measured throughout an actual cutting stroke")
	assert_lt(_blade_drift, 0.000001, "the blade stays fixed to its handle while cutting (%.3f mm drift)" % (_blade_drift * 1000.0))
	await driver.capture("released")
	var cuts := patient.wounds.filter(func(w: Wound) -> bool: return w.made_by_surgeon and w.kind == Wound.Kind.CUT)
	assert_eq(cuts.size(), 1, "one stroke makes one cut")
	if cuts.is_empty():
		return
	var cut: Wound = cuts[0]
	assert_almost_eq(body.uv_to_meters(cut.length_uv()), LENGTH, 0.005, "a light stroke along 5 cm cuts 5 cm")
	assert_true(cut.depth < Wound.MUSCLE_DEPTH and not body.is_open(cut.midpoint()), "a light cut stays in the skin")
	assert_almost_eq(_opening(body, from, to), LENGTH, 0.01, "the skin is open along the cut and closed past its ends")

	driver.note("returns the scalpel to the tray")
	await driver.player_puts_down()
	assert_eq(scalpel.state, SurgicalTool.State.FREE, "the scalpel is put down")
	assert_true(driver.lies_on_tray(scalpel), "the scalpel lies on the instrument tray (%s)" % ToolManager.middle(scalpel))
	driver.budget.check(self, shots != null)
	if shots:
		shots.end()
		shots.queue_free()
	_scalpel = null
	await driver.stop()
	driver.queue_free()
	RenderingServer.render_loop_enabled = true


## Meters of skin open along the line from `from` to `to`, looked for a centimeter past both ends.
func _opening(body: PatientBody, from: Vector2, to: Vector2) -> float:
	var step := body.meters_to_uv(0.001)
	var along := (to - from).normalized()
	var past := body.meters_to_uv(0.01)
	var open := 0
	for i in int((from.distance_to(to) + past * 2.0) / step):
		if body.tissue.is_open(from - along * past + along * step * i, TissueSim.Depth.SKIN):
			open += 1
	return body.uv_to_meters(open * step)
