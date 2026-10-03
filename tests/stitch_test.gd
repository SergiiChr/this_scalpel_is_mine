extends Node
## Interaction-level running-suture checks. The screenshot suite supplies the
## visual matrix; this scene verifies click, wheel and long-click semantics.

const SURGERY := preload("res://scenes/surgery.tscn")


func _ready() -> void:
	Net.leave()
	Net.scenario_id = "hand_stitch"
	Net.session_seed = 42
	Net.roster = {1: {"name": "Tester", "quirks": [{"id": "normal_dude", "variant": ""}], "ready": true}}
	Net.patient_quirks = []
	Net.run_modifiers = []
	var surgery: Surgery = SURGERY.instantiate()
	add_child(surgery)
	await _frames(30)
	var needle: SurgicalTool = null
	for tool: SurgicalTool in surgery.tools.tools.values():
		if tool.def.id == "needle":
			needle = tool
			break
	if needle == null:
		# Keep the interaction test independent from scenario tool placement; the
		# smoke suite separately verifies each scenario's tray contents.
		needle = SurgicalTool.new()
		add_child(needle)
		needle.setup(9999, Db.tool("needle"))
	var patient := surgery.patient
	var tip := patient.body.uv_to_world(Vector2(0.5, 0.5))
	_click(needle, patient, Vector2(0.36, 0.44), tip)
	var id := needle.suture_thread
	_check(id != 0, "the first click starts a live thread")
	_check(patient.body.tissue.thread_uvs(id).size() == 1, "the first click creates exactly one anchor")
	_click(needle, patient, Vector2(0.44, 0.56), tip)
	_check(patient.body.tissue.thread_uvs(id).size() == 2, "the second click connects the anchor to the new hole")
	_check(patient.wounds[0].closure() < 0.9, "a loose running thread does not close the wound")
	var info := patient.body.tissue.thread_info(id)
	var spring: int = info.springs[0]
	var rest_before: float = patient.body.tissue.c_rest[spring]
	ToolActions.adjust_suture_tension(needle, -1, patient)
	_check(needle.suture_tension < 1.15 and patient.body.tissue.c_rest[spring] < rest_before, "wheel down tightens the whole live thread")
	ToolActions.adjust_suture_tension(needle, 1, patient)
	_check(is_equal_approx(needle.suture_tension, 1.15), "wheel up loosens the thread")
	# Holding at a distinct point reaches the cut threshold, places that point,
	# and leaves the routed thread in the tissue at its present tension.
	var final_uv := Vector2(0.52, 0.44)
	ToolActions._update_running_suture(needle, patient, "site", final_uv, true, true, false, 0.1, tip)
	for i in 6:
		ToolActions._update_running_suture(needle, patient, "site", final_uv, true, false, false, 0.1, tip)
	_check(needle.suture_thread == 0, "a long click cuts the thread")
	_check(patient.body.tissue.thread_uvs(id).size() == 3, "the long click creates the final anchor")
	_check(patient.body.tissue.thread_info(id).final, "the cut thread remains finalized in the patient")
	_check(is_equal_approx(float(patient.body.tissue.thread_info(id).tension), 1.15), "cutting preserves its tension")
	var closure_id := 9900
	for i in 5:
		patient.place_suture_anchor(closure_id, Vector2(lerpf(0.30, 0.72, float(i) / 4.0), 0.44 if i % 2 == 0 else 0.56), TissueSim.Depth.SKIN, 1.15)
	patient.set_suture_tension(closure_id, 1.08)
	var safe_lip := _highest_lip(patient.body.tissue)
	patient.set_suture_tension(closure_id, 0.90)
	_check(_highest_lip(patient.body.tissue) > safe_lip, "more tension raises a larger wound-edge lip")
	patient.set_suture_tension(closure_id, 1.08)
	patient.finish_suture(closure_id)
	await _frames(40)
	_check(patient.wounds[0].closure() >= 0.99, "safe final tension closes every supported wound bin")
	_check(patient.body.tissue.gap_along(patient.wounds[0].points, 0.03, TissueSim.Depth.SKIN) < TissueSim.OPEN_GAP, "the finalized wound edges physically meet")
	var visible_cut_edges := 0
	for s: int in patient.body.tissue.severed():
		if patient.body.tissue.cut_depth(s) >= TissueSim.Depth.SKIN:
			visible_cut_edges += 1
	_check(visible_cut_edges == 0, "a fully supported final seam has no split wall edges (%d remain)" % visible_cut_edges)
	var highest_lip := _highest_lip(patient.body.tissue)
	_check(highest_lip >= 0.00075, "closed edges form a raised tension lip instead of clipping through each other")
	var tear_id := 9901
	patient.place_suture_anchor(tear_id, Vector2(0.56, 0.44), TissueSim.Depth.SKIN, 1.15)
	patient.place_suture_anchor(tear_id, Vector2(0.64, 0.56), TissueSim.Depth.SKIN, 1.15)
	patient.set_suture_tension(tear_id, 0.60)
	_check(patient._thread_torn.has(tear_id), "over-tightening a skin suture tears through the tissue")
	surgery.queue_free()
	await _frames(6)
	await _deep_layers()
	print("stitch_test: done")
	get_tree().quit()


func _click(tool: SurgicalTool, patient: Patient, uv: Vector2, tip: Vector3) -> void:
	ToolActions._update_running_suture(tool, patient, "site", uv, true, true, false, 0.05, tip)
	ToolActions._update_running_suture(tool, patient, "site", uv, false, false, true, 0.0, tip)


func _check(ok: bool, what: String) -> void:
	if not ok:
		print("FAIL: ", what)


func _highest_lip(tissue: TissueSim) -> float:
	var highest := 0.0
	for amount: float in tissue.suture_lip:
		highest = maxf(highest, amount)
	return highest


## A pre-cut abdomen exercises the two deeper closure layers. Fat can be
## approximated with deep absorbable bites; muscle takes more tension and must
## close the cavity before a skin closure is attempted.
func _deep_layers() -> void:
	Net.leave()
	Net.scenario_id = "appendectomy"
	Net.session_seed = 43
	Net.roster = {1: {"name": "Tester", "quirks": [{"id": "normal_dude", "variant": ""}], "ready": true}}
	Net.patient_quirks = []
	Net.run_modifiers = []
	var surgery: Surgery = SURGERY.instantiate()
	add_child(surgery)
	await _frames(20)
	var patient := surgery.patient
	patient.cut(9101, Vector2(0.25, 0.28), Vector2(0.75, 0.28), 1.0, 1.0, false, 0.1)
	patient.cut(9102, Vector2(0.30, 0.76), Vector2(0.70, 0.76), 0.6, 1.0, false, 0.1)
	await _frames(40)
	var fat_gap := patient.body.tissue.gap_at(Vector2(0.5, 0.76), 0.04, TissueSim.Depth.FAT)
	for spec: Dictionary in [
		{"id": 9201, "layer": TissueSim.Depth.MUSCLE, "v": 0.28, "a": 0.25, "b": 0.75, "spans": 10, "tension": 0.86},
		{"id": 9202, "layer": TissueSim.Depth.FAT, "v": 0.76, "a": 0.30, "b": 0.70, "spans": 6, "tension": 0.98},
	]:
		for i in int(spec.spans) + 1:
			var u := lerpf(float(spec.a), float(spec.b), float(i) / float(spec.spans))
			patient.place_suture_anchor(int(spec.id), Vector2(u, float(spec.v) + (-0.045 if i % 2 == 0 else 0.045)), int(spec.layer), 1.15)
		patient.set_suture_tension(int(spec.id), float(spec.tension))
		patient.finish_suture(int(spec.id))
	await _frames(80)
	_check(not patient.body.tissue.muscle_open_near(Vector2(0.5, 0.28), Patient.MUSCLE_REACH), "the running muscle suture closes the stomach muscle cavity")
	_check(not patient.body.tissue.is_open(Vector2(0.5, 0.28)), "the closed stomach muscle no longer opens into the cavity")
	_check(not patient.body.tissue.fat_open_near(Vector2(0.5, 0.76), Patient.MUSCLE_REACH), "a tightened deep suture closes the abdominal fat layer")
	_check(patient.body.tissue.gap_at(Vector2(0.5, 0.76), 0.04, TissueSim.Depth.FAT) < fat_gap, "closed abdominal fat no longer leaves a deep gap")
	surgery.queue_free()
	await _frames(5)


func _frames(count: int) -> void:
	for i in count:
		await get_tree().process_frame
