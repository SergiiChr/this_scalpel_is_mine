extends GutTest
## Gameplay runs on game time: a hitch in the frame rate, a slow machine or --fixed-fps changes nothing about what
## happens, only how fast it's seen.

const TAGS = ["smoke"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const Broken := preload("res://tests/support/broken.gd")
## Real time that passes between two frames, as a hitch would make it.
const HITCH_MSEC := 300
const TRIALS := 5


## Hand tremor is driven by Time.get_ticks_msec() (Surgeon._local_update()), so it follows the wall clock, not the
## game: a hitch jumps the hand by a whole shake, and runs under --fixed-fps shake differently from real time. That's
## the likely reason test_scenario_flows.gd::test_colon_cancer fails only beside another test script.
## The same wall clock decides other gameplay too: score throttling (Scoring.add()), toast throttling
## (Surgery.announce()), the bone scrape jolt (Patient.scrape_bone()), the tear notice (Patient._tear_notice()),
## the diazepam hand delay (Surgeon), how long an X-ray takes to develop (XrayCart) and sound and effect throttles.
## To fix: count these in game time (Surgery.elapsed, or a time summed from delta).
func test_tremor_follows_game_time() -> void:
	if not Broken.reproduce(self, "hand tremor (and other gameplay timing) follows the wall clock, not game time."):
		return
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("hand_stitch")
	var surgeon := driver.me
	var hand := surgeon.hands[surgeon.active]
	for trial in TRIALS:
		SurgeryState.surgeon_is_stressed(surgeon)
		await driver.frames(1)
		var before := hand.tremor
		var amount := surgeon.status.tremor_amount()
		OS.delay_msec(HITCH_MSEC)
		await driver.frames(1)
		# Over one frame (1/60 s) the shake's three sines (23, 31 and 19 rad/s) move it at most this far.
		var most := amount * Vector3(23.0, 31.0, 19.0).length() / 60.0
		assert_lte(hand.tremor.distance_to(before), most * 1.1, "one frame after a %d ms hitch the hand moved by one frame's shake" % HITCH_MSEC)
	await driver.stop()
	driver.queue_free()
