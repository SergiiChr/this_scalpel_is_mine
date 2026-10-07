extends GutTest
## A wound counts as closed exactly where it looks closed, whatever closed it: staples or a running thread leaving gaps
## between them leave it open and bleeding there, and once its edges meet along its length it's closed and stops
## bleeding. The forearm cut of Hand Stitch, stapled and sewn like a player would. Headless assertions in smoke; with key
## frames also the gaps left and the cut closed. Review them for gaps between the closures where the cut counts open,
## and no gap left where it counts closed.

const TAGS = ["smoke", "tissue_modification", "tool_skin_stapler", "tool_needle", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")
const KEY_FRAMES := "res://build/test-artifacts/screenshots/wound_closure"
## Edges further apart than this (meters) show a gap; closer than MEETS they meet. Between the two it's a judgement call
## the checks leave alone.
const SHOWS := 0.002
const MEETS := TissueSim.CLOSED_GAP
## A gap this close (meters) to a point of the cut is at that point; edges meeting this far round it, it's closed there.
## The game looks in between (TissueSim.seam_reach()): it may count a point either way where a gap starts just past it.
const AT := 0.003
const AROUND := 0.008
## Blood (ml/s) a closed wound may still lose: none to speak of.
const DRY := 0.02

var driver: Driver
var shots: KeyFrames
var along_cut := Vector3.ZERO


func test_staples_count_closed_only_where_the_edges_meet() -> void:
	await _start("staples")
	var wound: Wound = driver.patient.wounds[0]
	await driver.player_requests_item("skin_stapler")
	await driver.player_staples(wound, 0.025, 1)
	await driver.seconds(1.0)
	assert_gt(_widest_gap(wound), SHOWS, "staples 2.5 cm apart leave gaps between them\n%s" % driver.recent())
	assert_lt(wound.closure(), 0.99, "so the cut isn't closed")
	assert_gt(_bleeding(wound), DRY, "and it still bleeds")
	_assert_counts_as_seen(wound, "sparse staples")
	await driver.capture("gaps")

	# Filled in a centimeter apart, then wherever the rings still show a gap, like a player looking it over.
	await driver.player_staples(wound, 0.01, 2)
	await driver.seconds(1.0)
	assert_lt(_widest_gap(wound), SHOWS, "filled in, no gap shows along the cut\n%s" % driver.recent())
	assert_gt(wound.closure(), 0.99, "so it's closed\n%s" % _open_bins(wound))
	assert_lt(_bleeding(wound), DRY, "and doesn't bleed")
	_assert_counts_as_seen(wound, "filled in")
	await driver.player_puts_down()
	await driver.capture("closed")
	await _finish()


func test_thread_counts_closed_only_where_the_edges_meet() -> void:
	await _start("thread")
	var wound: Wound = driver.patient.wounds[0]
	await driver.player_requests_item("needle")
	assert_true(await driver.player_threads(wound, TissueSim.Depth.SKIN, 5.0), "holes go in beside the cut, far apart")
	await driver.player_pulls_thread("closed")
	await driver.seconds(1.0)
	assert_gt(_widest_gap(wound), SHOWS, "pulled to closed, holes far apart still leave gaps between them\n%s" % driver.recent())
	assert_lt(wound.closure(), 0.99, "so the cut isn't closed")
	assert_gt(_bleeding(wound), DRY, "and it still bleeds")
	_assert_counts_as_seen(wound, "wide thread")
	await driver.capture("gaps")

	await driver.player_ties_off("tied")
	await driver.seconds(1.0)
	assert_lt(_widest_gap(wound), SHOWS, "tied off, the thread gathers the edges between its holes too")
	assert_gt(wound.closure(), 0.99, "so it's closed")
	assert_lt(_bleeding(wound), DRY, "and doesn't bleed")
	_assert_counts_as_seen(wound, "tied thread")
	await _finish()


## The office stapler as in a real run: every staple catches a vessel, the cut bleeds through the staples, cautery
## stops it. Stapled shut and dry, the cut counts closed, enough for Hand Stitch's "Close the cut".
func test_office_staples_through_vessels_count_closed_once_dry() -> void:
	await _start("office")
	var wound: Wound = driver.patient.wounds[0]
	SurgeryState.tool_is_on_tray(driver.surgery, "office_stapler")
	var stapler := await driver.player_requests_item("office_stapler")
	var chances := [stapler.def.tear_chance, stapler.def.bleed_chance]
	stapler.def.tear_chance = 0.0
	stapler.def.bleed_chance = 1.0
	await driver.player_staples(wound, 0.01, 2)
	stapler.def.tear_chance = chances[0]
	stapler.def.bleed_chance = chances[1]
	await driver.seconds(1.0)
	assert_eq(driver.patient.wounds.size(), 1, "the nicked vessels make no new wounds")
	assert_gt(_bleeding(wound), DRY, "but the stapled cut bleeds through")
	await driver.player_puts_down()
	await driver.player_stops_bleeding(DRY)
	await driver.seconds(1.0)
	assert_lt(_bleeding(wound), DRY, "the cautery stops it")
	assert_gte(driver.patient.skin_closure(), Patient.CLOSED_ENOUGH, "stapled and dry, the cut is closed enough for \"Close the cut\"\n%s" % _open_bins(wound))
	_assert_counts_as_seen(wound, "office staples")
	await driver.capture("closed")
	await _finish()


## Every bin of `wound` showing a gap counts open, every one whose edges meet next to a closure counts closed.
func _assert_counts_as_seen(wound: Wound, label: String) -> void:
	var tissue := driver.body.tissue
	var wrong := PackedStringArray()
	for i in wound.bins.size():
		var at := wound.bin_position(i)
		var gap := _gap_at(at, AT)
		if gap > SHOWS and wound.bins[i] >= 1.0:
			wrong.append("bin %d counts closed with a %.1f mm gap" % [i, gap * 1000.0])
		elif _gap_at(at, AROUND) < MEETS and tissue.held_near(PackedVector2Array([at]))[0] == 1 and wound.bins[i] < 1.0:
			wrong.append("bin %d counts %.2f closed with its edges meeting" % [i, wound.bins[i]])
	assert_true(wrong.is_empty(), "%s: the cut counts as closed where it looks closed\n%s" % [label, "\n".join(wrong)])


## The bins of `wound` that count open, with their gap, whether a closure holds them and whether a staple has an
## edge left to take there.
func _open_bins(wound: Wound) -> String:
	var tissue := driver.body.tissue
	var lines := PackedStringArray()
	for i in wound.bins.size():
		if wound.bins[i] < 1.0:
			var at := wound.bin_position(i)
			lines.append("bin %d: %.2f, gap %.1f mm, held %d, unstitched edge %s" % [i, wound.bins[i], _gap_at(at, AT) * 1000.0, tissue.held_near(PackedVector2Array([at]))[0], tissue.skin_open_near(at, tissue.open_reach())])
	return "\n".join(lines)


## How far apart the skin's edges are pulled across the cut within `look` (meters) of uv (meters), measured on the
## springs the layers are drawn from.
func _gap_at(uv: Vector2, look: float) -> float:
	var tissue := driver.body.tissue
	var gap := 0.0
	for s in tissue.severed():
		var crossing := tissue.uv_of(tissue.c_a[s]).lerp(tissue.uv_of(tissue.c_b[s]), tissue.c_cross[s])
		if ((crossing - uv) * tissue.size).length() < look:
			var a := tissue.c_a[s]
			var b := tissue.c_b[s]
			gap = maxf(gap, tissue.pos[a].distance_to(tissue.pos[b]) - tissue.rest[a].distance_to(tissue.rest[b]))
	return gap


func _widest_gap(wound: Wound) -> float:
	var gap := 0.0
	for i in wound.bins.size():
		gap = maxf(gap, _gap_at(wound.bin_position(i), AT))
	return gap


func _bleeding(wound: Wound) -> float:
	return wound.bleed_rate(driver.body.uv_to_meters(1.0), 1.0)


## Starts Hand Stitch with the patient asleep, and in a run with key frames saves the case's under
## KEY_FRAMES/`case_name`, starting untouched.
func _start(case_name: String) -> void:
	RenderingServer.render_loop_enabled = false
	driver = Driver.new()
	add_child(driver)
	await driver.start("hand_stitch")
	SurgeryState.patient_is_asleep(driver.patient)
	var wound: Wound = driver.patient.wounds[0]
	along_cut = driver.site_point(wound.points[-1]) - driver.site_point(wound.points[0])
	if KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(driver.surgery, KEY_FRAMES.path_join(case_name))
		driver.on_key_frame = func(key_frame: String) -> void:
			assert_true(await shots.capture(key_frame, along_cut), "%s: saved key frame %s" % [case_name, key_frame])
		await driver.capture("untouched")
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
	# Sounds play in real time and the game ran faster: one still playing would be reported as a leak at quit.
	var quiet := Time.get_ticks_msec() + 1500
	while Time.get_ticks_msec() < quiet:
		await get_tree().process_frame
