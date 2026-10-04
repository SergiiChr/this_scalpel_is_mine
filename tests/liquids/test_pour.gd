extends GutTest
## Iodine poured from the bottle: into the kidney dish as well as the iodine dish (the scenario flows fill that one).

const TAGS = ["smoke", "liquids", "tool_iodine_bottle", "tool_kidney_dish"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")


func test_the_iodine_bottle_pours_into_the_kidney_dish() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	var dish := SurgeryState.tool_is_on_tray(driver.surgery, "kidney_dish")
	await driver.frames(10)
	await driver.player_requests_item("iodine_bottle")
	await driver.player_pours_into(dish, 1.0)
	assert_gt(dish.ml, 10.0, "iodine pours into the kidney dish (%.1f ml)" % dish.ml)
	assert_almost_eq(float(dish.contents.get("iodine", 0.0)), dish.ml, 0.001, "all of it is iodine")
	assert_almost_eq(dish.iodine, 1.0, 0.001, "every peer sees the liquid is iodine")
	var pool := dish.find_child("Pool", true, false) as MeshInstance3D
	var albedo: Color = (pool.get_surface_override_material(0) as ShaderMaterial).get_shader_parameter("albedo")
	assert_true(pool.visible and albedo.is_equal_approx(SurgicalTool.IODINE_COLOR), "the pool shows in the dish, iodine brown (%s)" % albedo)
	await driver.player_pours_into(dish, 4.0)
	assert_almost_eq(dish.ml, dish.def.volume, 0.001, "poured on, it fills up to the rim and no further")
	await driver.stop()
