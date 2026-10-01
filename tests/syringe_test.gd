extends Node
## Headless syringe test: the wheel works the plunger 1 ml a notch in every case of tests/syringe_bench.gd, and the
## syringe, its target and what the patient got all add up after every notch.
## Run: godot --headless --path . res://tests/syringe_test.tscn [-- --case=vein_pull]

const Bench := preload("res://tests/syringe_bench.gd")

var bench: Bench


func _ready() -> void:
	bench = Bench.new()
	add_child(bench)
	await bench.start()
	await _control_checks()
	var only := ""
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--case="):
			only = arg.get_slice("=", 1)
	for case: Dictionary in Bench.CASES:
		if only.is_empty() or case.name == only:
			await _run(case)
	print("syringe_test: done")
	get_tree().quit()


## Y1-Y3: the wheel moves the plunger instead of setting a level, the hints say so, and the last zoom step frames the
## needle with the hands faded and the printed scale turned to the camera.
func _control_checks() -> void:
	await bench.stage(Bench.CASES[0])
	var me := bench.surgery.local_surgeon
	var hand := me.hands[me.active]
	await bench.notch(true)
	await bench.notch(true)
	await bench.notch(false)
	_check(hand.level == 0 and not hand.lowered, "the wheel works a syringe's plunger, not an effort level, without Use tool held")
	_check(is_equal_approx(bench.syringe.ml, 1.0), "two notches down and one up leave 1 ml in the syringe (%.2f)" % bench.syringe.ml)
	var hints := "\n".join(Hud.control_lines(me))
	_check(hints.contains("Pull plunger 1 ml") and hints.contains("Push plunger 1 ml") and not hints.contains("Wheel  Plunger"), "the controls shown name the plunger on the wheel:\n" + hints)
	var own_twist := hand.twist
	var tip_before := bench.syringe.tip_position()
	me.zoom = Surgeon.ZOOM_FOV.size() - 1
	await bench.frames(40)
	var camera := me.camera()
	# A roll about its length can only face the camera across the barrel, not along it.
	var along := bench.syringe.global_basis.z.normalized()
	var to_camera := (camera.global_position - ToolManager.middle(bench.syringe)).slide(along).normalized()
	var printed := -bench.syringe.global_basis.y.normalized()
	_check(printed.dot(to_camera) > 0.98, "the needle view turns the syringe's printed scale toward the camera (%.2f)" % printed.dot(to_camera))
	_check(bench.syringe.tip_position().distance_to(tip_before) < 0.003, "turning the scale leaves the needle where it was (%.4f m)" % bench.syringe.tip_position().distance_to(tip_before))
	var head := camera.get_parent() as Node3D
	_check(camera.global_position.distance_to(head.global_position) > 0.05, "the last zoom step moves the camera over to the needle")
	for point: Vector3 in [bench.syringe.global_position, bench.syringe.tip_position(), ToolManager.middle(bench.container)]:
		_check(camera.is_position_in_frustum(point), "the needle view shows the syringe and the vial (%s)" % point)
	_check(_fade(hand) > 0.5, "the hands fade in the needle view (%.2f)" % _fade(hand))
	_check(hints != "\n".join(Hud.control_lines(me)), "the controls shown say when the needle view is on")
	me.zoom = 0
	await bench.frames(40)
	_check(camera.transform.is_equal_approx(Transform3D.IDENTITY) and _fade(hand) == 0.0, "zooming out puts the camera back and the hands solid")
	_check(is_equal_approx(hand.twist, own_twist), "zooming out rolls the syringe back the way the hand held it")


## Y4-Y10: one case, checked after every notch and once the needle is out.
func _run(case: Dictionary) -> void:
	print("--- ", case.name)
	await bench.stage(case)
	var syringe := bench.syringe
	var patient := bench.surgery.patient
	var target := bench.needle_target()
	var kind: String = {"vial": "container", "dish": "container", "vein": "vein", "air": "air"}.get(case.target, "tissue")
	if not _check(target.kind == kind and target.get("layer", case.target) == case.target, "%s: the needle is in the %s (%s)" % [case.name, case.target, target]):
		return
	var pull: bool = case.notches > 0
	var moves: bool = not (pull and kind == "tissue")
	var drugs_before := patient.active_drugs.size()
	for i in absi(case.notches):
		var ml_before := syringe.ml
		var air_before := syringe.air
		var red_before := syringe.red
		var outside_before := bench.container.ml if bench.container else 0.0
		var redness_before := _redness(syringe)
		var blood_before := patient.vitals.blood_ml
		await bench.notch(pull)
		var step := 1.0 if moves else 0.0
		var plunger := (syringe.ml + syringe.air) - (ml_before + air_before)
		_check(is_equal_approx(plunger, step if pull else -step), "%s: notch %d moves the plunger %+.0f ml (%+.2f)" % [case.name, i + 1, step if pull else -step, plunger])
		if case.target == "air":
			_check(is_equal_approx(syringe.air - air_before, 1.0) and syringe.ml == ml_before, "%s: pulling in the air draws air, the liquid stays %.0f ml" % [case.name, ml_before])
		elif bench.container:
			_check(is_equal_approx(bench.container.ml - outside_before, -1.0 if pull else 1.0), "%s: the %s's level changes by the same 1 ml" % [case.name, bench.container.def.name])
		if case.target == "vein" and pull:
			_check(syringe.red > red_before and _redness(syringe) > redness_before, "%s: drawing blood turns the liquid redder (%.2f -> %.2f)" % [case.name, red_before, syringe.red])
			# The patient keeps bleeding from the test's cuts meanwhile, a little.
			_check(absf(blood_before - patient.vitals.blood_ml - 1.0) < 0.3, "%s: the patient loses the 1 ml drawn (%.2f)" % [case.name, blood_before - patient.vitals.blood_ml])
		_check_shown(case.name, syringe)
		if bench.container:
			_check_shown(case.name, bench.container)
	var expected: float = case.ml + (case.notches if moves and case.target != "air" else 0.0)
	_check(is_equal_approx(syringe.ml, maxf(expected, 0.0)), "%s: the syringe ends with %.0f ml (%.2f)" % [case.name, expected, syringe.ml])
	if case.target == "vein" and pull:
		_check(is_equal_approx(syringe.red, case.notches / expected), "%s: the liquid is %.0f%% blood (%.2f)" % [case.name, 100.0 * case.notches / expected, syringe.red])
	await bench.withdraw()
	if not pull and kind in ["vein", "tissue"]:
		var given := patient.active_drugs.slice(drugs_before)
		var onset: float = Db.drug(Bench.DRUG).onset * (1.5 if kind == "vein" else 0.4)
		_check(given.size() == 1 and is_equal_approx(given[0].onset, onset), "%s: the drug is given %s once the needle is out" % [case.name, "into the blood" if kind == "vein" else "as a direct injection"])
	elif kind != "air":
		_check(patient.active_drugs.size() == drugs_before, "%s: nothing is given" % case.name)


## The liquid, air and plunger shown match what's in it exactly, against the full Level part (the graduation).
func _check_shown(case_name: String, tool: SurgicalTool) -> void:
	var share := tool.ml / tool.def.volume
	var level := tool.find_child("Level", true, false) as MeshInstance3D
	var pool := tool.find_child("Pool", true, false) as MeshInstance3D
	var part := level if level else pool
	var shown := part.scale.z if level else part.scale.y
	_check(part.visible == (tool.ml > 0.0) and (tool.ml == 0.0 or absf(shown - share) < 0.001), "%s: the %s shows %.1f ml (%.1f)" % [case_name, tool.def.name, tool.ml, shown * tool.def.volume])
	_check(tool.red < 0.5 or _redness(tool) > 0.0, "%s: the %s's liquid, %.0f%% blood, looks red" % [case_name, tool.def.name, tool.red * 100.0])
	if tool.def.action != "syringe":
		return
	var travel := level.get_aabb().size.z
	var plunger := tool.find_child("Plunger", true, false) as Node3D
	_check(absf(plunger.position.z - travel * (tool.ml + tool.air) / tool.def.volume) < 0.0005, "%s: the plunger sits behind %.0f ml of liquid and air" % [case_name, tool.ml + tool.air])
	var air_gap := level.position.z - (level.get_meta("rest") as Vector3).z
	_check(absf(air_gap - travel * tool.air / tool.def.volume) < 0.0005, "%s: the air shows at the needle end" % case_name)


## How much redder than blue the liquid shown is.
static func _redness(tool: SurgicalTool) -> float:
	var part := tool.find_child("Level", true, false) as MeshInstance3D
	var albedo: Color = (part.get_surface_override_material(0) as ShaderMaterial).get_shader_parameter("albedo")
	return albedo.r - albedo.b


static func _fade(hand: SurgeonHand) -> float:
	var glove := hand.find_children("*", "GeometryInstance3D", true, false)[0] as GeometryInstance3D
	var ghost := glove.material_override as StandardMaterial3D
	return 1.0 - ghost.albedo_color.a if ghost else 0.0


func _check(ok: bool, what: String) -> bool:
	print(("ok    " if ok else "FAIL: ") + what)
	return ok
