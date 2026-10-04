extends GutTest
## The needle as a player uses it: a running thread clicked through a cut hole by hole, pulled on the wheel and tied off
## with a long hold, layer by layer through a cut into the belly. Headless assertions in smoke; with key frames also the
## site untouched, after the first two holes and tied off. Review those for one continuous thread dipping into its
## holes, edges meeting in a slight ridge without passing through each other, and a narrow incision line still visible
## beneath the tied thread rather than an open red gap or seamless skin.

const TAGS = ["smoke", "tool_needle", "tissue_modification", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")
const FrameBudget := preload("res://tests/support/frame_budget.gd")
const KEY_FRAMES := "res://build/test-artifacts/screenshots/needle"

var driver: Driver
var shots: KeyFrames


func test_running_suture_closes_a_forearm_cut() -> void:
	await _start("hand_stitch", "forearm_skin")
	var patient := driver.patient
	var tissue := driver.body.tissue
	var wound: Wound = patient.wounds[0]
	var needle := await driver.player_requests_item("needle")
	assert_lt(driver.me.aim_point().distance_to(needle.tip_position()), 0.0001, "the aim point is the needle tip")
	assert_null(needle.find_child("Thread", true, false), "an idle needle has no placeholder thread")
	var jaw_a := needle.find_child("JawA", true, false) as Node3D
	var jaw_b := needle.find_child("JawB", true, false) as Node3D
	assert_lt(absf(jaw_a.transform.basis.get_euler().y) + absf(jaw_b.transform.basis.get_euler().y), 0.001, "the holder stays closed around its needle")
	assert_eq(driver.body.site.get_node("Sutures").get_child_count(), 0, "no live thread is drawn before the needle is anchored")
	await driver.player_walks_to(driver.site_point(wound.midpoint()))
	await driver.player_reaches(driver.site_point(wound.midpoint()))
	assert_eq(driver.body.site.get_node("Sutures").get_child_count(), 0, "hovering the needle over a wound does not create thread")
	assert_lt(absf(needle.global_basis.z.dot(Vector3.UP)), 0.35, "the loaded needle holder rests nearly horizontal")
	assert_lt(driver.me.aim_point().distance_to(needle.tip_position()), 0.0001, "the horizontal pose keeps the aiming point on the sharp tip")
	if shots:
		driver.budget_paused = true
		var saved := await shots.capture_view("needle_ready")
		driver.budget_paused = false
		driver.budget.resume()
		assert_true(saved, "saved the loaded needle holder from the player's view")
	assert_true(await driver.player_threads(wound, TissueSim.Depth.SKIN), "holes go in beside the cut")
	var thread := needle.suture_thread
	var info := tissue.thread_info(thread)
	assert_eq(info.springs.size(), info.anchors.size() - 1, "one thread runs through every hole, a span between each two")
	var drawn := driver.body.site.get_node("Sutures/Suture%d" % thread)
	var live := drawn.get_node("Live") as MeshInstance3D
	assert_true(live.mesh != null, "the newest hole has a live strand")
	assert_true(live.get_aabb().grow(0.001).has_point(driver.body.site.to_local(needle.tip_position())), "the live strand ends at the needle tip")
	# Sub-threshold moves must accumulate against the rendered endpoint, not disappear one frame at a time.
	# These are thirty synthetic updates in one frame; don't count the assertion batch as a gameplay frame.
	driver.budget_paused = true
	var needle_pose := needle.global_transform
	for frame in 30:
		needle.global_position += Vector3.RIGHT * 0.0001
		driver.body._update_sutures()
		var live_path: PackedVector3Array = live.get_meta("path")
		assert_lte(live_path[-1].distance_to(driver.body.site.to_local(needle.tip_position())), 0.00021, "slow movement keeps the strand within the redraw threshold")
	needle.global_transform = needle_pose
	driver.body._update_sutures()
	driver.budget_paused = false
	driver.budget.resume()
	var routed := drawn.get_node("Routed") as MeshInstance3D
	var loose_path: PackedVector3Array = (routed.get_meta("paths") as Array)[0]
	var pressure := drawn.get_node("Pressure") as Node3D
	assert_eq(float(pressure.get_meta("amount")), 0.0, "loose thread puts no visible compression around its holes")
	assert_true((drawn.get_node("StartKnot") as MeshInstance3D).mesh != null, "the beginning of the running thread has a compact anchor knot")
	_assert_thread_clears_skin(loose_path, "loose exposed thread")
	assert_lt(wound.closure(), 0.99, "the thread as it comes leaves the cut open")
	await driver.player_pulls_thread("closed")
	assert_gt(wound.closure(), 0.99, "pulled until it reads closed, the thread closes the cut along its whole length")
	var tight_path: PackedVector3Array = (routed.get_meta("paths") as Array)[0]
	assert_lt(_path_length(tight_path), _path_length(loose_path), "tightening takes visible slack out of the thread")
	assert_gt(float(pressure.get_meta("amount")), 0.0, "closing pressure appears around every puncture")
	assert_eq(int(pressure.get_meta("hole_count")), info.anchors.size(), "every pressured hole has a skin crease sprite")
	var pressure_marks := pressure.get_node("Marks") as MultiMeshInstance3D
	assert_true(pressure_marks.visible, "the pressure sprites render while the thread holds")
	assert_eq(pressure_marks.multimesh.instance_count, info.anchors.size(), "the renderer has one pressure sprite per puncture")
	_assert_thread_clears_skin(tight_path, "tight exposed thread")
	await driver.player_ties_off()
	await driver.seconds(1.0)
	assert_true(patient.suture_done(thread) and needle.suture_thread == 0, "a long hold ties the thread off")
	assert_lt(tissue.gap_along(wound.points, 0.03, TissueSim.Depth.SKIN), TissueSim.OPEN_GAP, "the tied off thread holds the edges together")
	var split := Array(tissue.severed()).filter(func(s: int) -> bool: return tissue.cut_depth(s) >= TissueSim.Depth.SKIN)
	assert_eq(split.size(), 0, "no split edge is left along the seam")
	assert_gt(Array(tissue.suture_lip).max(), 0.0005, "the pressed edges rise into a lip instead of passing through each other")
	assert_lt(driver.body.wound_map.value(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, wound.midpoint()), 0.12, "no blood stands in the closed seam")
	var seam := driver.body.wound_map.value(WoundMap.Layer.WOUNDS, WoundMap.CUT, wound.midpoint())
	assert_gt(seam, 0.06, "a tied wound keeps a visible incision line")
	assert_lt(seam, 0.16, "the incision line is healed-looking rather than an open groove")
	assert_gt(driver.body.wound_map.value(WoundMap.Layer.SEAMS, WoundMap.CLOSED_SEAM, wound.midpoint()), 0.9, "the sewn incision has an explicit seam mask independent of closure quality")
	assert_true(patient.flags.has("neat_closure"), "a closed, tied off thread is a neat closure")
	assert_true(live.mesh == null, "tying off releases the live end from the needle")
	assert_true((drawn.get_node("StartKnot") as MeshInstance3D).mesh != null, "the tied thread keeps its starting knot")
	assert_true((drawn.get_node("EndKnot") as MeshInstance3D).mesh != null, "the tied thread ends in a second visible knot")
	var routes: Array = routed.get_meta("paths")
	assert_eq(routes.size(), ceili(info.springs.size() / 2.0), "the running thread alternates exposed and subcutaneous spans")
	for i in routes.size():
		_assert_thread_clears_skin(routes[i], "exposed span %d" % i)
	await _finish()


func _assert_thread_clears_skin(path: PackedVector3Array, label: String) -> void:
	for i in range(1, path.size() - 1):
		var p := path[i]
		var uv := Vector2(p.x / driver.body.site_size.x + 0.5, p.z / driver.body.site_size.y + 0.5)
		assert_gte(p.y, driver.body.skin_height(uv) + PatientBody.SUTURE_RADIUS, "%s stays above the skin at sample %d" % [label, i])


static func _path_length(path: PackedVector3Array) -> float:
	var length := 0.0
	for i in range(1, path.size()):
		length += path[i - 1].distance_to(path[i])
	return length


## Too loose leaves a gap, too tight tears through, and skin pulled shut over open muscle tears through too.
func test_thread_tension_limits() -> void:
	await _start("appendectomy", "tension")
	assert_lte(ToolActions.SUTURE_TENSION_STEP, 0.04, "the thread wheel has fine tension steps")
	var patient := driver.patient
	var tissue := driver.body.tissue
	var loose := SurgeryState.skin_is_cut(patient, Vector2(0.3, 0.3), Vector2(0.55, 0.3), 0.5)
	var tight := SurgeryState.skin_is_cut(patient, Vector2(0.3, 0.5), Vector2(0.55, 0.5), 0.5)
	var deep := SurgeryState.skin_is_cut(patient, Vector2(0.3, 0.7), Vector2(0.55, 0.7), 1.0)
	await driver.seconds(1.0)
	await driver.capture("cuts_before_sutures")
	var needle := await driver.player_requests_item("needle")

	await driver.player_threads(loose, TissueSim.Depth.SKIN)
	await driver.player_ties_off("loose_tied")
	await driver.seconds(1.0)
	assert_lt(loose.closure(), 0.99, "tied off loose, the thread leaves the cut open")
	assert_gt(tissue.gap_along(loose.points, 0.03, TissueSim.Depth.SKIN), TissueSim.OPEN_GAP, "and its edges apart")
	assert_false(patient.flags.has("neat_closure"), "a loose closure isn't a neat one")

	await driver.player_threads(tight, TissueSim.Depth.SKIN)
	var thread := needle.suture_thread
	await driver.player_pulls_thread("too tight")
	assert_eq(ToolActions.thread_state(needle), "too tight", "the hand status reads too tight before it tears")
	assert_false(patient.suture_done(thread), "too tight thread is still intact before further tightening")
	assert_gt(tight.closure(), 0.99, "too tight still holds the cut closed")
	assert_gt(Array(tissue.suture_lip).max(), 0.0009, "too tight thread visibly puckers the joined skin")
	var tight_drawn := driver.body.site.get_node("Sutures/Suture%d" % thread)
	var tight_pressure := tight_drawn.get_node("Pressure") as Node3D
	assert_gt(float(tight_pressure.get_meta("amount")), 0.45, "over-tight thread strongly marks the skin around its holes")
	assert_eq(int(tight_pressure.get_meta("hole_count")), tissue.thread_info(thread).anchors.size(), "every over-tight hole shows pressure")
	assert_eq((tight_pressure.get_node("Marks") as MultiMeshInstance3D).multimesh.instance_count, tissue.thread_info(thread).anchors.size(), "every over-tight hole has a rendered pressure sprite")
	await driver.capture("too_tight_intact")
	var wounds := patient.wounds.size()
	for i in 6:
		if patient.suture_done(thread):
			break
		await driver.notch(false)
	assert_true(patient.suture_done(thread) and needle.suture_thread == 0, "pulled tighter still, the thread tears through")
	assert_true(Array(tissue.thread_info(thread).springs).all(func(s: int) -> bool: return tissue.c_active[s] == 0), "a torn thread lets go of every span")
	assert_lt(tight.closure(), 0.01, "and holds the cut no more")
	assert_gt(patient.wounds.size(), wounds, "it tears the skin")
	assert_true(driver.surgery.scoring.entries.has("suture_tear_through"), "a thread torn through costs points")

	await driver.player_pulls_thread("loose")
	await driver.player_threads(deep, TissueSim.Depth.SKIN)
	assert_lt(deep.closure(), 0.2, "skin won't meet over open muscle")
	thread = needle.suture_thread
	await driver.player_pulls_thread("closed")
	assert_true(patient.suture_done(thread), "pulled shut over open muscle, the skin thread tears through")
	await _finish()


## A cut through the belly wall sewn layer by layer from inside the opening: the muscle, the fat, then the skin.
func test_layered_closure_of_a_cut_into_the_belly() -> void:
	await _start("appendectomy", "layers")
	var patient := driver.patient
	var tissue := driver.body.tissue
	# Where the belly's sides hang off the table the site isn't drawn at all: what the fat layer shows uncut.
	var whole_fat := tissue.triangles(TissueSim.Depth.FAT).size()
	var wound := SurgeryState.skin_is_cut(patient, Vector2(0.3, 0.5), Vector2(0.7, 0.5), 1.0)
	await driver.seconds(1.0)
	var middle := wound.midpoint()
	await driver.capture("cut")
	await driver.player_requests_item("needle")
	await _sew_deep_layer(wound, TissueSim.Depth.MUSCLE)
	assert_false(tissue.muscle_open_near(middle, Patient.MUSCLE_REACH), "the muscle thread closes the muscle")
	assert_false(driver.body.is_open(middle), "the sewn muscle closes the way into the belly")
	await _sew_deep_layer(wound, TissueSim.Depth.FAT)
	assert_false(tissue.fat_open_near(middle, Patient.MUSCLE_REACH), "a thread through the fat closes it")
	assert_eq(tissue.triangles(TissueSim.Depth.FAT).size(), whole_fat, "the fat layer shows no opening")
	await driver.player_sews(wound, TissueSim.Depth.SKIN)
	await driver.seconds(1.0)
	assert_gt(wound.closure(), 0.99, "the skin closes over the sewn layers")
	assert_lt(tissue.gap_along(wound.points, 0.03, TissueSim.Depth.SKIN), TissueSim.OPEN_GAP, "and its edges meet")
	await _finish()


func _sew_deep_layer(wound: Wound, layer: int) -> void:
	assert_true(await driver.player_threads(wound, layer), "the horizontal needle reaches every deep puncture")
	var needle := driver.me.held_tool(driver.me.active)
	var root := driver.body.site.get_node("Sutures/Suture%d" % needle.suture_thread)
	var routes: Array = root.get_node("Routed").get_meta("paths")
	assert_false(routes.is_empty(), "the deep layer has exposed thread spans")
	_assert_thread_stays_on_layer(routes, layer)
	await driver.capture("%s_loose" % TissueSim.Depth.keys()[layer].to_lower())
	await driver.player_pulls_thread("closed")
	await driver.player_ties_off("%s_closed" % TissueSim.Depth.keys()[layer].to_lower())
	_assert_thread_stays_on_layer(root.get_node("Routed").get_meta("paths"), layer)


func _assert_thread_stays_on_layer(routes: Array, layer: int) -> void:
	for path: PackedVector3Array in routes:
		for i in range(1, path.size() - 1):
			var uv := Vector2(path[i].x / driver.body.site_size.x + 0.5, path[i].z / driver.body.site_size.y + 0.5)
			var surface := driver.body._suture_layer_height(uv, layer)
			assert_gte(path[i].y, surface + PatientBody.SUTURE_RADIUS, "deep thread rests above its sewn layer")
			assert_lt(path[i].y, surface + 0.002, "deep thread does not rise through the overlying tissue")


func test_finished_suture_redraw_and_cancelled_press() -> void:
	await _start("appendectomy", "finished_threads")
	var needle := await driver.player_requests_item("needle")
	driver.budget_paused = true
	ToolActions._sew(needle, driver.patient, "site", Vector2(0.4, 0.4), true, true, false, 0.1, needle.tip_position())
	ToolActions._sew(needle, driver.patient, "", Vector2.ZERO, true, false, true, 0.0, needle.tip_position())
	assert_eq(needle.suture_thread, 0, "releasing off the patient cancels the pending puncture")
	assert_true(driver.body.tissue.thread_ids().is_empty(), "a cancelled press makes no hole")
	var tissue := driver.body.tissue
	SurgeryState.skin_has_finished_threads(driver.patient, 12, 100)
	driver.body._update_sutures()
	var knots: Array[Mesh] = []
	for id in range(100, 112):
		knots.append((driver.body.site.get_node("Sutures/Suture%d/StartKnot" % id) as MeshInstance3D).mesh)
	var worst := 0
	for frame in 60:
		# A sim step elsewhere must not invalidate the meshes of unchanged punctures.
		tissue.steps_done += 1
		var start := Time.get_ticks_usec()
		driver.body._update_sutures()
		worst = maxi(worst, Time.get_ticks_usec() - start)
	for id in range(100, 112):
		assert_eq((driver.body.site.get_node("Sutures/Suture%d/StartKnot" % id) as MeshInstance3D).mesh, knots[id - 100], "stationary tied knots are not rebuilt by unrelated tissue steps")
	assert_lt(worst, 16000, "twelve finished threads fit the 16 ms update budget (worst %d us)" % worst)
	gut.p("twelve finished sutures: worst renderer update %.2f ms" % (worst / 1000.0))
	# Also exercise the normal solver/render loop with all twelve finished threads present.
	driver.budget_paused = false
	driver.budget.resume()
	await driver.seconds(1.0)
	await _finish()


## Starts `scenario_id` with the patient asleep (the cuts and holes don't make them flinch: the setting, not what's
## tested), and in a run with key frames saves the case's under KEY_FRAMES/`case_name`, starting untouched.
func _start(scenario_id: String, case_name: String) -> void:
	RenderingServer.render_loop_enabled = false
	driver = Driver.new()
	add_child(driver)
	await driver.start(scenario_id)
	SurgeryState.patient_is_asleep(driver.patient)
	if KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(driver.surgery, KEY_FRAMES.path_join(case_name))
		driver.on_key_frame = func(key_frame: String) -> void:
			assert_true(await shots.capture(key_frame), "%s: saved key frame %s" % [case_name, key_frame])
		await driver.capture("untouched")
	# Loading the room isn't gameplay: the frame budget counts from here.
	driver.budget.clear()


func _finish() -> void:
	if shots and FrameBudget.enforced():
		assert_true(driver.budget.within(), driver.budget.summary())
	else:
		gut.p(driver.budget.summary())
	if shots:
		gut.p("key frames: %s" % shots.out_dir)
		shots.end()
		shots.queue_free()
		shots = null
	await driver.stop()
	driver.queue_free()
	RenderingServer.render_loop_enabled = true
