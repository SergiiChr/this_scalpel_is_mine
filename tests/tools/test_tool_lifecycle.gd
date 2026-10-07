extends GutTest
## Every tool a hand can hold, used like a player: taken off the instrument tray, brought to the patient and lowered
## onto the site with Use tool, then put back on the tray. What each tool then does is tested with its feature
## (tissue, liquids, the scenario flows).

const TAGS = ["slow", "smoke", "tool_all"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")


func test_every_handheld_tool_is_picked_up_lowered_onto_the_site_and_put_back() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	var surgery := driver.surgery
	SurgeryState.patient_is_asleep(driver.patient)
	var site := driver.site_point(Vector2(0.5, 0.5))
	var ids: Array = Db.tools.keys().filter(func(id: String) -> bool: return not Db.tool(id).fixed)
	ids.sort()
	for id: String in ids:
		# A fully stocked tray: whatever isn't on it is laid in its clear strip, and taken away again after.
		var extra := driver.free_tools(id).is_empty()
		if extra:
			SurgeryState.tool_is_on_tray(surgery, id)
			await driver.frames(10)
		var hand := driver.me.hands[driver.me.active]
		# Aim the empty hand down before selecting a tool, as a player can.
		var aim := Vector2(hand.turn - 0.12, hand.tilt - (SurgeonHand.REST_TILT + 0.05)) / (Surgeon.AIM_SENSITIVITY * Settings.mouse_sensitivity)
		await driver.player_aims(aim, 1)
		driver.player_lets_go_of_aim()
		await driver.frames(10)
		var tilt := hand.tilt
		var turn := hand.turn
		var tool := await driver.player_requests_item(id)
		assert_true(tool != null and tool.def.id == id and tool.state == SurgicalTool.State.HELD, "%s is picked up\n%s" % [id, driver.recent(4)])
		if tool == null or tool.def.id != id:
			continue
		# A needle holder and a syringe come up in their own carry pose (SurgeonHand.default_tilt(), Surgeon._face_syringe()).
		if not tool.def.grip in ["needle", "syringe"]:
			if tool.def.action != "spread":
				assert_eq(hand.tilt, tilt, "%s pickup preserves the player's tilt" % id)
			else:
				assert_eq(hand.tilt, SurgeonHand.SPREADER_TILT, "the spreader is held tipped toward the skin, its points down")
			assert_eq(hand.turn, turn, "%s pickup preserves the player's turn" % id)
		await driver.player_walks_to(site)
		await driver.player_reaches(site)
		driver.use()
		var touched := false
		for i in 30:
			await driver.frames(1)
			touched = touched or driver.body.probe(tool.tip_position()).zone in ["site", "cavity", "body"]
		driver.use(false)
		assert_true(touched, "%s comes down onto the site" % id)
		if tool.state == SurgicalTool.State.HELD:
			await driver.player_puts_down()
			assert_true(driver.lies_on_tray(tool), "%s is put back on the tray (%s)\n%s" % [id, ToolManager.middle(tool), driver.recent(4)])
		if extra:
			surgery.tools.consume(tool)
			await driver.frames(2)
	await driver.stop()


func test_a_held_tool_moves_with_the_hand_while_walking() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	var scalpel := await driver.player_requests_item("scalpel")
	# Between the table and the delivery tray, walking sideways along the table over clear floor.
	await driver.player_walks_to(Vector3(0.0, 0.0, 1.6), 0.4)
	var hand := driver.me.hands[driver.me.active]
	var start := driver.me.global_position
	var worst := [0.0]
	await driver.player_holds_walk_key("move_right", 0.5, func() -> void:
		worst[0] = maxf(worst[0], scalpel.global_position.distance_to(hand.grip_transform().origin)))
	assert_gt(driver.me.global_position.distance_to(start), 0.5, "the surgeon walks")
	assert_lt(worst[0], 0.001, "the scalpel stays in the hand every frame of the walk (%.1f mm off at worst)" % (worst[0] * 1000.0))
	await driver.stop()


func test_drinks_and_cigarettes_are_used_up_one_at_a_time() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	for id: String in Db.tools.keys():
		var def := Db.tool(id)
		if not def.drinkable and id != "cig_pack":
			continue
		SurgeryState.tool_is_on_tray(driver.surgery, id)
		await driver.frames(10)
		var tool := await driver.player_requests_item(id)
		var before := tool.charges
		if def.drinkable:
			driver.press("drink")
		else:
			await driver.player_interacts("Smoke a cigarette")
		await driver.seconds(1.0)
		assert_eq(tool.charges, before - 1, "%s is used once" % id)
		if driver.me.held_tool(driver.me.active):
			await driver.player_puts_down()
	await driver.stop()


func test_fixed_tools_have_a_station_interaction_instead_of_a_hand_lifecycle() -> void:
	var fixed: Array = Db.tools.values().filter(func(def: ToolDef) -> bool: return def.fixed)
	assert_eq(fixed.map(func(def: ToolDef) -> String: return def.id), ["iv_drip"], "only the IV drip is fixed")
	assert_eq(fixed[0].action, "drip", "the fixed IV bag has the line interaction covered by the liquids suite")


func test_a_tool_dropped_on_the_floor_is_soiled() -> void:
	var driver: Driver = Driver.new()
	add_child(driver)
	await driver.start("appendectomy")
	var scalpel := await driver.player_requests_item("scalpel")
	# Away from the table and the tray, over bare floor.
	var floor_spot := Vector3(0.0, 0.0, 1.6)
	await driver.player_walks_to(floor_spot, 0.4)
	await driver.player_reaches(floor_spot)
	driver.press("grab")
	await driver.seconds(2.0)
	assert_eq(scalpel.state, SurgicalTool.State.FREE, "the scalpel is let go")
	assert_true(scalpel.soiled and not scalpel.sterile, "a scalpel dropped on the floor is soiled and no longer sterile")
	await driver.stop()

