extends GutTest
## Iodine poured from the bottle while Use tool is held, into any dish (ToolDef.is_dish()), and a cotton pad dipped in
## it: the kidney dish works like the iodine dish (the scenario flows use that one).

const TAGS = ["smoke", "liquids", "tool_iodine_bottle", "tool_kidney_dish", "tool_cotton_pad"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")


func test_the_iodine_bottle_pours_while_held_into_any_dish() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	var kidney := SurgeryState.tool_is_on_tray(driver.surgery, "kidney_dish")
	await driver.frames(10)
	var bottle := await driver.player_requests_item("iodine_bottle")
	assert_false(driver.me.uses_level(driver.me.active), "the bottle has no pouring level on the wheel")
	for dish: SurgicalTool in [driver.free_tools("iodine_dish")[0], kidney]:
		await driver.player_pours_into(dish, 1.0)
		var poured := bottle.def.power * 1.0
		assert_almost_eq(dish.ml, minf(poured, dish.def.volume), 2.0, "a second of Use tool held pours %.0f ml into the %s (%.1f ml)" % [poured, dish.def.name, dish.ml])
		assert_almost_eq(float(dish.contents.get("iodine", 0.0)), dish.ml, 0.001, "all of it is iodine")
		assert_almost_eq(dish.iodine, 1.0, 0.001, "every peer sees the liquid is iodine")
		var part := dish.find_child("Pool", true, false) as MeshInstance3D
		if part == null:
			part = dish.find_child("Liquid", true, false) as MeshInstance3D
		assert_true(part.visible, "the iodine shows in the %s" % dish.def.name)
	var pool := kidney.find_child("Pool", true, false) as MeshInstance3D
	var albedo: Color = (pool.get_surface_override_material(0) as ShaderMaterial).get_shader_parameter("albedo")
	assert_true(albedo.is_equal_approx(SurgicalTool.IODINE_COLOR), "the kidney dish's pool is iodine brown (%s)" % albedo)
	await driver.player_pours_into(kidney, 6.0)
	assert_almost_eq(kidney.ml, kidney.def.volume, 0.001, "poured on, it fills up to the rim and no further")
	await driver.stop()


func test_a_cotton_pad_dipped_in_the_kidney_dish_sanitizes_the_site() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	SurgeryState.patient_is_asleep(driver.patient)
	var kidney := SurgeryState.tool_is_on_tray(driver.surgery, "kidney_dish")
	await driver.frames(10)
	await driver.player_sanitizes_site(0.2, "kidney_dish")
	assert_gt(driver.patient.sanitized_fraction(), 0.2, "pads dipped in iodine in the kidney dish sanitize the site (%.2f)\n%s" % [driver.patient.sanitized_fraction(), driver.recent(8)])
	# The driver pours for 3 s; a soaked pad takes up to ToolActions.PAD_ML of it.
	var poured := Db.tool("iodine_bottle").power * 3.0
	var left: float = kidney.contents.get("iodine", 0.0)
	assert_lt(left, poured - 1.0, "the pads took iodine out of the kidney dish (%.0f of %.0f ml left)" % [left, poured])
	await driver.stop()
