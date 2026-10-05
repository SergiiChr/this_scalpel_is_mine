extends GutTest
## A normal injection ends on Use tool release, before moving away: one bead at the puncture, no tear or extra pain.
## Check all syringe sizes on skin and a vein; key frames show the ready needle, injection, release and moved-away bead.

const TAGS = ["smoke", "liquids", "tool_syringe_3", "tool_syringe_10", "tool_syringe_50", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Bench := preload("res://tests/support/syringe_bench.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")
const FrameBudget := preload("res://tests/support/frame_budget.gd")
const KEY_FRAMES := "res://build/test-artifacts/screenshots/syringe_withdrawal"

var budget := FrameBudget.new()
var measuring := false
var during := ""


func _process(_delta: float) -> void:
	if measuring:
		budget.sample(during)


func test_release_withdraws_without_trauma_and_leaves_blood_at_the_puncture() -> void:
	seed(42)
	var bench := Bench.new()
	add_child(bench)
	await bench.start(false, false)
	var surgery := bench.surgery
	var patient := surgery.patient
	var body := patient.body
	var me := surgery.local_surgeon
	var shots: KeyFrames
	RenderingServer.render_loop_enabled = false
	if KeyFrames.wanted():
		shots = KeyFrames.new()
		add_child(shots)
		shots.begin(surgery, KEY_FRAMES)
	for target: String in ["skin", "vein"]:
		for size: String in ["syringe_10", "syringe_3", "syringe_50"]:
			var label := "%s_%s" % [target, size]
			var capture := shots != null and size == "syringe_10"
			await bench.stage({"target": target, "syringe": size, "ml": 1.0}, false)
			var before := body.find_children("BloodBead*", "MeshInstance3D", true, false)
			var ready := bench.syringe.tip_position()
			if capture:
				assert_true(await shots.capture_at(label + "_ready", ready, 0.12), "saved untouched injection site")
			await bench.press()
			await bench.frames(5)
			assert_ne(me._needle_anchor, Vector3.INF, label + ": the needle is inserted")
			var entered := me._needle_anchor
			await bench.notch(false)
			assert_eq(bench.syringe.ml, 0.0, label + ": the wheel injects the dose")
			assert_eq(body.find_children("BloodBead*", "MeshInstance3D", true, false).size(), before.size(), label + ": blood does not appear while the needle is in")
			if capture:
				assert_true(await shots.capture_view(label + "_injected"), "saved needle contact during injection")
			var pain := patient.vitals.pain
			var wounds := body.wound_map._data[WoundMap.Layer.WOUNDS].duplicate()
			# Capturing the injected view advances breathing. Measure the puncture on the current skin immediately
			# before release, rather than retaining a world-space height from before the screenshot.
			var probe := body.probe(entered)
			var surface := me._surface_below(entered)
			var puncture := body.uv_to_world(probe.uv) if probe.zone == "site" else Vector3(entered.x, surface.y, entered.z)
			budget.resume()
			budget.sample(label + ": releasing Use tool")
			await bench.release(0)
			budget.sample(label + ": creating the withdrawal bead")
			assert_eq(me._needle_anchor, Vector3.INF, label + ": release frees the needle before another mouse move")
			var after := body.find_children("BloodBead*", "MeshInstance3D", true, false)
			assert_eq(after.size(), before.size() + 1, label + ": release immediately creates one bead")
			var added := after.filter(func(node: Node) -> bool: return not before.has(node))
			if added.size() != 1:
				continue
			var bead := added[0] as MeshInstance3D
			assert_lt(bead.global_position.distance_to(puncture), 0.0005, label + ": the bead sits exactly on the skin at the injection point")
			assert_eq(patient.vitals.pain, pain, label + ": withdrawal adds no pain")
			assert_true(body.wound_map._data[WoundMap.Layer.WOUNDS] == wounds, label + ": withdrawal adds no scratch")
			# physics_frame signals precede node updates; process_frame follows the first completed physics step.
			budget.resume()
			budget.sample(label + ": first withdrawal frame")
			await get_tree().process_frame
			budget.sample(label + ": needle withdrawn")
			assert_gt(bench.syringe.tip_position().y, puncture.y, label + ": the needle clears the skin in the first release frame, without moving away")
			assert_false(me._needle_torn, label + ": releasing does not trigger mishandling")
			if capture:
				assert_true(await shots.capture_at(label + "_released", bead.global_position, 0.12), "saved immediate withdrawal bead from above and obliquely")
			var stuck := bead.position
			budget.resume()
			measuring = true
			for frame in 15:
				during = label + ": moving the released needle away, frame %d" % frame
				me.steer_hand(Vector2(20, 0))
				await bench.frames(1)
			measuring = false
			assert_false(me._needle_torn, label + ": moving away after release cannot tear the skin")
			assert_eq(body.find_children("BloodBead*", "MeshInstance3D", true, false).size(), after.size(), label + ": moving away does not create another bead")
			assert_true(body.wound_map._data[WoundMap.Layer.WOUNDS] == wounds, label + ": moving away adds no scratch")
			assert_eq(bead.position, stuck, label + ": the bead stays attached to the puncture")
			if capture:
				assert_true(await shots.capture_at(label + "_moved_away", bead.global_position, 0.12), "saved persistent bead at the injection site")
	print("Syringe withdrawal: " + budget.summary())
	if shots and FrameBudget.enforced():
		assert_true(budget.within(), budget.summary())
	if shots:
		shots.end()
		shots.queue_free()
	bench.queue_free()
	await get_tree().process_frame
	Net.leave()
	RenderingServer.render_loop_enabled = true
