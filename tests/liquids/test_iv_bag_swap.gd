extends GutTest
## Hanging a bag on the IV stand ("Swap IV bag") as a player does it.

const TAGS = ["liquids", "tool_saline_bag"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const Broken := preload("res://tests/support/broken.gd")


## With no line in a vein, Surgery._req_iv() still uses up the held bag after Patient.administer() refused to give it
## (only the toast "Nothing happens. There's no IV line in." says so), and refills the hung bag with its fluid. On the
## sidewalk and in the ambulance there's no nurse, so a lost saline or blood bag can't be replaced.
## To fix: check patient.iv_working() first in _req_iv() and keep the bag (and the hung one) as they were.
func test_bag_hung_without_a_line_is_kept() -> void:
	if not Broken.reproduce(self, "\"Swap IV bag\" uses up the held bag although no line is in and nothing ran."):
		return
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("hand_stitch")
	var bag := await driver.player_requests_item("saline_bag")
	await driver.player_interacts("Swap IV bag")
	await driver.frames(10)
	assert_false(driver.patient.iv_set, "no line is in")
	assert_ne(bag.state, SurgicalTool.State.CONSUMED, "the bag is still there to hang once a line is in")
	assert_eq(bag.charges, 1, "the bag is still full")
	await driver.stop()
	driver.queue_free()
