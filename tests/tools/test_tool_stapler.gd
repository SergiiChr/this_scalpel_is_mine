extends GutTest
## The staplers as a player uses them: the aim shows two rings lying on the skin where the legs go in, and a click with
## them on both edges of an open cut puts one staple in there, joining the edges. Through a belly cut into the muscle the
## first staples go into the muscle, then the skin closes over it. The office stapler works the same but now and then
## tears out of the skin or catches a vessel. Headless assertions in smoke; with key frames also the site untouched,
## after the first staple and stapled shut, and what the surgeon sees (the two rings). Review them for steel staples
## bridging the cut square to it with their legs in the skin on both sides, the edges meeting between them and the
## rings on the cut's edges.

const TAGS = ["smoke", "tool_skin_stapler", "tool_office_stapler", "tissue_modification", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")
const KEY_FRAMES := "res://build/test-artifacts/screenshots/stapler"

var driver: Driver
var shots: KeyFrames
## The oblique key frames look along the cut, so the staples stand across it in view.
var along_cut := Vector3.ZERO


func test_skin_stapler_closes_a_forearm_cut() -> void:
	await _start("hand_stitch", "forearm")
	var patient := driver.patient
	var tissue := driver.body.tissue
	var wound: Wound = patient.wounds[0]
	along_cut = driver.site_point(wound.points[-1]) - driver.site_point(wound.points[0])
	var stapler := await driver.player_requests_item("skin_stapler")
	assert_false(driver.me.uses_level(driver.me.active), "the stapler takes no level from the wheel")
	var middle := driver.site_point(wound.midpoint())
	await driver.player_walks_to(middle)
	await driver.player_turns_blade(along_cut)
	await _aim_over_the_arms_edge(stapler, middle)
	await driver.player_reaches(middle + Vector3(0.0, 0.0, 0.03))
	await driver.frames(2)
	var rings: Array[Line2D] = driver.surgery.hud._legs
	assert_true(rings.all(func(ring: Line2D) -> bool: return ring.visible), "the aim is two rings, one per leg")
	assert_false(driver.surgery.hud._dot.visible, "instead of the dot")
	var charges := stapler.charges
	var toasts: Array[String] = []
	driver.surgery.hud._toasts.child_entered_tree.connect(func(toast: Node) -> void: toasts.append((toast as Label).text))
	driver.use()
	await driver.frames(4)
	driver.use(false)
	await driver.frames(3)
	assert_eq(stapler.charges, charges, "a click beside the cut puts no staple in")
	assert_true(toasts.has(ToolActions.staple_miss(tissue, driver.body.world_to_uv(stapler.staple_aim[0]), driver.body.world_to_uv(stapler.staple_aim[1]))) and not toasts[-1].is_empty(), "and says why (%s)" % [toasts])

	await driver.player_reaches(middle)
	await driver.frames(2)
	_assert_rings_on_skin(stapler)
	# A pair of forceps left lying under a leg: the ring stays on the skin, not up on them.
	var forceps := SurgeryState.tool_lies_at(driver.surgery, "forceps", ToolActions.staple_legs(stapler, driver.body)[0] + Vector3.UP * 0.01)
	await driver.seconds(1.0)
	_assert_rings_on_skin(stapler)
	driver.surgery.tools.consume(forceps)
	await driver.frames(2)
	if shots:
		await _capture_view("ready")
	var gap := tissue.gap_at(wound.midpoint(), 0.02)
	var aimed := _ring_middles()
	driver.use()
	await driver.frames(4)
	driver.use(false)
	assert_eq(stapler.charges, charges - 1, "a click puts one staple in")
	_assert_legs_where_aimed(stapler, aimed)
	await driver.seconds(0.5)
	assert_lt(tissue.gap_at(wound.midpoint(), 0.02), gap * 0.5, "and its edges meet under it (%.1f mm, were %.1f mm)" % [tissue.gap_at(wound.midpoint(), 0.02) * 1000.0, gap * 1000.0])
	assert_between(wound.closure(), 0.01, 0.5, "one staple closes a bit of the cut")
	var wire := driver.body.site.get_node_or_null("Staples/Wire") as MeshInstance3D
	assert_true(wire != null and (wire.get_meta("paths") as Array).size() == 1, "the staple is drawn")

	var placed := 1 + await driver.player_staples(wound)
	await driver.seconds(1.0)
	assert_gt(wound.closure(), 0.99, "stapled along its length the cut is closed\n%s" % driver.recent())
	assert_lt(tissue.gap_along(wound.points, 0.03, TissueSim.Depth.SKIN), TissueSim.OPEN_GAP, "the staples hold the edges together")
	assert_eq(stapler.charges, charges - placed, "one charge per staple")
	var paths: Array = wire.get_meta("paths")
	assert_eq(paths.size(), placed, "every staple is drawn")
	_assert_staples_across(paths, wound)
	assert_lt(patient.vitals.bleed_rate, 0.2, "the stapled cut stops bleeding")
	await driver.player_reaches(middle)
	charges = stapler.charges
	await _click()
	assert_eq(stapler.charges, charges, "over the stapled cut a click puts no staple in")
	await driver.player_puts_down()
	# Back at the patient, to see the result from where the surgeon works.
	await driver.player_walks_to(driver.site_point(wound.midpoint()))
	await driver.capture("stapled")
	await _finish()


func test_skin_stapler_closes_a_belly_cut_muscle_first() -> void:
	await _start("appendectomy", "belly_muscle")
	var patient := driver.patient
	var tissue := driver.body.tissue
	var from := Vector2(0.35, 0.45)
	var to := from + Vector2(driver.body.meters_to_uv(0.05), 0.0)
	along_cut = driver.site_point(to) - driver.site_point(from)
	await driver.player_cuts_skin(from, to, 3)
	await driver.player_puts_down()
	await driver.seconds(1.0)
	var wound: Wound = patient.wounds[-1]
	var middle := wound.midpoint()
	assert_true(wound.through_muscle() and tissue.muscle_open_near(middle, Patient.MUSCLE_REACH), "the cut goes through the muscle")
	await driver.player_requests_item("skin_stapler")
	await driver.player_walks_to(driver.site_point(middle))
	await driver.player_turns_blade(along_cut)
	await driver.player_reaches(driver.site_point(middle))
	await _click()
	assert_false(tissue.muscle_open_near(middle, Patient.MUSCLE_REACH), "the first staple goes into the open muscle")
	assert_lt(wound.closure(), 0.01, "and leaves the skin over it open")
	var wire := driver.body.site.get_node("Staples/Wire") as MeshInstance3D
	var muscle_staple: PackedVector3Array = (wire.get_meta("paths") as Array)[0]
	var under := Vector2(muscle_staple[1].x / driver.body.site_size.x + 0.5, muscle_staple[1].z / driver.body.site_size.y + 0.5)
	assert_lt(muscle_staple[1].y, driver.body.skin_height(under) - PatientBody.SKIN_THICKNESS, "it's drawn down in the muscle, under the skin")
	await driver.capture("muscle_staple")
	await driver.player_staples(wound)
	await driver.seconds(1.0)
	assert_false(Array(tissue.severed()).any(func(s: int) -> bool: return tissue.depth_of(s) == TissueSim.Depth.MUSCLE), "no muscle is left open")
	assert_gt(wound.closure(), 0.99, "the skin is stapled shut over it\n%s" % driver.recent())
	assert_lt(tissue.gap_along(wound.points, 0.03, TissueSim.Depth.SKIN), TissueSim.OPEN_GAP, "and its edges meet")
	var drawn := driver.body.site.get_node("Staples/Wire") as MeshInstance3D
	var layers: Array = drawn.get_meta("layers")
	assert_true(layers.has(TissueSim.Depth.MUSCLE) and layers.has(TissueSim.Depth.SKIN), "staples went into both the muscle and the skin")
	var paths: Array = drawn.get_meta("paths")
	_assert_staples_across(range(paths.size()).filter(func(i: int) -> bool: return layers[i] == TissueSim.Depth.SKIN).map(func(i: int) -> PackedVector3Array: return paths[i]), wound)
	await driver.player_puts_down()
	# Back at the patient, to see the result from where the surgeon works.
	await driver.player_walks_to(driver.site_point(wound.midpoint()))
	await driver.capture("stapled")
	await _finish()


func test_office_stapler_can_tear_or_bleed() -> void:
	await _start("appendectomy", "office")
	var patient := driver.patient
	var from := Vector2(0.4, 0.45)
	var to := from + Vector2(driver.body.meters_to_uv(0.05), 0.0)
	along_cut = driver.site_point(to) - driver.site_point(from)
	await driver.player_cuts_skin(from, to, 1)
	await driver.player_puts_down()
	await driver.seconds(1.0)
	var wound: Wound = patient.wounds[-1]
	SurgeryState.tool_is_on_tray(driver.surgery, "office_stapler")
	var stapler := await driver.player_requests_item("office_stapler")
	var def := stapler.def
	assert_between(def.tear_chance + def.bleed_chance, 0.01, 0.25, "an office staple only now and then goes wrong")
	var chances := [def.tear_chance, def.bleed_chance]
	def.tear_chance = 1.0
	def.bleed_chance = 0.0
	var at := driver.site_point(wound.points[0].lerp(wound.points[-1], 0.25))
	await driver.player_walks_to(at)
	await driver.player_turns_blade(along_cut)
	await driver.player_reaches(at)
	var wounds := patient.wounds.size()
	await _click()
	assert_true(patient.flags.has("tears"), "an office staple that tears out tears the skin")
	assert_eq(patient.wounds.size(), wounds + 1, "a tear is a new wound")
	assert_lt(wound.closure(), 0.1, "and the staple holds nothing")
	await driver.capture("torn")

	def.tear_chance = 0.0
	def.bleed_chance = 1.0
	at = driver.site_point(wound.points[0].lerp(wound.points[-1], 0.7))
	await driver.player_reaches(at)
	wounds = patient.wounds.size()
	await _click()
	await driver.seconds(1.0)
	var site_m := driver.body.uv_to_meters(1.0)
	assert_eq(patient.wounds.size(), wounds, "a staple through a vessel makes no new wound to close")
	assert_gt(wound.bleed_rate(site_m, 1.0), Patient.STAPLE_NICK * 0.5, "but the cut bleeds through it")
	var wire := driver.body.site.get_node_or_null("Staples/Wire") as MeshInstance3D
	assert_true(wire != null and (wire.get_meta("paths") as Array).size() == 1, "the staple still goes in")
	assert_true(patient.flags.has("office_staples"), "the report hears about the office staples")
	await driver.capture("bleeding")
	def.tear_chance = chances[0]
	def.bleed_chance = chances[1]
	await driver.player_puts_down()
	await driver.player_stops_bleeding(0.05)
	assert_lt(wound.bleed_rate(site_m, 1.0), 0.05, "cauterized, the nicked vessel stops bleeding")
	await _finish()


func _click() -> void:
	await driver.frames(2)
	driver.use()
	await driver.frames(4)
	driver.use(false)
	await driver.frames(3)


## The stapler hovers over the skin, and each ring of its aim lies on the skin under where its leg comes down.
func _assert_rings_on_skin(stapler: SurgicalTool) -> void:
	var legs := ToolActions.staple_legs(stapler, driver.body)
	var above := legs[0].y - _skin_y(legs[0])
	assert_gt(above, 0.003, "the stapler hovers %.1f mm over the skin" % (above * 1000.0))
	var rings := driver.surgery.hud.leg_rings
	for side in 2:
		var ring: PackedVector3Array = rings[side]
		var middle := Vector3.ZERO
		for p in ring:
			middle += p / ring.size()
			assert_lt(absf(p.y - _skin_y(p)), 0.0005, "ring %d lies on the skin" % side)
		assert_lt((middle - legs[side]).slide(Vector3.UP).length(), 0.0005, "right under leg %d" % side)


## Holds the stapler square across the arm with one leg just past its side, where the surface drops away to the drape
## or the table, and checks the rings there.
func _aim_over_the_arms_edge(stapler: SurgicalTool, middle: Vector3) -> void:
	var me := driver.me
	var top: float = me._surface_below(middle, false).y
	var out := middle
	for i in 40:
		out += Vector3(0.0, 0.0, 0.003)
		if me._surface_below(out, false).y < top - 0.02:
			break
	var span := stapler.def.staple_span
	await driver.player_reaches(out - Vector3(0.0, 0.0, span * 0.5 - 0.002))
	await driver.frames(2)
	var legs := ToolActions.staple_legs(stapler, driver.body)
	var drops := legs.map(func(leg: Vector3) -> float: return top - float(me._surface_below(leg, false).y))
	assert_gt(drops.max(), 0.02, "one leg is past the side of the arm (%s m below its top)" % [drops])
	_assert_rings_lie_flat()


## Wherever the legs are, even one past the side of the arm: each ring lies on what's under its middle, no steeper than
## 45 degrees, not floating in the air or standing on end.
func _assert_rings_lie_flat() -> void:
	for side in 2:
		var ring: PackedVector3Array = driver.surgery.hud.leg_rings[side]
		var middle := Vector3.ZERO
		for p in ring:
			middle += p / ring.size()
		var under: float = driver.me._surface_below(middle, false).y
		assert_lt(absf(middle.y - under), 0.002, "ring %d lies on what's under it (%.1f mm off)" % [side, (middle.y - under) * 1000.0])
		for p in ring:
			assert_lte(absf(p.y - middle.y), Hud.LEG_RING * 1.5, "ring %d doesn't stand on end" % side)


## The middles of the stapler's two rings (world), where the aim shows its legs going in.
func _ring_middles() -> Array[Vector3]:
	var middles: Array[Vector3] = []
	for ring: PackedVector3Array in driver.surgery.hud.leg_rings:
		var middle := Vector3.ZERO
		for p in ring:
			middle += p / ring.size()
		middles.append(middle)
	return middles


## The staple's legs went in where the rings showed them (`aimed`, world) before the click, across the floor.
func _assert_legs_where_aimed(stapler: SurgicalTool, aimed: Array[Vector3]) -> void:
	for side in 2:
		var miss := (stapler.staple_aim[side] - aimed[side]).slide(Vector3.UP).length()
		assert_lt(miss, 0.0005, "leg %d goes in where its ring showed it (%.1f mm off)" % [side, miss * 1000.0])


## The skin's height (world) as drawn under p.
func _skin_y(p: Vector3) -> float:
	var body := driver.body
	var local := body.site.to_local(p)
	return body.site.to_global(Vector3(local.x, body.skin_height(body.world_to_uv(p)), local.z)).y


## Every staple bridges the cut: its legs on both sides of the wound's line, each within the staple's span of it.
func _assert_staples_across(paths: Array, wound: Wound) -> void:
	var size := driver.body.site_size
	for path: PackedVector3Array in paths:
		var a := Vector2(path[1].x / size.x + 0.5, path[1].z / size.y + 0.5)
		var b := Vector2(path[-2].x / size.x + 0.5, path[-2].z / size.y + 0.5)
		var line := wound.closest_point((a + b) * 0.5)
		var across := (b - a).orthogonal()
		assert_lt((a - line).dot((b - a)) * (b - line).dot((b - a)), 0.0, "a staple has a leg on each side of the cut")
		var widest := Db.tool("skin_stapler").staple_span + Db.tool("skin_stapler").staple_give * 2.0
		assert_lt(path[1].distance_to(path[-2]), widest * 1.1, "and is no wider than the stapler reaches")
		assert_gt(absf(across.normalized().dot((wound.points[-1] - wound.points[0]).normalized())), 0.8, "lying square across the cut")
		for i in range(1, path.size() - 1):
			var uv := Vector2(path[i].x / size.x + 0.5, path[i].z / size.y + 0.5)
			assert_gte(path[i].y, driver.body.skin_height(uv) + PatientBody.STAPLE_RADIUS, "its crown rides on the skin, not in it")


func _capture_view(key_frame: String) -> void:
	driver.budget_paused = true
	assert_true(await shots.capture_view(key_frame), "saved the surgeon's view %s" % key_frame)
	driver.budget_paused = false
	driver.budget.resume()


## Starts `scenario_id` with the patient asleep (the staples don't make them flinch: the setting, not what's tested),
## and in a run with key frames saves the case's under KEY_FRAMES/`case_name`, starting untouched: the site from above
## and obliquely along the cut, and what the surgeon sees.
func _start(scenario_id: String, case_name: String) -> void:
	RenderingServer.render_loop_enabled = false
	driver = Driver.new()
	add_child(driver)
	await driver.start(scenario_id)
	along_cut = Vector3.ZERO
	SurgeryState.patient_is_asleep(driver.patient)
	if KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(driver.surgery, KEY_FRAMES.path_join(case_name))
		driver.on_key_frame = func(key_frame: String) -> void:
			assert_true(await shots.capture(key_frame, along_cut), "%s: saved key frame %s" % [case_name, key_frame])
			assert_true(await shots.capture_view(key_frame), "%s: saved the surgeon's view %s" % [case_name, key_frame])
		await driver.capture("untouched")
	# Loading the room isn't gameplay: the frame budget counts from here.
	driver.budget.clear()


func _finish() -> void:
	driver.budget.check(self, shots != null, "", "known to go over the frame budget: each staple changes the tissue, and rebuilding its layers takes about 6 ms (PatientBody._rebuild_layers()); not optimized yet")
	if shots:
		gut.p("key frames: %s" % shots.out_dir)
		shots.end()
		shots.queue_free()
		shots = null
	await driver.stop()
	driver.queue_free()
	RenderingServer.render_loop_enabled = true
	# Sounds play in real time and the game ran faster: the last staple's still playing would be reported as a leak
	# when the run quits right after.
	var quiet := Time.get_ticks_msec() + 1500
	while Time.get_ticks_msec() < quiet:
		await get_tree().process_frame
