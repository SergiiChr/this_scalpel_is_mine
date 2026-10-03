extends GutTest
## The needle as a player uses it: a running thread clicked through a cut hole by hole, pulled on the wheel and tied off
## with a long hold, layer by layer through a cut into the belly. Headless assertions in smoke; with key frames also the
## site untouched, after the first two holes and tied off. Review those for one continuous thread dipping into its
## holes, edges meeting in a slight ridge without passing through each other, and no red opening left along the seam.

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
	assert_true(await driver.player_threads(wound, TissueSim.Depth.SKIN), "holes go in beside the cut")
	var thread := needle.suture_thread
	var info := tissue.thread_info(thread)
	assert_eq(info.springs.size(), info.anchors.size() - 1, "one thread runs through every hole, a span between each two")
	assert_lt(wound.closure(), 0.99, "the thread as it comes leaves the cut open")
	await driver.player_pulls_thread("closed")
	assert_gt(wound.closure(), 0.99, "pulled until it reads closed, the thread closes the cut along its whole length")
	await driver.player_ties_off()
	await driver.seconds(1.0)
	assert_true(patient.suture_done(thread) and needle.suture_thread == 0, "a long hold ties the thread off")
	assert_lt(tissue.gap_along(wound.points, 0.03, TissueSim.Depth.SKIN), TissueSim.OPEN_GAP, "the tied off thread holds the edges together")
	var split := Array(tissue.severed()).filter(func(s: int) -> bool: return tissue.cut_depth(s) >= TissueSim.Depth.SKIN)
	assert_eq(split.size(), 0, "no split edge is left along the seam")
	assert_gt(Array(tissue.suture_lip).max(), 0.0005, "the pressed edges rise into a lip instead of passing through each other")
	assert_lt(driver.body.wound_map.value(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, wound.midpoint()), 0.12, "no blood stands in the closed seam")
	assert_true(patient.flags.has("neat_closure"), "a closed, tied off thread is a neat closure")
	var drawn := driver.body.site.get_node("Sutures/Suture%d" % thread)
	assert_eq(drawn.get_children().filter(func(span: MeshInstance3D) -> bool: return span.mesh != null).size(), info.springs.size(), "the thread is drawn span by span")
	await _finish()


## Too loose leaves a gap, too tight tears through, and skin pulled shut over open muscle tears through too.
func test_thread_tension_limits() -> void:
	await _start("appendectomy", "tension")
	var patient := driver.patient
	var tissue := driver.body.tissue
	var loose := SurgeryState.skin_is_cut(patient, Vector2(0.3, 0.3), Vector2(0.55, 0.3), 0.5)
	var tight := SurgeryState.skin_is_cut(patient, Vector2(0.3, 0.5), Vector2(0.55, 0.5), 0.5)
	var deep := SurgeryState.skin_is_cut(patient, Vector2(0.3, 0.7), Vector2(0.55, 0.7), 1.0)
	await driver.seconds(1.0)
	var needle := await driver.player_requests_item("needle")

	await driver.player_threads(loose, TissueSim.Depth.SKIN)
	await driver.player_ties_off()
	await driver.seconds(1.0)
	assert_lt(loose.closure(), 0.99, "tied off loose, the thread leaves the cut open")
	assert_gt(tissue.gap_along(loose.points, 0.03, TissueSim.Depth.SKIN), TissueSim.OPEN_GAP, "and its edges apart")
	assert_false(patient.flags.has("neat_closure"), "a loose closure isn't a neat one")

	await driver.player_threads(tight, TissueSim.Depth.SKIN)
	var thread := needle.suture_thread
	await driver.player_pulls_thread("too tight")
	assert_eq(ToolActions.thread_state(needle), "too tight", "the hand status reads too tight before it tears")
	assert_gt(tight.closure(), 0.99, "too tight still holds the cut closed")
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
	await driver.player_sews(wound, TissueSim.Depth.MUSCLE)
	assert_false(tissue.muscle_open_near(middle, Patient.MUSCLE_REACH), "the muscle thread closes the muscle")
	assert_false(driver.body.is_open(middle), "the sewn muscle closes the way into the belly")
	await driver.player_sews(wound, TissueSim.Depth.FAT)
	assert_false(tissue.fat_open_near(middle, Patient.MUSCLE_REACH), "a thread through the fat closes it")
	assert_eq(tissue.triangles(TissueSim.Depth.FAT).size(), whole_fat, "the fat layer shows no opening")
	await driver.player_sews(wound, TissueSim.Depth.SKIN)
	await driver.seconds(1.0)
	assert_gt(wound.closure(), 0.99, "the skin closes over the sewn layers")
	assert_lt(tissue.gap_along(wound.points, 0.03, TissueSim.Depth.SKIN), TissueSim.OPEN_GAP, "and its edges meet")
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
