extends GutTest
## The nurse's shop as a player uses it: the bell opens categories with their items as a list, [+] adds to the cart up
## to five items (the same one twice too), [-] takes one back out, and Place order sends the cart as one batch.
## It takes as long as its slowest item, and every item lands on the delivery tray.
## While the nurse is out, Place order waits.
## With key frames also the shop with a full cart and the delivery tray afterwards.
## Review them for legible rows, the picked category lit, the cart's lines and counts, and the five items lying side by
## side on the tray, not on or in each other.

const TAGS = ["smoke", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")
const FrameBudget := preload("res://tests/support/frame_budget.gd")
const KEY_FRAMES := "res://build/test-artifacts/screenshots/nurse_shop"
## Two of the same, from three categories, and a sixth that doesn't fit.
const WANTED := ["gauze", "gauze", "scalpel", "hemostat", "vial_propofol", "needle"]
const ORDERED := ["gauze", "gauze", "scalpel", "hemostat", "vial_propofol"]


func test_cart_of_five_with_a_repeat_is_delivered_as_one_batch() -> void:
	RenderingServer.render_loop_enabled = false
	var driver := Driver.new()
	add_child_autofree(driver)
	await driver.start("appendectomy")
	var shots: KeyFrames = null
	if KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(driver.surgery, KEY_FRAMES)
		# The shop as the player sees it; the delivery tray from above and obliquely.
		driver.on_key_frame = func(key_frame: String) -> void:
			var tray: Vector3 = driver.surgery.room.layout.delivery_tray + Vector3(0, 0.92, 0)
			var saved: bool = await shots.capture_view(key_frame) if key_frame == "cart_full" else await shots.capture_at(key_frame, tray, 0.6)
			assert_true(saved, "saved key frame %s" % key_frame)
	driver.budget.clear()
	var surgery := driver.surgery
	var before := surgery.tools.tools.size()

	await driver.player_fills_cart(PackedStringArray(WANTED))
	assert_not_null(driver.shop_button("add", "needle"), "the sixth item's category lists it")
	assert_true(driver.shop_button("add", "needle").disabled, "[+] is off once the cart holds %d" % Nurse.BATCH)
	var shown := "\n".join(surgery.hud.find_children("*", "Label", true, false).filter(func(l: Label) -> bool: return l.is_visible_in_tree()).map(func(l: Label) -> String: return l.text))
	assert_string_contains(shown, "Cart  5 / 5", "the cart counts five")
	assert_string_contains(shown, "Gauze  ×2", "the same item twice shows as one line with its count")
	assert_false(shown.contains("Suture needle  ×"), "the sixth item didn't fit")
	await driver.capture("cart_full")
	# [-] takes one back out, [+] puts it back, so a full cart can still change.
	driver.player_picks_category(Db.tool("gauze").category)
	driver.player_removes_from_cart("gauze")
	assert_false(driver.shop_button("add", "gauze").disabled, "[-] makes room in a full cart")
	driver.shop_button("add", "gauze").pressed.emit()
	assert_true(driver.shop_button("add", "gauze").disabled, "[+] fills it again")
	await driver.player_places_order()
	assert_null(driver.shop_button("place", true), "placing the order closes the shop")

	var order := surgery.nurse.order()
	assert_false(order.is_empty(), "the nurse takes the cart as one order")
	if order.is_empty():
		await _stop(driver, shots)
		return
	assert_eq(order[0], "Gauze ×2, Scalpel, Hemostat clamp, Propofol 10 mg/ml, 50 ml", "the board names the batch with its repeats")
	var slowest := 0.0
	for id: String in ORDERED:
		slowest = maxf(slowest, Nurse.delivery_time(Db.tool(id), driver.me, surgery))
	assert_almost_eq(float(order[1]), slowest, 0.1, "the batch takes as long as its slowest item")
	# Back at the bell while she's out: the next cart waits for her, it isn't sent and lost.
	await driver.player_fills_cart(PackedStringArray(["gauze"]))
	await driver.seconds(Surgery.STATUS_INTERVAL + 0.1)
	assert_true(driver.shop_button("place", true).disabled, "Place order waits while the nurse is fetching")
	await driver.wait_until(func() -> bool: return surgery.nurse.order().is_empty(), slowest + 5.0)
	await driver.seconds(Surgery.STATUS_INTERVAL + 0.1)
	assert_false(driver.shop_button("place", true).disabled, "Place order comes back once she's delivered")
	surgery.hud.close_overlay()
	await driver.seconds(2.0)
	var delivered: Array = surgery.tools.tools.values().slice(before)
	var ids: Array = delivered.map(func(t: SurgicalTool) -> String: return t.def.id)
	ids.sort()
	var expected := ORDERED.duplicate()
	expected.sort()
	assert_eq(ids, expected, "everything in the cart is delivered, both gauzes too")
	var tray: Vector3 = surgery.room.layout.delivery_tray
	for tool: SurgicalTool in delivered:
		var off := Vector2(tool.global_position.x - tray.x, tool.global_position.z - tray.z).length()
		assert_true(tool.global_position.y > 0.85 and off < 0.35, "the %s lands on the delivery tray (%.2f m off, %.2f m up)" % [tool.def.id, off, tool.global_position.y])
	# Side by side, not on each other: no two of them rest on top of one another.
	for a: SurgicalTool in delivered:
		for b: SurgicalTool in delivered:
			if a != b:
				var apart := ToolManager.middle(a) - ToolManager.middle(b)
				assert_false(absf(apart.y) > 0.015 and apart.slide(Vector3.UP).length() < 0.06, "the %s doesn't lie on the %s" % [a.def.id, b.def.id])
	await driver.capture("delivered")
	await _stop(driver, shots)


func _stop(driver: Driver, shots: KeyFrames) -> void:
	if shots and FrameBudget.enforced():
		assert_true(driver.budget.within(), driver.budget.summary())
	else:
		gut.p(driver.budget.summary())
	if shots:
		gut.p("key frames: %s" % shots.out_dir)
		shots.end()
		shots.queue_free()
	await driver.stop()
	RenderingServer.render_loop_enabled = true
