extends GutTest
## Aim tool (MMB held): the mouse turns the held tool about the wrist. Only the wrist moves, the tip follows the mouse,
## and let go, the tool settles back onto what it rested on. The hands start turned in, the tool pointing across beside
## the hand, and zoomed all the way in they're see-through. Headless assertions in smoke; with key frames also the
## site from above and obliquely and what the surgeon sees. Review them for the forearm and glove staying where they
## were while the scalpel swings right and tips up, the glove bending at the wrist without breaking from the cuff, the
## scalpel showing beside the hand at rest and the hands see-through zoomed in.

const TAGS = ["smoke", "tool_scalpel", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")
const FrameBudget := preload("res://tests/support/frame_budget.gd")
const KEY_FRAMES := "res://build/test-artifacts/screenshots/aim"


func test_aiming_turns_the_tool_about_the_wrist() -> void:
	RenderingServer.render_loop_enabled = false
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	SurgeryState.patient_is_asleep(driver.patient)
	var shots: KeyFrames = null
	if KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(driver.surgery, KEY_FRAMES)
		driver.on_key_frame = func(key_frame: String) -> void:
			assert_true(await shots.capture(key_frame), "saved key frame %s" % key_frame)
			assert_true(await shots.capture_view(key_frame), "saved the surgeon's view %s" % key_frame)
	var me := driver.me
	var scalpel := await driver.player_requests_item("scalpel")
	var site := driver.site_point(Vector2(0.5, 0.5))
	await driver.player_walks_to(site)
	await driver.player_reaches(site)
	await driver.frames(10)
	await driver.capture("resting")
	driver.budget.clear()
	var hand := me.hands[me.active]
	var rest_tip := me.to_local(scalpel.tip_position())
	var wrist := me.to_local(_wrist(hand))
	var elbow := me.to_local(hand._elbow)
	# Up first: the tip comes off the skin, so nothing under it (the belly breathing) pushes the hand up.
	await driver.player_aims(Vector2(0.0, -4.0), 30)
	var raised := me.to_local(scalpel.tip_position())
	assert_gt(raised.y - rest_tip.y, 0.02, "the mouse moved up lifts the tip off the skin (%.1f cm)" % ((raised.y - rest_tip.y) * 100.0))
	_check_still(me, hand, wrist, elbow, "tipped up")
	await driver.capture("aimed_up")
	await driver.player_aims(Vector2(4.0, 0.0), 30)
	var swung := me.to_local(scalpel.tip_position())
	assert_gt(swung.x - raised.x, 0.03, "the mouse moved right swings the tip right (%.1f cm)" % ((swung.x - raised.x) * 100.0))
	_check_still(me, hand, wrist, elbow, "swung right")
	await driver.capture("aimed_right")
	driver.player_lets_go_of_aim()
	await driver.seconds(0.5)
	var settled := me.to_local(scalpel.tip_position())
	assert_true(hand.raise == 0.0 and swung.y - settled.y > 0.02, "let go, the tip settles back down onto its spot (%.1f cm down)" % ((swung.y - settled.y) * 100.0))
	await driver.capture("let_go")
	var other := me.hands[1 - me.active]
	_check_faded(hand, other, 0.0, "the hands are solid at the first zoom step")
	driver.press("zoom")
	await driver.seconds(0.5)
	_check_faded(hand, other, Surgeon.ZOOM_SEE_THROUGH, "zoomed all the way in with a scalpel, both hands are see-through")
	await driver.capture("zoomed_in")
	driver.press("zoom")
	await driver.seconds(0.5)
	_check_faded(hand, other, 0.0, "zoomed back out, the hands are solid again")
	if shots and FrameBudget.enforced():
		assert_true(driver.budget.within(), driver.budget.summary())
	else:
		gut.p(driver.budget.summary())
	if shots:
		gut.p("key frames: %s" % shots.out_dir)
		shots.end()
		shots.queue_free()
	await driver.stop()
	driver.queue_free()
	RenderingServer.render_loop_enabled = true


## Only the wrist bends: it and the forearm stay where they were before aiming.
func _check_still(me: Surgeon, hand: SurgeonHand, wrist: Vector3, elbow: Vector3, what: String) -> void:
	var wrist_off := me.to_local(_wrist(hand)).distance_to(wrist)
	var elbow_off := me.to_local(hand._elbow).distance_to(elbow)
	assert_lt(wrist_off, 0.001, "%s, the wrist stays where it was (%.1f mm off)" % [what, wrist_off * 1000.0])
	assert_lt(elbow_off, 0.001, "%s, the forearm stays where it was (elbow %.1f mm off)" % [what, elbow_off * 1000.0])


func _check_faded(hand: SurgeonHand, other: SurgeonHand, amount: float, what: String) -> void:
	var faded := [_fade(hand), _fade(other)]
	assert_true(is_equal_approx(faded[0], amount) and is_equal_approx(faded[1], amount), "%s (%s)" % [what, faded])


## How see-through a hand is drawn (0 solid).
static func _fade(hand: SurgeonHand) -> float:
	var glove := hand.find_children("*", "GeometryInstance3D", true, false)[0] as GeometryInstance3D
	var ghost := glove.material_override as StandardMaterial3D
	return 1.0 - ghost.albedo_color.a if ghost else 0.0


## The wrist where the hand holds it: the glove's own frame, without its stress tremor and shiver.
static func _wrist(hand: SurgeonHand) -> Vector3:
	return hand._glove.global_position - hand.shiver - hand.tremor
