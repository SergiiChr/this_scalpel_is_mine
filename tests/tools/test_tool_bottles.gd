extends GutTest
## Bottles as a player handles them: a vial ordered from the nurse comes standing on the delivery tray, cap up. Picked
## up, Grab held a second stands it upright where it's held: on the instrument tray, or on the patient's belly, resting
## on the skin. A quick click puts it down the way any tool goes down, lying. Headless assertions in smoke; with key
## frames also each bottle from above and obliquely. Review them for the vial standing straight on its base, resting on
## what's under it (not floating, not sunk into the tray or the skin) and lying flat after the click.

const TAGS = ["smoke", "tool_vial_propofol", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")
const KEY_FRAMES := "res://build/test-artifacts/screenshots/bottles"
const VIAL := "vial_propofol"
## How far from a bottle the key frames look at it (meters).
const VIEW := 0.3

var driver: Driver
var shots: KeyFrames
var vial: SurgicalTool


func test_bottles_come_standing_and_stand_when_grab_is_held() -> void:
	RenderingServer.render_loop_enabled = false
	driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	SurgeryState.patient_is_asleep(driver.patient)
	if KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(driver.surgery, KEY_FRAMES)
		driver.on_key_frame = func(key_frame: String) -> void:
			assert_true(await shots.capture_at(key_frame, ToolManager.middle(vial), VIEW), "saved key frame %s" % key_frame)
	driver.budget.clear()
	var surgery := driver.surgery
	var on_tray := driver.free_tools(VIAL)
	await driver.player_orders(PackedStringArray([VIAL]))
	await driver.wait_until(func() -> bool: return driver.free_tools(VIAL).size() > on_tray.size(), 60.0)
	await driver.seconds(1.0)
	vial = driver.free_tools(VIAL).filter(func(t: SurgicalTool) -> bool: return not t in on_tray).front()
	assert_true(_stands(vial), "a vial comes from the nurse standing, cap up (tip %.2f up)" % _tip_up(vial))
	assert_lt(vial.linear_velocity.length(), 0.01, "it stands still on the delivery tray")
	await driver.capture("delivered")

	await _pick_up()
	var spot := SurgeryState.free_tray_spot(surgery)
	await driver.player_walks_to(spot)
	await driver.player_reaches(spot - (ToolManager.middle(vial) - vial.tip_position()))
	await _hold_grab()
	assert_true(vial.state == SurgicalTool.State.FREE and _stands(vial) and driver.lies_on_tray(vial), "Grab held a second stands the vial upright on the tray (tip %.2f up)" % _tip_up(vial))
	await driver.capture("stood_on_tray")

	await _pick_up()
	var belly := driver.site_point(Vector2(0.5, 0.5))
	await driver.player_walks_to(belly)
	await driver.player_reaches(belly - (ToolManager.middle(vial) - vial.tip_position()))
	await _hold_grab()
	# Its base's middle against the skin as it's drawn there. It settles on the patient's collider, which over the site
	# lies a little above the drawn skin; inside the body (on the table) it would be far below.
	var above := driver.body.height_above_site(vial.global_position)
	assert_true(_stands(vial) and above > -0.005 and above < 0.03, "stood on the patient's belly, the vial rests on the body, not inside it (base %.1f cm over the skin)" % (above * 100.0))
	await driver.capture("stood_on_patient")

	await _pick_up()
	await driver.player_puts_down()
	assert_true(vial.state == SurgicalTool.State.FREE and not _stands(vial) and driver.lies_on_tray(vial), "a quick click puts it down lying, as before (tip %.2f up)" % _tip_up(vial))
	await driver.capture("put_down")

	driver.budget.check(self, shots != null)
	if shots:
		gut.p("key frames: %s" % shots.out_dir)
		shots.end()
		shots.queue_free()
	await driver.stop()
	RenderingServer.render_loop_enabled = true


func _pick_up() -> void:
	await driver.player_walks_to(vial.global_position)
	await driver.player_reaches(vial.global_position)
	driver.tap("grab")
	await driver.frames(5)
	assert_eq(driver.me.held_tool(driver.me.active), vial, "the vial is picked up")


## Grab held a little over a second, then let go: the bottle in the hand stands upright where it is.
func _hold_grab() -> void:
	driver.press("grab")
	await driver.seconds(Surgeon.STAND_HOLD + 0.2)
	driver.release("grab")
	await driver.seconds(1.0)


## How far up a tool's tip end points (1: straight up).
static func _tip_up(tool: SurgicalTool) -> float:
	return (-tool.global_basis.z.normalized()).y


static func _stands(tool: SurgicalTool) -> bool:
	return _tip_up(tool) > 0.95
