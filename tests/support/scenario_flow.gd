extends GutTest
## A scenario played through its positive flow, like a player: each required objective done in order with the
## scenario's tools and the controls a player has (surgery_driver.gd), and the game itself ending the surgery with a
## successful report. A step that doesn't register fails the case with what the driver did last.
## A case that asks for key frames, in a run with key frames (KeyFrames.wanted()), also saves the site before anything
## is done and right after every objective, and fails when a frame takes longer than the frame budget (not on CI).
## Rendering stays off, also under a display (visual tests): the game runs at full speed, and a software renderer only
## draws the key frames.

const Driver := preload("res://tests/support/surgery_driver.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")
const FrameBudget := preload("res://tests/support/frame_budget.gd")
## Game seconds an objective gets to register once its step is done, on top of how long it has to hold.
const SETTLE := 20.0
const KEY_FRAMES := "res://build/test-artifacts/screenshots/scenarios"


func play(scenario_id: String, key_frames: bool = false) -> void:
	RenderingServer.render_loop_enabled = false
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start(scenario_id)
	var surgery := driver.surgery
	var objectives := surgery.objectives
	var shots: KeyFrames = null
	if key_frames and KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(surgery, KEY_FRAMES.path_join(scenario_id))
		driver.on_key_frame = func(key_frame: String) -> void:
			assert_true(await shots.capture(key_frame), "%s: saved key frame %s" % [scenario_id, key_frame])
		await driver.capture("untouched")
	# Loading the room isn't gameplay: the frame budget counts from here.
	driver.budget.clear()
	for index in objectives.steps.size():
		var step := objectives.steps[index]
		if step.get("optional", false):
			continue
		await driver.player_completes(step)
		var hold: float = step.get("seconds", 0.0) + (60.0 if step.type == "calm" else 0.0)
		# A drug counts once enough has soaked in to work: through a line, most of it by 1.5 times its onset.
		if step.type == "inject" and step.has("drug"):
			hold += Db.drug(step.drug).onset * 1.5
		await driver.wait_until(func() -> bool: return objectives.states[index].done or surgery.finished, hold + SETTLE)
		if not objectives.states[index].done:
			fail_test("%s: \"%s\" (%s) didn't complete. Last steps:\n%s" % [scenario_id, step.label, step.type, driver.recent()])
			break
		await driver.capture("%s_done" % step.type)
	await driver.wait_until(func() -> bool: return surgery.finished, 10.0)
	var report := surgery.report
	assert_true(report.get("success", false), "%s ends in success: %s, %.0f s, %d stars" % [scenario_id, report.get("reason", "not finished"), surgery.elapsed, report.get("stars", 0)])
	if shots and FrameBudget.enforced():
		assert_true(driver.budget.within(), "%s: %s" % [scenario_id, driver.budget.summary()])
	else:
		gut.p("%s: %s" % [scenario_id, driver.budget.summary()])
	if shots:
		gut.p("%s key frames: %s" % [scenario_id, shots.out_dir])
		shots.end()
		shots.queue_free()
	await driver.stop()
	driver.queue_free()
	RenderingServer.render_loop_enabled = true
