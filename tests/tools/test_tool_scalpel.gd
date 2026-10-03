extends GutTest
## The scalpel as a player uses it: asked for and taken off the tray, a light 5 cm cut along the blade's edge, the
## opening it leaves, and the scalpel put back on the tray.

const TAGS = ["smoke", "tool_scalpel", "tissue_modification"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const LENGTH := 0.05


func test_scalpel_pickup_five_centimeter_cut_and_table_drop() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	var patient := driver.patient
	var body := driver.body
	# Asleep, so the cut doesn't make them flinch: the setting, not what's tested.
	SurgeryState.patient_is_asleep(patient)
	var scalpel := await driver.player_requests_item("scalpel")
	assert_eq(driver.me.held_tool(driver.me.active), scalpel, "player_requests_item(scalpel) puts it in hand")

	var from := Vector2(0.4, 0.45)
	var to := from + Vector2(body.meters_to_uv(LENGTH), 0.0)
	await driver.player_cuts_skin(from, to, 1)
	var cuts := patient.wounds.filter(func(w: Wound) -> bool: return w.made_by_surgeon and w.kind == Wound.Kind.CUT)
	assert_eq(cuts.size(), 1, "one stroke makes one cut")
	if cuts.is_empty():
		return
	var cut: Wound = cuts[0]
	assert_almost_eq(body.uv_to_meters(cut.length_uv()), LENGTH, 0.005, "a light stroke along 5 cm cuts 5 cm")
	assert_true(cut.depth < Wound.MUSCLE_DEPTH and not body.is_open(cut.midpoint()), "a light cut stays in the skin")
	assert_almost_eq(_opening(body, from, to), LENGTH, 0.01, "the skin is open along the cut and closed past its ends")

	await driver.player_puts_down()
	assert_eq(scalpel.state, SurgicalTool.State.FREE, "the scalpel is put down")
	assert_true(driver.lies_on_tray(scalpel), "the scalpel lies on the instrument tray (%s)" % ToolManager.middle(scalpel))
	await driver.stop()


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
