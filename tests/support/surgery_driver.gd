extends Node
## Plays a surgery the way a player does, for GUT tests. It walks up to things, picks tools up from the tray or orders
## them from the nurse, steers the active hand across the floor and works the controls: Use tool, the wheel, Grab and
## Interact. The game does the rest exactly as for a player: the surgeon's hand settles onto what's under it, and the
## host runs what each held tool does (ToolManager -> ToolActions).
## Add it as a child of the test, start() a scenario, then call the player_* steps (player_requests_item(),
## player_incises(), player_closes_wounds()...), built on the input and movement primitives. A step does its work and
## returns; tests assert on the game's state. Direct state changes for a test's setup live apart, in surgery_state.gd.
## Walking is a step to where the surgeon wants to stand: no route is walked, so nothing is bumped on the way.

const SURGERY := preload("res://scenes/surgery.tscn")
const FrameBudget := preload("res://tests/support/frame_budget.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")

## How far from what it works on (meters, across the floor) the surgeon stands.
const STAND_OFF := 0.45
## A hand move across the floor this slow (m/s) keeps cuts clean and pulls slow (ToolActions, Patient.update_grip()).
const SLOW := 0.02
## The right hand works; the left holds what a second tool needs.
const RIGHT := 1
const LEFT := 0

var surgery: Surgery
var me: Surgeon
var patient: Patient
var body: PatientBody
## Game work per frame while the surgery runs, unless paused (a screenshot being drawn isn't gameplay).
var budget := FrameBudget.new()
var budget_paused := false
## What the steps did, newest last: test failure messages show the tail.
var trail: Array[String] = []
## Called (and awaited) with a name right after a step's key interaction: a cut made, a target out. Unset: no pictures.
var on_key_frame: Callable


## Starts `scenario_id` solo, like the menu's single player: one surgeon who rolled nothing special, a patient
## with only `patient_quirks` (rolls, {"id", "variant"}) and every tool the scenario lists on the tray, so the run only
## depends on the scenario.
## random_events: false leaves the escalation events out (scripted ones in the scenario still happen).
func start(scenario_id: String, random_events: bool = false, seed_value: int = 1, patient_quirks: Array = []) -> void:
	# The game's own unseeded rolls (where the nurse leaves things, jitter) come out the same every run too.
	seed(seed_value)
	Net.leave()
	Net.scenario_id = scenario_id
	Net.session_seed = seed_value
	Net.roster = {1: {"name": "Driver", "quirks": [{"id": "normal_dude", "variant": ""}], "ready": true}}
	Net.patient_quirks = patient_quirks
	Net.run_modifiers = []
	# The tray as the scenario lists it: a missing tool is a twist for another test.
	var scenario := Db.scenario(scenario_id)
	var missing := scenario.missing_tool_chance
	scenario.missing_tool_chance = 0.0
	surgery = SURGERY.instantiate()
	add_child(surgery)
	scenario.missing_tool_chance = missing
	await frames(10)
	me = surgery.local_surgeon
	patient = surgery.patient
	body = patient.body
	if not random_events:
		SurgeryState.random_events_are_off(surgery)
	me.active = RIGHT
	note("started %s" % scenario_id)


func stop() -> void:
	if surgery:
		surgery.queue_free()
		surgery = null
	await get_tree().process_frame
	await get_tree().process_frame


# --- Time --------------------------------------------------------------------------------------------


func frames(count: int) -> void:
	for i in count:
		await get_tree().physics_frame


func seconds(time: float) -> void:
	await frames(int(time * Engine.physics_ticks_per_second))


## Waits until `done` returns true or `limit` seconds of game time pass. Returns whether it came true.
func wait_until(done: Callable, limit: float) -> bool:
	for i in int(limit * Engine.physics_ticks_per_second):
		if done.call():
			return true
		await get_tree().physics_frame
	return done.call()


func _process(_delta: float) -> void:
	if surgery and surgery.running and not budget_paused:
		budget.sample(trail[-1] if trail else "")


func note(text: String) -> void:
	trail.append("%6.1f s  %s" % [surgery.elapsed if surgery else 0.0, text])
	if OS.get_environment("SURGERY_TRACE") == "1":
		print("[%s] %s" % [Net.scenario_id, trail[-1]])


## The last steps, for a failure message.
func recent(count: int = 12) -> String:
	return "\n".join(trail.slice(maxi(trail.size() - count, 0)))


func capture(key_frame: String) -> void:
	if on_key_frame.is_valid():
		budget_paused = true
		await on_key_frame.call(key_frame)
		budget_paused = false
		budget.resume()


# --- Controls ------------------------------------------------------------------------------------------


## Presses (and with release, lets go of) an input action, as the keyboard or mouse sends it.
func press(action: String, pressed: bool = true) -> void:
	var event := InputEventAction.new()
	event.action = action
	event.pressed = pressed
	me._unhandled_input(event)


## Presses (and with pressed false, lets go of) an input action through the engine's input, as a key or mouse button
## does: it reaches the game when the engine dispatches input, between physics frames, not straight away.
func press_key(action: String, pressed: bool = true) -> void:
	var event := InputEventAction.new()
	event.action = action
	event.pressed = pressed
	Input.parse_input_event(event)


## A click of an input action through the engine's input (see press_key()).
func tap_key(action: String) -> void:
	press_key(action)
	press_key(action, false)


func release(action: String) -> void:
	press(action, false)


## Presses and lets go of an action straight away, like a click: Grab puts a held bottle down rather than standing it.
func tap(action: String) -> void:
	press(action)
	press(action, false)


## One wheel notch on the active hand: its effort level, or a syringe's plunger (up pushes it in).
func notch(up: bool) -> void:
	press("level_up" if up else "level_down")
	await frames(2)


func set_level(level: int) -> void:
	var hand := me.hands[me.active]
	for i in 4:
		if hand.level == level:
			return
		await notch(level > hand.level)


func use(on: bool = true) -> void:
	press("use_tool", on)


func switch_to(hand: int) -> void:
	if me.active != hand:
		press("move_right_hand" if hand == RIGHT else "move_left_hand")
		release("move_right_hand" if hand == RIGHT else "move_left_hand")
		await seconds(0.3)


# --- Moving ---------------------------------------------------------------------------------------------


## Stands at the table's long side nearest `point`, facing it, close enough to reach it with either hand.
## Off the table (the tray, the IV stand) it stands on the side facing the room's middle. Either way it keeps its feet
## off the IV tubing, like a player stepping around it (walking into it rips the line out).
func player_walks_to(point: Vector3, off: float = STAND_OFF) -> void:
	# Holding onto something with the other hand, stay put while the point is in reach: walking off would drag it along.
	var holding := me.hands.any(func(h: SurgeonHand) -> bool: return h.attached)
	if holding and point.distance_to(me.shoulder(me.active)) < Surgeon.REACH - 0.05:
		return
	var table := patient.global_position
	var flat := point * Vector3(1, 0, 1)
	var spots: Array[Vector3] = []
	if absf(point.x - table.x) < 1.1 and absf(point.z - table.z) < 0.5:
		var side := signf(point.z - table.z) if absf(point.z - table.z) > 0.05 else signf(me.global_position.z - table.z)
		for s: float in [side, -side]:
			for along: float in [0.0, 0.15, -0.15, 0.3, -0.3, 0.45, -0.45, 0.6, -0.6]:
				spots.append(Vector3(point.x + along, 0.0, table.z + s * maxf(absf(point.z - table.z) + off, 0.55)))
	else:
		var out := (Vector3.ZERO - flat).normalized()
		for away: float in [off, off + 0.15]:
			for turn in [0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6]:
				spots.append(flat + out.rotated(Vector3.UP, turn * PI / 6.0) * away)
	# The first of them, in that order of preference, clear of the tubing.
	var clear := spots.filter(_clear_of_tubing)
	var spot: Vector3 = clear[0] if not clear.is_empty() else spots[0]
	spot.y = me.global_position.y
	me.global_position = spot
	var facing := (point - spot) * Vector3(1, 0, 1)
	me.rotation.y = atan2(-facing.x, -facing.z)
	await frames(3)


## Holds a walking key (move_forward, move_back, move_left, move_right) for `time` seconds, so the body walks the way
## a player's does. `each_frame` is called once every frame of it, after the frame's physics.
func player_holds_walk_key(action: String, time: float, each_frame: Callable) -> void:
	Input.action_press(action)
	for i in int(time * Engine.physics_ticks_per_second):
		await get_tree().process_frame
		each_frame.call()
	Input.action_release(action)
	note("walked (%s) for %.1f s" % [action, time])


func _clear_of_tubing(spot: Vector3) -> bool:
	var line: IvLine = surgery.room.iv_line
	if line == null or not line.is_attached():
		return true
	for p in line._points:
		if p.y < IvLine.TRIP_HEIGHT and Vector2(p.x - spot.x, p.z - spot.z).length() < IvLine.TRIP_DISTANCE + 0.12:
			return false
	return true


## Steers the active hand so its tool's tip (or the empty hand's fingertips) is over `point`, across the floor. The
## game decides the height: hovering, or resting on what's under it while Use tool is held.
func player_reaches(point: Vector3) -> void:
	var hand := me.hands[me.active]
	for i in 8:
		var miss := (point - _tip(hand)) * Vector3(1, 0, 1)
		if i > 0 and miss.length() < 0.002:
			break
		# As the mouse moves it (Surgeon.steer_hand()): within reach, and also while it holds onto something.
		me._move_hand(hand, miss)
		await frames(6)


## Turns the active hand's tool straight ahead with Aim tool: the hands start turned in, and straight the tip reaches
## further and the tool lies along where it points, not across what's beside it.
func player_aims_straight() -> void:
	var hand := me.hands[me.active]
	await player_aims(Vector2(hand.turn / (Surgeon.AIM_SENSITIVITY * Settings.mouse_sensitivity), 0.0), 1)
	player_lets_go_of_aim()
	await frames(10)


## Moves the active hand's tip from where it is to `point` across the floor at `speed` m/s, steering back onto the
## straight line if it drifts.
func player_sweeps_to(point: Vector3, speed: float = SLOW) -> void:
	var hand := me.hands[me.active]
	var from := _tip(hand) * Vector3(1, 0, 1)
	var to := point * Vector3(1, 0, 1)
	var steps := maxi(int(from.distance_to(to) / speed * Engine.physics_ticks_per_second), 1)
	for i in steps:
		var planned := from.lerp(to, float(i + 1) / steps)
		var drift := (planned - _tip(hand)) * Vector3(1, 0, 1)
		me._move_hand(hand, (to - from) / steps + drift * 0.3)
		await get_tree().physics_frame


func _tip(hand: SurgeonHand) -> Vector3:
	var tool := me.held_tool(hand.index)
	return tool.tip_position() if tool else hand.global_position + hand.tip_offset(0.05)


## Holds Aim tool (MMB) and moves the mouse `motion` pixels a frame for `count` frames, as the mouse handler does
## (Surgeon.aim_tool()). Aim tool stays held until player_lets_go_of_aim().
func player_aims(motion: Vector2, count: int) -> void:
	Input.action_press("aim_tool")
	for i in count:
		me.aim_tool(motion * Settings.mouse_sensitivity)
		await get_tree().physics_frame
	note("aimed %s px" % (motion * count))


func player_lets_go_of_aim() -> void:
	Input.action_release("aim_tool")


## Points the active hand's blade edge (ToolActions.blade_direction()) along `direction` (world, across the floor) by
## twisting the wrist, as C/V do.
func player_turns_blade(direction: Vector3) -> void:
	var hand := me.hands[me.active]
	var best := hand.twist
	var best_dot := -1.0
	for i in range(-60, 61):
		# The hand's own way of turning its tool: a spreader swings about the upright where a blade rolls.
		hand.twist = i * PI / 60.0
		var side := hand.grip_transform().basis.x
		var dot := absf(side.cross(Vector3.UP).normalized().dot(direction.normalized()))
		if dot > best_dot:
			best_dot = dot
			best = hand.twist
	hand.twist = best
	await frames(5)


# --- Tools ---------------------------------------------------------------------------------------------


## The free tools with this id that aren't used up, nearest the surgeon first.
func free_tools(id: String) -> Array[SurgicalTool]:
	var found: Array[SurgicalTool] = []
	for tool: SurgicalTool in surgery.tools.tools.values():
		if tool.def.id == id and tool.state == SurgicalTool.State.FREE and tool.charges != 0:
			found.append(tool)
	found.sort_custom(func(a: SurgicalTool, b: SurgicalTool) -> bool: return a.global_position.distance_to(me.global_position) < b.global_position.distance_to(me.global_position))
	return found


## player_orders: rings the nurse, puts `ids` in the cart (repeats are more of the same) and places the order.
func player_orders(ids: PackedStringArray) -> void:
	await player_fills_cart(ids)
	await player_places_order()


## Opens the nurse's shop and, for each of `ids`, picks its category and presses its [+].
## Presses past a full cart do nothing, as for a player.
func player_fills_cart(ids: PackedStringArray) -> void:
	surgery.open_nurse(me)
	await frames(1)
	for id in ids:
		player_picks_category(Db.tool(id).category)
		var plus := shop_button("add", id)
		if not plus.disabled:
			plus.pressed.emit()
	note("cart: %s" % ", ".join(ids))


## Clicks a category in the open nurse's shop: its button stays down and its items show.
func player_picks_category(category: String) -> void:
	shop_button("category", category).button_pressed = true


## Presses [-] next to `id` in the shown category of the open nurse's shop.
func player_removes_from_cart(id: String) -> void:
	shop_button("remove", id).pressed.emit()
	note("takes one %s out of the cart" % id)


## Presses Place order once the nurse is free to take it (the button waits for her).
func player_places_order() -> void:
	var place := shop_button("place", true)
	await wait_until(func() -> bool: return not place.disabled, 60.0)
	place.pressed.emit()
	await frames(2)
	note("places the order")


## The nurse's shop button whose `meta` is `value`, among those on screen: what a player could click.
func shop_button(meta: String, value: Variant) -> Button:
	for button: Button in surgery.hud.find_children("*", "Button", true, false):
		if button.has_meta(meta) and button.get_meta(meta) == value and button.is_visible_in_tree():
			return button
	return null


## player_requests_item: the tool in the active hand. Off the tray when it's there, otherwise ordered from the nurse
## and taken off the delivery tray. Null when there's none and nobody to fetch one.
func player_requests_item(id: String) -> SurgicalTool:
	if surgery.finished:
		return null
	var held := me.held_tool(me.active)
	if held and held.def.id == id and held.charges != 0:
		return held
	if held:
		await player_puts_down()
	var found := free_tools(id)
	if found.is_empty() and surgery.scenario.nurse:
		note("orders %s from the nurse" % id)
		await wait_until(func() -> bool: return surgery.nurse._order.is_empty() and surgery.nurse.cooldown_left <= 0.0, 60.0)
		await player_orders(PackedStringArray([id]))
		await wait_until(func() -> bool: return not free_tools(id).is_empty(), 90.0)
		await seconds(1.0)
		found = free_tools(id)
	if found.is_empty():
		note("no %s to be had" % id)
		return null
	var tool := found[0]
	await player_walks_to(tool.global_position)
	await player_reaches(tool.global_position)
	tap("grab")
	await frames(5)
	if me.held_tool(me.active) != tool:
		# Something else lay nearer the fingertips: take exactly this one, like reaching past the other.
		if me.held_tool(me.active):
			tap("grab")
			await frames(3)
		surgery.tools.request_grab(tool, me.active)
		await frames(3)
	note("holds %s" % id)
	return me.held_tool(me.active)


## Puts the active hand's tool back on the instrument tray (or, holding nothing, does nothing). `standing`: a bottle is
## stood upright there (Grab held), so a needle can go in through its cap.
func player_puts_down(standing: bool = false) -> void:
	var tool := me.held_tool(me.active)
	if tool == null:
		return
	use(false)
	await frames(3)
	if not tool.grip_info.is_empty():
		# A clamp still pinching: pressed again it lets go.
		use()
		await frames(3)
		use(false)
		await frames(3)
	if me.uses_level(me.active):
		await set_level(0)
	var spot := SurgeryState.free_tray_spot(surgery)
	for i in 4:
		await player_walks_to(spot)
		# The middle of the tool over the spot: a bottle held in a fist lies well away from its tip.
		await player_reaches(spot - (ToolManager.middle(tool) - tool.tip_position()))
		await player_reaches(spot - (ToolManager.middle(tool) - tool.tip_position()))
		if (ToolManager.middle(tool) - spot).slide(Vector3.UP).length() < 0.04:
			break
		# Out of this tool's reach from the side of the tray: another spot.
		SurgeryState.tray_spot_is_bad(surgery, spot)
		spot = SurgeryState.free_tray_spot(surgery)
	# Set down onto the tray before letting go, or a bottle would fall and roll.
	if not ToolActions.TRIGGER_NAMES.has(tool.def.action):
		use()
		await frames(15)
	if standing and tool.def.tray == "bottles":
		press("grab")
		await seconds(Surgeon.STAND_HOLD + 0.2)
		release("grab")
	else:
		tap("grab")
	await seconds(0.5)
	if (ToolManager.middle(tool) - spot).slide(Vector3.UP).length() > 0.08:
		# It slid off whatever it was set down against: anything else set down there would too.
		SurgeryState.tray_spot_is_bad(surgery, spot)
	note("puts %s down" % tool.def.id)


## True when the tool lies on the instrument tray: its middle over the tray, on it or on what else lies there.
func lies_on_tray(tool: SurgicalTool) -> bool:
	var at := ToolManager.middle(tool) - (surgery.room.layout.tray as Vector3)
	return tool.state == SurgicalTool.State.FREE and absf(at.x) < 0.36 and absf(at.z) < 0.41 and absf(at.y - Room.TRAY_SURFACE) < 0.2


func player_works_at(point: Vector3, level: int, time: float) -> void:
	await player_reaches(point)
	await set_level(level)
	use()
	await seconds(time)
	use(false)
	await frames(3)


## Like player_works_at() along a path of points: lowered at the first, moved through the rest at `speed`, lifted at the end.
func player_works_along(points: Array[Vector3], level: int, speed: float = SLOW) -> void:
	await player_reaches(points[0])
	await set_level(level)
	use()
	await seconds(0.3)
	for p in points.slice(1):
		await player_sweeps_to(p, speed)
	use(false)
	await frames(3)


func site_point(uv: Vector2, depth: float = 0.0) -> Vector3:
	return body.uv_to_world(uv, depth)


## Interacts with the room station whose prompt is `prompt`, standing in front of it.
func player_interacts(prompt: String) -> bool:
	for node in surgery.room.find_children("*", "Interactable", true, false):
		var station := node as Interactable
		if station.prompt == prompt and station.offered_to(me):
			await player_walks_to(station.global_position, 0.6)
			station.interact(me)
			await frames(5)
			note("interacts: %s" % prompt)
			return true
	note("no station '%s' on offer" % prompt)
	return false


# --- Steps -----------------------------------------------------------------------------------------------


## Holds the bottle in the active hand tipped over `dish` with Use tool for `time` seconds, pouring.
func player_pours_into(dish: SurgicalTool, time: float) -> void:
	await player_walks_to(dish.global_position)
	await player_works_at(ToolManager.middle(dish) + Vector3.UP * 0.05, 0, time)


## Fills a dish (the iodine dish unless `dish_id` says) from the bottle, takes a cotton pad in forceps, dips it and
## wipes the site row by row, dipping again whenever the pad runs dry, until `amount` of the site is sanitized.
func player_sanitizes_site(amount: float, dish_id: String = "iodine_dish") -> void:
	note("sanitizes the site")
	var dishes := free_tools(dish_id)
	if dishes.is_empty():
		note("no %s" % dish_id)
		return
	var dish := dishes[0]
	if dish.fill < 0.5:
		await player_requests_item("iodine_bottle")
		await player_pours_into(dish, 3.0)
		note("dish filled: %.2f" % dish.fill)
		await player_puts_down()
	var forceps := await player_requests_item("forceps")
	var radius := body.meters_to_uv(Db.tool("cotton_pad").radius)
	var rows := ceili(1.0 / (radius * 1.4))
	var row := 0
	for attempt in 12:
		if patient.sanitized_fraction() >= amount + 0.05 or row >= rows:
			break
		if surgery.tools.carried_by(forceps) == null:
			var pads := free_tools("cotton_pad")
			if pads.is_empty():
				await player_requests_item("cotton_pad")
				await player_puts_down()
				await player_requests_item("forceps")
				pads = free_tools("cotton_pad")
			await player_walks_to(pads[0].global_position)
			await player_works_at(pads[0].global_position, 0, 0.2)
		var pad := surgery.tools.carried_by(forceps)
		note("forceps carry %s" % pad)
		if pad == null:
			note("forceps didn't take a pad")
			continue
		if dish.fill <= 0.05:
			await player_puts_down()
			await player_requests_item("iodine_bottle")
			await player_pours_into(dish, 3.0)
			await player_requests_item("forceps")
		await player_walks_to(dish.global_position)
		await player_works_at(ToolManager.middle(dish), 0, 1.0)
		note("pad dipped: %.2f, dish %.2f" % [pad.fill, dish.fill])
		# Rows across the site, as long as the pad holds iodine.
		await player_walks_to(site_point(Vector2(0.5, 0.5)))
		while pad.fill > 0.05 and row < rows:
			var v := (row + 0.5) / rows
			var line: Array[Vector3] = [site_point(Vector2(0.02, v)), site_point(Vector2(0.98, v))]
			if row % 2 == 1:
				line.reverse()
			await player_works_along(line, 0, 0.06)
			row += 1
	# Pressed in the air, the forceps let the pad fall.
	if surgery.tools.carried_by(forceps):
		var spot := SurgeryState.free_tray_spot(surgery)
		await player_walks_to(spot)
		await player_reaches(spot)
		use()
		await frames(3)
		use(false)
	await player_puts_down()
	note("site sanitized: %.2f" % patient.sanitized_fraction())


## The dose a player works out from the chart: `share` of the right dose for this patient, in ml of `vial_id`.
func dose_ml(vial_id: String, share: float = 1.0) -> float:
	var vial := Db.tool(vial_id)
	var drug := Db.drug(vial.drug)
	return drug.dose * patient.weight_kg * share / vial.concentration


## Draws `ml` of a vial into a syringe big enough for it and gives it: "drip" pushes it into the IV bag (it runs down
## the line), "vein" straight into the forearm vein, "arm" into the forearm muscle beside it, "tissue" into the site's
## skin at `at` (both direct injections).
func player_gives_drug(vial_id: String, ml: float, route: String, at: Vector2 = Vector2(0.5, 0.5)) -> void:
	note("gives %.1f ml of %s (%s)" % [ml, vial_id, route])
	var size := "syringe_3" if ml <= 3.0 else "syringe_10" if ml <= 10.0 else "syringe_50"
	if ml > 50.0:
		note("%.0f ml won't fit a syringe" % ml)
		return
	var syringe := await player_requests_item(size)
	var vials := free_tools(vial_id)
	var vial: SurgicalTool = vials[0] if not vials.is_empty() else null
	if vial == null:
		vial = await player_requests_item(vial_id)
		if vial == null:
			return
		await player_puts_down(true)
		syringe = await player_requests_item(size)
	if syringe == null:
		return
	# In through the cap at the vial's tip.
	await _needle_into(vial.tip_position(), false)
	var into: String = ToolActions.needle_target(syringe, patient).kind
	for i in ceili(ml):
		await notch(false)
	note("drew %.1f ml (needle in %s)" % [syringe.ml, into])
	await _needle_out()
	match route:
		"drip":
			var bag := surgery.tools.drip_bag()
			await _needle_into(ToolManager.middle(bag), false)
		"vein":
			await _needle_into(vein_point(), true)
		"arm":
			await _needle_into(_beside_vein(0.025), true)
		_:
			await _needle_into(site_point(at), true)
	into = ToolActions.needle_target(syringe, patient).kind
	for i in ceili(syringe.ml + syringe.air) + 1:
		await notch(true)
	note("pushed into %s, %.1f ml left" % [into, syringe.ml])
	await _needle_out()
	await frames(10)
	await player_puts_down()


## Brings the active hand's needle to `point`. pressed: Use tool pushes it in there (skin, a vein); otherwise it just
## rests in a vial or bag.
func _needle_into(point: Vector3, pressed: bool) -> void:
	var hand := me.hands[me.active]
	await player_walks_to(point)
	# Pointed straight at it: turned in, a long syringe would lie across whatever is beside a vial.
	await player_aims_straight()
	for i in 40:
		# Aimed the hand's own way: a vial or the bag it's over snaps the needle in.
		hand.local_target = me.to_local(point - me.own_tip_offset(me.active) + Vector3.UP * 0.04)
		await get_tree().physics_frame
	if pressed:
		use()
		await frames(30)


func _needle_out() -> void:
	use(false)
	var hand := me.hands[me.active]
	hand.local_target += Vector3(0.0, 0.15, 0.1)
	await frames(20)


## A point on the forearm `off` meters across it from the middle of the vein.
func _beside_vein(off: float) -> Vector3:
	var vein: MeshInstance3D = body._veins[0]
	var line: PackedVector3Array = vein.get_meta("line")
	var middle := line.size() / 2
	var across := vein.global_basis * (line[middle + 1] - line[middle - 1])
	return vein_point() + across.cross(Vector3.UP).normalized() * off


## The middle of the forearm vein (world space), where an IV catheter or a needle goes in.
func vein_point() -> Vector3:
	var vein: MeshInstance3D = body._veins[0]
	var line: PackedVector3Array = vein.get_meta("line")
	return vein.to_global(line[line.size() / 2])


## Sews every open skin wound shut: the muscle first where a wound goes through it, then the skin. The needle sews a
## running thread along each (player_sews()); a stapler or tape (`tool_id`) goes segment by segment along it at medium
## tension, held on each until it's closed. Goes round again for anything that didn't close.
func player_closes_wounds(tool_id: String = "needle") -> void:
	note("closes the wounds with %s" % tool_id)
	var tool := await player_requests_item(tool_id)
	for round in 4:
		if patient.skin_closure() >= 0.98:
			break
		for wound: Wound in patient.wounds.duplicate():
			if wound.is_internal() or wound.kind == Wound.Kind.BURN:
				continue
			if tool.def.action == "sew":
				if wound.through_muscle() and Array(wound.muscle).any(func(m: float) -> bool: return m < 1.0):
					await player_sews(wound, TissueSim.Depth.MUSCLE)
				if Array(wound.bins).any(func(b: float) -> bool: return b < 1.0):
					await player_sews(wound, TissueSim.Depth.SKIN)
				continue
			for layer in ["muscle", "skin"]:
				if layer == "muscle" and not wound.through_muscle():
					continue
				for bin in wound.bins.size():
					var closed := func() -> bool: return (wound.muscle if layer == "muscle" else wound.bins)[bin] >= 1.0
					if closed.call():
						continue
					await _within_reach(site_point(wound.bin_position(bin)))
					await player_reaches(site_point(wound.bin_position(bin)))
					await set_level(2)
					use()
					await wait_until(closed, 3.0)
					if not closed.call():
						var held := me.held_tool(me.active)
						note("%s %d won't close: lowered %s, level %d, tip %s" % [layer, bin, me.hands[me.active].lowered, me.hands[me.active].level, body.probe(held.tip_position()) if held else "no tool"])
					use(false)
					await frames(3)
	note("skin closure %.2f" % patient.skin_closure())
	await player_puts_down()


## Sews `wound` with a running thread through `layer` (TissueSim.Depth), the needle in the active hand: holes along
## it (player_threads()), the wheel until the hand status reads closed (player_pulls_thread()), and the knot
## (player_ties_off()).
func player_sews(wound: Wound, layer: int) -> void:
	if await player_threads(wound, layer):
		await player_pulls_thread("closed")
		await player_ties_off()
	note("%s of wound %d closed %.2f" % [TissueSim.Depth.keys()[layer], wound.id, Array(wound.muscle if layer == TissueSim.Depth.MUSCLE else wound.bins).min()])


## Clicks the needle's thread through `wound` in `layer`: the first hole as close beside the wound as that layer
## shows (inside the opening for what's under the skin, Patient.suture_layer_at()), then one a grid cell and a half
## further along on the other side each click, the last two at the wound's end. The first two clicks capture a key
## frame (thread_hole_1, thread_hole_2). Returns false when the layer shows nowhere beside the wound.
func player_threads(wound: Wound, layer: int) -> bool:
	# Grid cells along and across the wound, in uv: they're square in meters, not in uv.
	var along := (wound.points[-1] - wound.points[0]).normalized()
	if along == Vector2.ZERO:
		along = Vector2.RIGHT
	var step := (absf(along.x) / body.tissue.res_x + absf(along.y) / body.tissue.res_y) * 1.5
	var cell := absf(along.y) / body.tissue.res_x + absf(along.x) / body.tissue.res_y
	# A fractional remainder gets one final endpoint, not two overshooting samples clamped to the same end.
	# Exact multiples still finish with a bite across that endpoint, as the placement loop describes below.
	var holes := floori(wound.length_uv() / step) + 2
	var first := -1.0
	for i in 16:
		var off := cell * (0.25 + i * 0.25)
		if patient.suture_layer_at(_beside_wound(wound, 0.0, off), wound) == layer:
			# Skin holes well clear of the opening: the needle lands a little off where the hand aims.
			first = off + (cell if layer == TissueSim.Depth.SKIN else 0.0)
			break
	if first < 0.0:
		note("no %s shows beside wound %d to sew" % [TissueSim.Depth.keys()[layer], wound.id])
		return false
	note("threads %s along wound %d: %d holes" % [TissueSim.Depth.keys()[layer], wound.id, holes])
	for i in holes:
		# The rest a grid cell off at least, so each lands on its own side of the wound. Clamp only the final hole to the
		# far end: a two-hole stitch must run from one end to the other, and longer routes keep their last crossing there.
		var along_at := wound.length_uv() if i == holes - 1 else minf(i * step, wound.length_uv())
		var at := site_point(_beside_wound(wound, along_at, (first if i == 0 else maxf(first, cell)) * (1 if i % 2 == 0 else -1)))
		# Re-square to each puncture. The holder's horizontal carry and tip-pivot working pose use different reaches;
		# approaching the point keeps both sides of the bite comfortably inside the arm's range.
		await player_walks_to(at)
		await player_reaches(at)
		if ((_tip(me.hands[me.active]) - at) * Vector3(1, 0, 1)).length() > 0.004:
			note("needle cannot reach intended puncture within 4 mm")
			return false
		use()
		await seconds(0.2)
		use(false)
		await frames(3)
		if i < 2:
			await capture("thread_hole_%d" % (i + 1))
	return true


## Turns the wheel on the needle's thread until the hand status reads `state` (ToolActions.thread_state()): down
## tightens, up loosens. Stops early when the thread goes (torn through).
func player_pulls_thread(state: String) -> void:
	var needle := me.held_tool(me.active)
	var thread := needle.suture_thread
	var order := ["loose", "closed", "too tight"]
	for i in 16:
		var now := ToolActions.thread_state(needle)
		if now == state or thread != 0 and needle.suture_thread == 0:
			break
		await notch(order.find(now) > order.find(state))
	note("thread %s" % ToolActions.thread_state(needle))


## Holds Use tool where the needle is until the thread is tied off, then captures `key_frame`.
func player_ties_off(key_frame: String = "tied_off") -> void:
	use()
	await seconds(ToolActions.SUTURE_TIE_HOLD + 0.2)
	use(false)
	await frames(3)
	await capture(key_frame)


## The point `along` (uv) from the start of `wound` on its line, clamped to its ends, moved `off` (uv) to its left
## (negative: right).
static func _beside_wound(wound: Wound, along: float, off: float) -> Vector2:
	var points := wound.points
	if points.size() < 2:
		return points[0] + Vector2.RIGHT.orthogonal() * off
	var travelled := 0.0
	for i in range(1, points.size()):
		var seg := points[i - 1].distance_to(points[i])
		if travelled + seg >= along or i == points.size() - 1:
			var direction := (points[i] - points[i - 1]).normalized()
			return points[i - 1].lerp(points[i], clampf((along - travelled) / maxf(seg, 0.0001), 0.0, 1.0)) + direction.orthogonal() * off
		travelled += seg
	return points[0]


## Walks over only when `point` is out of the active hand's comfortable reach.
func _within_reach(point: Vector3) -> void:
	var hand := me.hands[me.active]
	var tool := me.held_tool(me.active)
	var grip := point - hand.tip_offset(tool.def.length) if tool else point
	if grip.distance_to(me.shoulder(me.active)) > Surgeon.REACH - 0.12:
		await player_walks_to(point)


## Puts an IV catheter into the forearm vein (the arm away from the site when the site is a forearm).
func player_sets_iv() -> void:
	note("sets an IV line")
	# An awake patient in pain jerks their arm: settled first, or the catheter misses the vein, and a missed line
	# can't be put right.
	if patient.vitals.is_awake() and maxf(patient.vitals.pain, patient.vitals.panic) > 0.4:
		await player_calms_patient()
	var catheter := await player_requests_item("iv_catheter")
	if catheter == null:
		return
	var hand := me.hands[me.active]
	await player_walks_to(vein_point())
	# Following the vein (an awake patient's arm shifts as they flinch and tense up), and pushing in once the tip is
	# over it, still following it while the catheter goes in.
	for i in 240:
		hand.local_target = me.to_local(vein_point() - hand.tip_offset(catheter.def.length) + Vector3.UP * 0.04)
		await get_tree().physics_frame
		if patient.iv_set:
			break
		if i >= 30 and not hand.trigger and _over_vein(catheter.tip_position()):
			use()
	use(false)
	await frames(10)
	note("IV in: %s, in the vein: %s" % [patient.iv_set, patient.iv_in_vein])
	if me.held_tool(me.active):
		await player_puts_down()


## The vein is right under `tip`, on the skin below it: what a player sees from above before pushing a needle in.
func _over_vein(tip: Vector3) -> bool:
	var skin: Dictionary = me._surface_below(tip)
	return skin.y != -INF and body.vein_at(Vector3(tip.x, skin.y, tip.z))


## A painkilling sedative from the scenario's kit (ketamine, else morphine) into the forearm muscle, then a moment for
## it to work.
func player_calms_patient() -> void:
	for vial in ["vial_ketamine", "vial_morphine"]:
		if surgery.scenario.starting_tools.has(vial) or not free_tools(vial).is_empty():
			note("calms the patient with %s" % vial)
			await player_gives_drug(vial, dose_ml(vial, 0.5), "arm")
			await wait_until(func() -> bool: return maxf(patient.vitals.pain, patient.vitals.panic) < 0.3, 30.0)
			return


## General anesthesia from the scenario's own vial (propofol or ketamine): down the IV line if there's one, straight
## into the vein otherwise.
func player_anesthetizes() -> void:
	var vial := "vial_propofol"
	for id in ["vial_propofol", "vial_ketamine"]:
		if not free_tools(id).is_empty() or surgery.scenario.starting_tools.has(id):
			vial = id
			break
	await player_gives_drug(vial, dose_ml(vial), "drip" if patient.iv_working() else "vein")
	await wait_until(func() -> bool: return patient.vitals.anesthesia >= 0.7, 30.0)
	note("anesthesia %.2f" % patient.vitals.anesthesia)


## Lidocaine into the skin by the site's wounds (or its middle).
func player_numbs_site() -> void:
	var at := _work_uv()
	await player_gives_drug("vial_lidocaine", dose_ml("vial_lidocaine"), "tissue", at)
	await wait_until(func() -> bool: return patient.vitals.local_block >= 0.5, 20.0)
	note("local block %.2f" % patient.vitals.local_block)


## Where the work is: the first target still in, else the first wound, else the site's middle (site uv).
func _work_uv() -> Vector2:
	for target in patient.targets:
		if not target.extracted:
			return target.uv
	for wound in patient.wounds:
		if not wound.is_internal():
			return wound.midpoint()
	return Vector2(0.5, 0.5)


## Strokes `length` meters long through `center` along the site's long side, cut to fit on the site, then parallel
## ones beside it: [[from uv, to uv], ...]. Steps that need a total length take strokes until they have it.
func _incision_strokes(center: Vector2, length: float) -> Array[Array]:
	var strokes: Array[Array] = []
	var along := Vector2(1, 0) if body.site_size.x >= body.site_size.y else Vector2(0, 1)
	var across := Vector2(along.y, along.x)
	var span := minf(length / body.uv_to_meters(1.0), 0.84)
	for row in 7:
		var shift := across * 0.1 * ((row + 1) / 2) * (1 if row % 2 == 1 else -1)
		var middle := (center + shift).clamp(Vector2(0.08, 0.08) + along * span * 0.5, Vector2(0.92, 0.92) - along * span * 0.5)
		if middle.x < 0.05 or middle.y < 0.05 or middle.x > 0.95 or middle.y > 0.95:
			continue
		strokes.append([middle - along * span * 0.5, middle + along * span * 0.5])
	return strokes


## Marks the incision line with the marker.
func player_marks_line(length: float) -> void:
	note("marks %.0f cm" % (length * 100.0))
	await player_requests_item("marker")
	for stroke in _incision_strokes(_work_uv(), length + 0.01):
		if body.uv_to_meters(patient.marked_uv) >= length + 0.005:
			break
		await player_walks_to(site_point((stroke[0] + stroke[1]) * 0.5))
		var line: Array[Vector3] = [site_point(stroke[0]), site_point(stroke[1])]
		await player_works_along(line, 0, 0.05)
	note("marked %.1f cm" % (body.uv_to_meters(patient.marked_uv) * 100.0))
	await player_puts_down()


## Cuts `length` meters at full depth with the scalpel, the blade's edge turned along the line, slowly enough for a
## clean cut, through where the work is.
func player_incises(length: float) -> void:
	note("incises %.0f cm" % (length * 100.0))
	for stroke in _incision_strokes(_work_uv(), length + 0.01):
		if patient.surgeon_cut_length_m(0.7) >= length + 0.005:
			break
		await player_cuts_skin(stroke[0], stroke[1], 3)
	note("cut %.1f cm deep enough" % (patient.surgeon_cut_length_m(0.7) * 100.0))
	await player_puts_down()
	await capture("incised")


## One stroke of a blade (the scalpel unless `tool_id` says) from `from` to `to` (site uv) at depth `level` (1 through
## the skin, 3 through the muscle): the surgeon stands by the line, turns the edge along it, presses in and draws it
## slowly enough for a clean cut. The blade stays in hand.
func player_cuts_skin(from: Vector2, to: Vector2, level: int, tool_id: String = "scalpel") -> void:
	await player_requests_item(tool_id)
	var start := site_point(from)
	var finish := site_point(to)
	await player_walks_to((start + finish) * 0.5)
	await player_turns_blade(finish - start)
	await player_works_along([start, finish] as Array[Vector3], level)


## Sets the Gelpi retractor into the cut from `from` to `to` (site uv) at its middle: the surgeon stands by it, rolls
## it so its tips are square to the cut (as C/V turn a blade's edge along it) and presses Use tool there.
## It stays in hand, set or not.
func player_sets_gelpi(from: Vector2, to: Vector2) -> SurgicalTool:
	var gelpi := await player_requests_item("gelpi")
	var middle := site_point((from + to) * 0.5)
	await player_walks_to(middle)
	await player_turns_blade(site_point(to) - site_point(from))
	await player_reaches(middle)
	use()
	await frames(10)
	use(false)
	# Set, it goes down into the cut (SurgicalTool.DIG_TIME).
	await seconds(SurgicalTool.DIG_TIME + 0.1)
	note("gelpi %s" % ("set" if gelpi.in_wound else "not set"))
	return gelpi


## Turns the wheel on the active hand's Gelpi retractor until its tips are `spread` meters apart, then lets the skin
## settle.
func player_opens_gelpi(spread: float) -> void:
	var gelpi := me.held_tool(me.active)
	for i in 30:
		if absf(gelpi.spread - spread) < ToolActions.SPREAD_STEP * 0.5:
			break
		await notch(spread > gelpi.spread)
	await seconds(1.0)
	note("gelpi open %.1f cm" % (gelpi.spread * 100.0))


## Takes out every target of `kind` the way its scenario says: lifted out with forceps (slowly while it's still
## attached), drained with suction, sawn or knocked loose.
func player_extracts(kind: String) -> void:
	for target in patient.targets:
		if target.kind != kind or target.extracted:
			continue
		note("extracts %s (%s)" % [kind, target.remove_with])
		match target.remove_with:
			"suction":
				await player_requests_item("suction")
				await player_walks_to(site_point(target.uv))
				await player_works_at(site_point(target.uv), 3, 0.5)
				await set_level(3)
				use()
				await wait_until(func() -> bool: return target.extracted, 60.0)
				use(false)
			"saw":
				await player_requests_item("bone_saw")
				await player_walks_to(site_point(target.uv))
				await player_reaches(site_point(target.uv))
				await set_level(3)
				use()
				await wait_until(func() -> bool: return target.extracted, 60.0)
				use(false)
			"smash":
				await player_requests_item("mallet")
				await player_walks_to(site_point(target.uv))
				for hit in 20:
					if target.extracted:
						break
					await player_works_at(site_point(target.uv), 0, 0.3)
					await frames(10)
			_:
				await _lift_out(target)
		note("%s out: %s" % [kind, target.extracted])
		await player_puts_down()
	await capture("extracted_" + kind)


## Forceps onto the target and out over the floor beside the table. Still attached, it comes loose with a slow pull.
## Under an organ, its attachment is cut through first, the organ is held aside with forceps in the other hand while
## the target comes out, and let go again quickly (held aside too long it bruises).
func _lift_out(target: CavityTarget) -> void:
	var aside: Array[SurgicalTool] = []
	if patient._covered(target):
		if target.anchor > 0.0:
			await _cut_free(target)
		# Forceps in both hands first: fetching the second while the first holds the organ aside would drag it along.
		await switch_to(RIGHT)
		await player_requests_item("forceps")
		aside = await _hold_aside(target)
	await switch_to(RIGHT)
	var forceps := await player_requests_item("forceps")
	var spot := body.uv_to_world(target.uv, target.depth)
	await player_walks_to(spot)
	await player_reaches(spot)
	use()
	await frames(5)
	if forceps.grip_info.get("type", "") != "target":
		note("forceps didn't take hold of %s: %s" % [target.kind, forceps.grip_info])
		use(false)
		await _let_go(aside)
		return
	# A steady pull, a couple of centimeters up, held until it lets go.
	Input.action_press("lift")
	await wait_until(func() -> bool: return body.site.to_local(forceps.tip_position()).y > -target.depth + 0.025, 3.0)
	Input.action_release("lift")
	await wait_until(func() -> bool: return target.anchor <= 0.0, 30.0)
	Input.action_press("lift")
	await wait_until(func() -> bool: return target.extracted, 5.0)
	await seconds(0.5)
	Input.action_release("lift")
	await _let_go(aside)
	await switch_to(RIGHT)
	use(false)
	await frames(10)


## The scalpel worked round a target from inside the opening until whatever holds it is cut through.
func _cut_free(target: CavityTarget) -> void:
	note("cuts %s free" % target.kind)
	await player_requests_item("scalpel")
	var spot := body.uv_to_world(target.uv, target.depth)
	await player_walks_to(spot)
	await player_reaches(spot)
	await set_level(3)
	use()
	await wait_until(func() -> bool: return target.anchor <= 0.0, 20.0)
	use(false)
	note("%s attached %.2f" % [target.kind, target.anchor])
	await player_puts_down()


## Forceps in the left hand take hold of each organ over the target and draw it aside, away from the target.
## Returns the forceps holding them.
func _hold_aside(target: CavityTarget) -> Array[SurgicalTool]:
	var holding: Array[SurgicalTool] = []
	await switch_to(LEFT)
	for i in body.organs.size():
		var organ := body.organs[i]
		var organ_uv := Vector2(organ.position.x / body.site_size.x + 0.5, organ.position.z / body.site_size.y + 0.5)
		if organ_uv.distance_to(target.uv) >= 0.07:
			continue
		var away := (organ_uv - target.uv).normalized() if organ_uv.distance_to(target.uv) > 0.01 else Vector2(1, 0)
		var grip_at := _organ_grip_point(i, away)
		if grip_at == Vector3.INF:
			note("no place to take hold of the %s clear of bleeders" % organ.get_meta("kind", organ.name))
			continue
		var forceps := await player_requests_item("forceps")
		await player_walks_to(grip_at)
		await player_reaches(grip_at)
		use()
		await frames(5)
		if forceps.grip_info.get("type", "") != "organ":
			note("forceps didn't take hold of the %s: %s" % [organ.get_meta("kind", organ.name), forceps.grip_info])
			use(false)
			continue
		await player_sweeps_to(site_point(organ_uv + away * 0.12), 0.1)
		note("holds the %s aside, target covered: %s" % [organ.get_meta("kind", organ.name), patient._covered(target)])
		holding.append(forceps)
		if holding.size() == 1:
			break
	return holding


## A point on top of organ `index` to take hold of it, toward `away` (site uv) and clear of internal wounds (forceps
## there would clamp the bleeder instead). INF if there's none.
func _organ_grip_point(index: int, away: Vector2) -> Vector3:
	var organ := body.organs[index]
	for step in 6:
		var local := organ.position + Vector3(away.x, 0.0, away.y) * step * 0.008
		var uv := Vector2(local.x / body.site_size.x + 0.5, local.z / body.site_size.y + 0.5)
		if patient.wounds.any(func(w: Wound) -> bool: return w.is_internal() and w.points[0].distance_to(uv) < 0.055):
			continue
		var top := body.site.to_global(Vector3(local.x, organ.position.y, local.z))
		if body.organ_at(top, 0.02) == index:
			return top
	return Vector3.INF


## The left hand lets go of what its forceps hold and puts them down.
func _let_go(holding: Array[SurgicalTool]) -> void:
	if holding.is_empty():
		return
	await switch_to(LEFT)
	use(false)
	await frames(3)
	use()
	await frames(3)
	use(false)
	await player_puts_down()


## Seals what still bleeds until the patient loses less than `max_rate` ml/s, the way a surgeon escalates: the
## cautery on internal bleeders and along the skin beside open wounds (over the opening it would reach into the cavity),
## a hemostat locked onto each bleeder that's left, tranexamic acid, then gauze pressed along the wounds.
func player_stops_bleeding(max_rate: float) -> void:
	note("stops the bleeding: %s" % bleeders())
	var settled := func() -> bool: return patient.vitals.bleed_rate <= max_rate
	if settled.call():
		return
	if surgery.scenario.nurse or not free_tools("cautery").is_empty():
		await player_requests_item("cautery")
		for wound in _bleeding():
			if wound.is_internal():
				var spot := body.uv_to_world(wound.points[0], wound.depth_m)
				await player_walks_to(spot)
				await player_works_at(spot, 3, 3.0)
			else:
				await player_walks_to(site_point(wound.midpoint()))
				await player_works_along(_beside(wound), 2, 0.01)
		await player_puts_down()
	if not await wait_until(settled, 3.0):
		for wound in _bleeding():
			await player_clamps_bleeder(wound)
	if not await wait_until(settled, 3.0) and (surgery.scenario.nurse or not free_tools("vial_txa").is_empty()):
		await player_gives_drug("vial_txa", dose_ml("vial_txa"), "drip" if patient.iv_working() else "vein")
		await seconds(15.0)
	if not await wait_until(settled, 3.0) and (surgery.scenario.nurse or not free_tools("gauze").is_empty()):
		await player_requests_item("gauze")
		for wound in _bleeding():
			if not wound.is_internal():
				var along := _beside(wound)
				if along.is_empty():
					continue
				await player_walks_to(site_point(wound.midpoint()))
				await player_works_along(along, 3, 0.01)
		await player_puts_down()
	await wait_until(settled, 3.0)
	note("bleeding: %s" % bleeders())


## Wounds still bleeding noticeably, the worst first.
func _bleeding() -> Array[Wound]:
	var out: Array[Wound] = []
	for wound in patient.wounds:
		if wound.bleed_rate(body.uv_to_meters(1.0), 1.0) >= 0.05:
			out.append(wound)
	out.sort_custom(func(a: Wound, b: Wound) -> bool: return a.bleed_rate(1.0, 1.0) > b.bleed_rate(1.0, 1.0))
	return out


## Every wound still bleeding: kind, ml/s, and what holds it back.
func bleeders() -> String:
	var out: Array[String] = []
	for wound in patient.wounds:
		var rate := wound.bleed_rate(body.uv_to_meters(1.0), 1.0)
		if rate >= 0.05:
			out.append("%s %.2f ml/s (%.1f cm, open %.2f, sealed %.2f, held %.2f, clamped %.2f, closed %.2f)" % [Wound.Kind.keys()[wound.kind], rate, body.uv_to_meters(wound.length_uv()) * 100.0, wound.opened, wound.cauterized, wound.held, wound.clamped, wound.closure()])
	return ", ".join(out)


## Points along a skin wound on whole skin just beside it, where a tool touches the wound's edge.
func _beside(wound: Wound) -> Array[Vector3]:
	var line: Array[Vector3] = []
	for i in wound.bins.size():
		var at := wound.bin_position(i)
		var ahead := wound.bin_position(mini(i + 1, wound.bins.size() - 1)) - wound.bin_position(maxi(i - 1, 0))
		var across := Vector2(-ahead.y, ahead.x).normalized() if ahead.length() > 0.0 else Vector2(0, 1)
		var found := false
		for step in range(2, 14):
			for side: float in [1.0, -1.0]:
				var beside := at + across * side * step * 0.005
				if not found and not body.is_open(beside) and beside.clamp(Vector2.ZERO, Vector2.ONE) == beside:
					line.append(site_point(beside))
					found = true
	return line


## Sews internal wounds shut from inside the opening.
func player_closes_internal_wounds() -> void:
	note("closes internal wounds")
	await player_requests_item("needle")
	for wound: Wound in patient.wounds.duplicate():
		if not wound.is_internal():
			continue
		var spot := body.uv_to_world(wound.points[0], wound.depth_m)
		await player_walks_to(spot)
		# Deep in the belly, across it: the needle pointed straight ahead reaches further.
		await player_aims_straight()
		await player_reaches(spot)
		use()
		await wait_until(func() -> bool: return wound.closure() >= 0.9, 20.0)
		use(false)
		note("internal wound closed %.2f" % wound.closure())
	await player_puts_down()


## An injection for an "inject" objective: a drug from its vial into the IV line or the vein, or an antibiotic.
func player_injects(step: Dictionary) -> void:
	var vial := "vial_cefazolin" if step.get("flag", "") == "antibiotic" else "vial_" + str(step.get("drug", ""))
	await player_gives_drug(vial, dose_ml(vial), "drip" if patient.iv_working() else "vein")
	await seconds(2.0)


## A tourniquet pressed onto the limb above the site.
func player_applies_tourniquet() -> void:
	note("applies a tourniquet")
	await player_requests_item("tourniquet")
	var spot := site_point(Vector2(0.5, -0.4))
	for v: float in [-0.4, -0.2, 0.0, 1.2]:
		if patient.tourniquet_on:
			break
		spot = site_point(Vector2(0.5, v))
		await player_walks_to(spot)
		await player_works_at(spot, 0, 0.5)
	note("tourniquet on: %s" % patient.tourniquet_on)
	if me.held_tool(me.active):
		await player_puts_down()


## A hemostat locked onto a bleeding wound (the worst one when none is given) and left on it: on an internal bleeder
## from inside the opening, on a skin wound at its opened edge. Gripped away from anything to take out, which a clamp
## would take hold of instead.
func player_clamps_bleeder(wound: Wound = null) -> void:
	if wound == null:
		var bleeding := _bleeding()
		if bleeding.is_empty():
			return
		wound = bleeding[0]
	note("clamps a bleeding %s" % Wound.Kind.keys()[wound.kind].to_lower())
	var spots: Array[Vector3] = []
	if wound.is_internal():
		for turn in 8:
			spots.append(body.uv_to_world(wound.points[0] + Vector2(0.035, 0.0).rotated(turn * PI / 4.0), wound.depth_m))
	else:
		for i in wound.bins.size():
			spots.append(site_point(wound.bin_position(i)))
	var clear := func(spot: Vector3) -> bool:
		var uv := body.world_to_uv(spot)
		return patient.targets.all(func(t: CavityTarget) -> bool: return t.extracted or t.uv.distance_to(uv) >= 0.06)
	var hemostat := await player_requests_item("hemostat")
	if hemostat == null:
		return
	for spot in spots.filter(clear):
		await player_walks_to(spot)
		await player_reaches(spot)
		use()
		await frames(5)
		use(false)
		if wound.clamped >= 0.8:
			break
		if not hemostat.grip_info.is_empty():
			use()
			await frames(3)
			use(false)
			await frames(3)
	if wound.clamped >= 0.8:
		# Self-retaining: let go of the handle and it stays locked on.
		tap("grab")
		await frames(10)
	else:
		await player_puts_down()
	note("clamped: %.2f" % wound.clamped)


## Hangs blood bags on the IV stand until the patient has `min_ml` back.
func player_transfuses(min_ml: float) -> void:
	note("transfuses")
	for bag in 4:
		var needed := min_ml * patient.vitals.max_blood_ml / Vitals.NORMAL_BLOOD_ML
		if patient.transfused_ml > 0.0 and patient.vitals.blood_ml >= needed:
			break
		await player_requests_item("blood_o_neg")
		await player_interacts("Swap IV bag")
		await wait_until(func() -> bool: return patient.vitals.blood_ml >= needed, 70.0)
	note("blood %.0f ml of %.0f, %.0f ml transfused" % [patient.vitals.blood_ml, patient.vitals.max_blood_ml, patient.transfused_ml])


## Turns the patient with the table's handle and the quick time keys, until they lie as `orientation` says.
func player_turns_patient(orientation: int) -> void:
	note("turns the patient")
	for attempt in 4:
		if body.orientation == orientation:
			break
		await player_interacts("Turn the patient")
		for key in 8:
			await frames(20)
			var qte: QteView = null
			for node in surgery.hud.find_children("*", "QteView", true, false):
				qte = node
			if qte == null:
				break
			var action: String = qte._sequence[qte._index]
			var event: InputEventKey = InputMap.action_get_events(action).filter(func(e: InputEvent) -> bool: return e is InputEventKey)[0].duplicate()
			event.pressed = true
			qte._unhandled_input(event)
		await seconds(1.0)
	note("orientation %d" % body.orientation)


## Holds every bone fragment back in its place with forceps, one in each hand, for `time` seconds. Both forceps are
## in hand before either fragment is taken hold of: fetching one later would drag the other along.
func player_aligns_fragments(time: float) -> void:
	note("aligns the fragments")
	var fragments := patient.targets.filter(func(t: CavityTarget) -> bool: return t.is_fragment())
	var hands: Array[int] = [RIGHT, LEFT]
	for i in mini(fragments.size(), 2):
		await switch_to(hands[i])
		await player_requests_item("forceps")
	for i in mini(fragments.size(), 2):
		var target: CavityTarget = fragments[i]
		await switch_to(hands[i])
		var forceps := me.held_tool(me.active)
		if forceps == null:
			continue
		var spot := body.uv_to_world(target.uv, target.depth)
		await player_walks_to(spot)
		await player_reaches(spot)
		use()
		await frames(5)
		if forceps.grip_info.get("type", "") != "target":
			note("forceps didn't take hold of a fragment: %s" % forceps.grip_info)
			continue
		await player_sweeps_to(body.uv_to_world(target.rest_uv, target.depth), 0.01)
		note("fragment %d off by %.3f" % [i, target.uv.distance_to(target.rest_uv)])
	await seconds(time + 1.0)
	for hand in [LEFT, RIGHT]:
		await switch_to(hand)
		await player_puts_down()


## Cuts the dead skin off the burns with the scalpel, row by row over each burn.
func player_debrides(amount: float) -> void:
	note("debrides")
	await player_requests_item("scalpel")
	await _over_burns(func() -> bool: return patient.grid_fraction("debrided") >= amount, 1)
	note("debrided %.2f" % patient.grid_fraction("debrided"))
	await player_puts_down()


## Skin grafts pressed onto every cleaned burn cell.
func player_grafts(amount: float) -> void:
	note("grafts")
	for sheet in 10:
		if patient.grid_fraction("grafted") >= amount:
			break
		var pad := await player_requests_item("skin_graft")
		if pad == null:
			break
		for cell in Patient.GRID * Patient.GRID:
			if pad.charges == 0 or pad.state != SurgicalTool.State.HELD:
				break
			if patient._burn_cells[cell] == 1 and patient._grafted[cell] == 0:
				var uv := (Vector2(cell % Patient.GRID, cell / Patient.GRID) + Vector2(0.5, 0.5)) / Patient.GRID
				await player_walks_to(site_point(uv))
				await player_works_at(site_point(uv), 0, 0.2)
	note("grafted %.2f" % patient.grid_fraction("grafted"))


## Works the held tool over every burn cell, row by row, until `done`.
func _over_burns(done: Callable, level: int) -> void:
	for y in Patient.GRID:
		var row: Array[Vector3] = []
		for x in Patient.GRID:
			if patient._burn_cells[y * Patient.GRID + x] == 1:
				row.append(site_point((Vector2(x, y) + Vector2(0.5, 0.5)) / Patient.GRID))
		if row.size() < 2:
			continue
		await player_walks_to(row[row.size() / 2])
		await player_turns_blade(row[-1] - row[0])
		await player_works_along(row, level, 0.03)
		if done.call():
			return


## The defibrillator's paddles on the chest, charged and let go, until the heart is back.
func player_defibrillates() -> void:
	note("defibrillates")
	await wait_until(func() -> bool: return patient.vitals.is_arrested(), 120.0)
	var paddles := await player_requests_item("defibrillator")
	var chest := site_point(Vector2(0.5, 0.5))
	for shock in 6:
		if patient.flags.has("revived") and not patient.vitals.is_arrested():
			break
		await player_walks_to(chest)
		await player_reaches(chest)
		use()
		await seconds(ToolActions.DEFIB_CHARGE_TIME + 0.3)
		use(false)
		await seconds(3.0)
		if patient.vitals.is_arrested() and shock == 1:
			await player_puts_down()
			await player_gives_drug("vial_adrenaline", dose_ml("vial_adrenaline"), "drip" if patient.iv_working() else "vein")
			paddles = await player_requests_item("defibrillator")
	note("revived: %s, rhythm %d" % [patient.flags.has("revived"), patient.vitals.rhythm])
	await player_puts_down()


## Gets the patient stable (heart going, oxygen 94+, pressure 90+): blood, or fluid without it, down the IV line while
## they're short of blood (setting a line first), the defibrillator if the heart stops. Returns once they're stable.
func player_stabilizes() -> void:
	var v := patient.vitals
	var stable := func() -> bool: return not v.is_arrested() and v.spo2 >= 94.0 and v.systolic >= 90.0
	for round in 6:
		if stable.call() or surgery.finished:
			break
		note("stabilizes: spo2 %.0f, systolic %.0f, blood %.0f%%" % [v.spo2, v.systolic, v.blood_ratio() * 100.0])
		if v.is_arrested():
			await player_defibrillates()
		elif v.blood_ratio() < 0.95:
			if not patient.iv_working():
				await player_sets_iv()
			var bag := "blood_o_neg" if surgery.scenario.starting_tools.has("blood_o_neg") or surgery.scenario.nurse else "saline_bag"
			await player_requests_item(bag)
			await player_interacts("Swap IV bag")
		await wait_until(stable, 30.0)


## Completes one objective step of the scenario like a player would.
func player_completes(step: Dictionary) -> void:
	if surgery.finished:
		return
	match step.type:
		"sanitize":
			await player_sanitizes_site(step.get("amount", 0.5))
		"iv":
			await player_sets_iv()
		"anesthesia":
			await player_anesthetizes()
		"local_block":
			await player_numbs_site()
		"mark":
			await player_marks_line(step.get("length", 0.1))
		"incise":
			await player_incises(step.get("length", 0.1))
		"extract":
			await player_extracts(step.target)
		"close":
			await player_closes_wounds()
		"close_internal":
			await player_closes_internal_wounds()
		"stop_bleeding":
			await player_stops_bleeding(step.get("max_ml_s", 0.3))
		"inject":
			await player_injects(step)
		"defib":
			await player_defibrillates()
		"tourniquet":
			await player_applies_tourniquet()
		"clamp":
			await player_clamps_bleeder()
		"transfuse":
			await player_transfuses(step.get("min_ml", 4000.0))
		"flip":
			await player_turns_patient(step.get("orientation", PatientBody.Orientation.FACE_DOWN))
		"align":
			await player_aligns_fragments(step.get("seconds", 6.0))
		"debride":
			await player_debrides(step.get("amount", 0.5))
		"graft":
			await player_grafts(step.get("amount", 0.7))
		"comfort":
			await player_interacts("Talk to the patient")
		"stabilize":
			await player_stabilizes()
		_:
			# Waiting it out: calm, listen and wait hold for a while on their own.
			pass
