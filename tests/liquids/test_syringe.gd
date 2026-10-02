extends GutTest
## Headless syringe test: the wheel works the plunger 1 ml a notch in every case of the shared syringe bench, and the
## syringe, its target and what the patient got all add up after every notch.
## The table below is the executable source of truth for all syringe and IV cases.

const TAGS = ["smoke", "liquids", "tool_syringe_3", "tool_syringe_10", "tool_syringe_50", "tool_iv_catheter"]
const Bench := preload("res://tests/support/syringe_bench.gd")

var bench: Bench
## Doses the host gave surgeons: [peer, drug, amount].
var doses: Array = []


func test_syringe_iv_and_plunger_cases() -> void:
	bench = Bench.new()
	add_child(bench)
	await bench.start()
	bench.surgery.surgeon_dosed.connect(func(peer: int, drug: String, amount: float) -> void: doses.append([peer, drug, amount]))
	await _control_checks()
	var only := ""
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--case="):
			only = arg.get_slice("=", 1)
	for case: Dictionary in Bench.CASES:
		if case.name.begins_with(only):
			await _run(case)
	for case: Dictionary in Bench.CATHETER_CASES:
		if case.name.begins_with(only):
			await _catheter_checks(case)
	if "swap_bag".begins_with(only):
		await _swap_checks()
	if "stress".begins_with(only):
		_stress_checks()
	if "sedation".begins_with(only):
		_sedation_checks()
	if "sedated_surgeon".begins_with(only):
		await _sedated_surgeon_checks()
	if "needle_hand".begins_with(only):
		await _hand_checks()


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
	var kind: String = {"vial": "container", "dish": "container", "drip": "container", "vein": "vein", "air": "air"}.get(case.target, "tissue")
	var into_surgeon: bool = case.target in Bench.SURGEON_TARGETS
	var peer := 1 if case.target == "own_hand" else 2
	if into_surgeon:
		var part := "body" if case.target == "doctor_body" else "hand"
		if not _check(target.kind == "surgeon" and target.peer == peer and target.part == part, "%s: the needle is in surgeon %d's %s (%s)" % [case.name, peer, part, target]):
			return
	elif not _check(target.kind == kind and target.get("layer", case.target) == case.target, "%s: the needle is in the %s (%s)" % [case.name, case.target, target]):
		return
	var pull: bool = case.notches > 0
	var moves: bool = not (pull and (kind == "tissue" or into_surgeon))
	var drugs_before := patient.active_drugs.size()
	var me := bench.surgery.local_surgeon
	me.status.drugs.clear()
	doses.clear()
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
	if case.target == "drip" and not pull:
		_check(patient.active_drugs.size() == drugs_before, "%s: nothing runs down the line while the needle is in the bag" % case.name)
	await bench.withdraw()
	if into_surgeon:
		var drug: String = Db.tool(case.vial).drug
		var amount: float = absi(case.notches) * Db.tool(case.vial).concentration
		if pull:
			_check(doses.is_empty() and me.status.drugs.is_empty(), "%s: pulling from a hand draws nothing and gives nothing" % case.name)
		else:
			var given: bool = doses.size() == 1 and doses[0][0] == peer and doses[0][1] == drug and is_equal_approx(doses[0][2], amount)
			_check(given, "%s: once the needle is out surgeon %d gets %.1f %s (%s)" % [case.name, peer, amount, drug, doses])
		if case.target == "own_hand" and not pull:
			var entry: Dictionary = me.status.drugs[0] if me.status.drugs.size() == 1 else {}
			var onset := Db.drug(drug).onset * DrugDef.DIRECT_ONSET
			_check(not entry.is_empty() and is_equal_approx(entry.onset, onset), "%s: the surgeon's own dose takes effect like a direct injection (%s)" % [case.name, entry])
		_check(patient.active_drugs.size() == drugs_before, "%s: the patient gets nothing" % case.name)
		me.status.drugs.clear()
	elif not pull and (kind in ["vein", "tissue"] or case.target == "drip"):
		var given := patient.active_drugs.slice(drugs_before)
		var into_blood: bool = kind == "vein" or case.target == "drip"
		var onset: float = Db.drug(Bench.DRUG).onset * (1.5 if into_blood else 0.4)
		var how := "through the IV line" if case.target == "drip" else "into the blood" if into_blood else "as a direct injection"
		_check(given.size() == 1 and is_equal_approx(given[0].onset, onset), "%s: the drug is given %s once the needle is out" % [case.name, how])
	elif kind != "air":
		_check(patient.active_drugs.size() == drugs_before, "%s: nothing is given" % case.name)


## Y11-Y12: the IV catheter in the needle view goes into the forearm. On the vein the line works; beside it the
## catheter still sticks and the tubing runs to it, but a drug in the IV drip stays in the bag.
func _catheter_checks(case: Dictionary) -> void:
	print("--- ", case.name)
	await bench.stage_catheter(case.miss)
	var me := bench.surgery.local_surgeon
	var patient := bench.surgery.patient
	var hit: bool = case.miss == 0.0
	me.zoom = Surgeon.ZOOM_FOV.size() - 1
	await bench.frames(40)
	var camera := me.camera()
	var moved := camera.global_position.distance_to((camera.get_parent() as Node3D).global_position)
	var framed := moved > 0.05 and camera.is_position_in_frustum(bench.catheter.tip_position())
	_check(framed, "%s: the last zoom step frames the catheter's needle (camera moved %.2f m)" % [case.name, moved])
	# Checked before it goes in: the needle hurts, an awake patient's arm flinches and the vein moves with it.
	var off := (bench.catheter.tip_position() - bench.vein_point()) * Vector3(1, 0, 1)
	_check(off.length() < 0.01 if hit else off.length() > 0.02, "%s: the needle is over %s (%.1f cm across)" % [case.name, "the vein" if hit else "the arm beside the vein", off.length() * 100.0])
	await bench.press()
	var stuck := patient.iv_set and bench.surgery.room.iv_line.is_attached()
	_check(stuck and patient.iv_in_vein == hit, "%s: the catheter sticks with the tubing run to it, %s" % [case.name, "in the vein" if hit else "but outside the vein"])
	var drip := bench.surgery.tools.drip_bag()
	var drugs_before := patient.active_drugs.size()
	bench.surgery.tools.add_liquid(drip, 5.0, {Bench.DRUG: 5.0 * Db.tool(Bench.VIAL).concentration})
	await bench.frames(5)
	var given := patient.active_drugs.size() - drugs_before
	_check(given == (1 if hit else 0) and drip.contents.has(Bench.DRUG) != hit, "%s: a drug in the IV drip %s" % [case.name, "runs into the patient" if hit else "stays in the bag"])
	me.zoom = 0
	await bench.frames(20)


## Y13: the IV stand offers "Swap IV bag" only to a hand holding a bag; swapping hangs a full bag in place of the old
## one and runs it into a working line.
func _swap_checks() -> void:
	print("--- swap_bag")
	var surgery := bench.surgery
	var me := surgery.local_surgeon
	var patient := surgery.patient
	var swap: Interactable = surgery.room.find_children("*", "Interactable", false, false).filter(func(i: Interactable) -> bool: return i.prompt == "Swap IV bag").front()
	await bench.stage_catheter(0.0)
	await bench.press()
	_check(patient.iv_working(), "swap_bag: a working line is in")
	var drip := surgery.tools.drip_bag()
	surgery.tools.add_liquid(drip, -drip.ml)
	_check(not swap.offered_to(me), "swap_bag: an empty hand isn't offered Swap IV bag")
	var bag := bench._spawn("saline_bag", me.global_position + Vector3.UP)
	await bench.frames(2)
	surgery.tools._req_grab(bag.uid, me.active)
	await bench.frames(2)
	_check(swap.offered_to(me), "swap_bag: a hand holding a bag is offered Swap IV bag")
	patient.flags.erase("drug_saline")
	surgery._req_iv(me.active)
	await bench.frames(2)
	var hung := is_equal_approx(drip.ml, SurgicalTool.DRIP_FLUID) and bag.state == SurgicalTool.State.CONSUMED
	_check(hung and patient.flags.has("drug_saline"), "swap_bag: the bag hangs on the stand full (%.0f ml) and runs into the line" % drip.ml)


## Y15-Y16: stress shakes the hands in three steps, quirks only set how low stress drains, and Steady hands stops it
## all. A status on its own, its update() driven directly.
func _stress_checks() -> void:
	print("--- stress")
	var status := SurgeonStatus.new(Modifiers.new())
	var shakes: Array[float] = []
	for stress: float in [0.2, 0.45, 0.8]:
		status.stress = stress
		shakes.append(status.tremor_amount())
	_check(shakes[0] == 0.0 and shakes[1] > 0.0 and shakes[2] > shakes[1] * 2.0, "stress: calm hands keep the tool still, a third stressed shake it lightly, two thirds plainly (%s)" % [shakes])
	status.stress = 0.2
	var twitches := 0
	for i in 600:
		status.update(1.0 / 60.0, {})
		twitches += 1 if status.shiver() > 0.0 else 0
	_check(twitches > 0 and twitches < 500, "stress: calm, the glove still twitches now and then (%d of 600 frames)" % twitches)
	var shaky := _status({"stress_floor": 0.65})
	shaky.update(0.1, {})
	_check(is_equal_approx(shaky.stress, 0.65) and shaky.tremor_amount() > shakes[1], "stress: Shaky hands never drain below 65%% and shake plainly (%.2f)" % shaky.stress)
	var steady := _status({"stress_floor": 0.65, "tremor_mult": 0.0})
	steady.cold_tremor = 0.0015
	steady.stress = 0.95
	_check(steady.tremor_amount() == 0.0 and steady.shiver() == 0.0, "stress: Steady hands with Shaky hands, stressed and cold, don't shake at all")
	_check(SurgeonStatus.weight_of(Modifiers.new()) == 80.0 and _status({"weight_kg": -20.0}).weight_kg == 60.0, "stress: a surgeon weighs 80 kg, small hands 60 kg")


## Y17-Y18: diazepam given to a surgeon. The right dose for their weight stops stress shaking (not the cold) and
## delays hand moves; past 1.4 times the dose the view darkens and the delay grows; twice the dose knocks them out for five
## minutes. Flumazenil brings them round at once; adrenaline only while it lasts.
func _sedation_checks() -> void:
	print("--- sedation")
	var right := 0.2 * 80.0
	var status := _dosed(right)
	status.stress = 0.8
	status.cold_tremor = 0.0015
	# Ten seconds in, the dose has faded a little from its peak.
	_check(status.calm > 0.95 and absf(status.tremor_amount() - 0.0015) < 0.0003, "sedation: the right dose steadies stress shaking, the cold stays (%.4f)" % status.tremor_amount())
	_check(absf(status.input_delay() - SurgeonStatus.SEDATED_DELAY) < 0.005 and status.overdose == 0.0 and not status.is_out(), "sedation: the right dose delays hand moves %.0f ms, nothing more" % (status.input_delay() * 1000.0))
	var more := _dosed(right * 1.8)
	_check(more.overdose > 0.4 and more.input_delay() > SurgeonStatus.SEDATED_DELAY + 0.05 and not more.is_out(), "sedation: 1.8 doses darken the view and lag more (%.2f, %.0f ms)" % [more.overdose, more.input_delay() * 1000.0])
	var events := PackedStringArray()
	var out := _dosed(right * 2.5, events)
	_check(events.has("knocked_out") and out.is_out() and out.knocked_out > SurgeonStatus.KNOCKOUT_TIME - 10.0, "sedation: 2.5 doses knock the surgeon out for five minutes (%.0f s left)" % out.knocked_out)
	_run_status(out, 60.0, events)
	out.administer("flumazenil", 0.01 * 80.0)
	events.clear()
	_run_status(out, 2.0, events)
	_check(events.has("came_round") and not out.is_out() and out.calm == 0.0, "sedation: flumazenil brings them round and ends the diazepam (%s)" % events)
	var kept := _dosed(right * 3.5)
	kept.administer("adrenaline", 0.01 * 80.0)
	events.clear()
	_run_status(kept, 3.0, events)
	_check(events.has("came_round") and not kept.is_out() and kept.calm > 0.9, "sedation: adrenaline gets them up, still sedated")
	_run_status(kept, Db.drug("adrenaline").duration, events)
	_check(kept.is_out(), "sedation: once the adrenaline wears off, 3.5 doses put them down again")


## Y19: the surgeon in the room, sedated: afterimages trail the gloves, mouse moves reach the hand late; knocked out
## they lie on the floor with the table in view, and flumazenil gets them up again.
func _sedated_surgeon_checks() -> void:
	print("--- sedated_surgeon")
	var me := bench.surgery.local_surgeon
	me.status.drugs.clear()
	me.status.administer("diazepam", 0.2 * 80.0)
	await bench.frames(200)
	var hand := me.hands[me.active]
	var trail := hand.find_child("Trail", false, false)
	_check(me.status.calm > 0.9 and trail != null and trail.get_child_count() == SurgeonHand.TRAIL_COPIES, "sedated_surgeon: afterimages follow the gloves")
	var before := hand.target
	me._delayed.append([Time.get_ticks_msec() + 100, me.active, Vector2(40.0, 0.0)])
	await bench.frames(2)
	var early := hand.target.distance_to(before)
	await get_tree().create_timer(0.2).timeout
	await bench.frames(2)
	_check(early < 0.001 and hand.target.distance_to(before) > 0.01, "sedated_surgeon: a mouse move reaches the hand only after the delay (%.3f m, then %.3f m)" % [early, hand.target.distance_to(before)])
	me.status.administer("diazepam", 0.2 * 80.0 * 2.0)
	await bench.frames(240)
	var camera := me.camera()
	var patient := bench.surgery.patient
	_check(me.status.is_knocked_out() and me.is_down() and camera.global_position.y - me.global_position.y < 0.4, "sedated_surgeon: knocked out, the surgeon lies on the floor (eyes %.2f m up)" % (camera.global_position.y - me.global_position.y))
	_check(camera.is_position_in_frustum(patient.global_position), "sedated_surgeon: lying there, the patient on the table is in view")
	_check(me.hands.all(func(h: SurgeonHand) -> bool: return h.global_position.y - me.global_position.y < 0.15), "sedated_surgeon: the hands lie on the floor")
	me.status.administer("flumazenil", 0.01 * 80.0)
	await bench.frames(200)
	_check(not me.status.is_out() and not me.is_down() and camera.global_position.y - me.global_position.y > 1.4, "sedated_surgeon: flumazenil gets them back on their feet")


func _status(effects: Dictionary) -> SurgeonStatus:
	var mods := Modifiers.new()
	mods.add(effects)
	return SurgeonStatus.new(mods)


## A status given `mg` of diazepam, run 10 seconds on (past its onset), the events it gave collected.
func _dosed(mg: float, events: PackedStringArray = PackedStringArray()) -> SurgeonStatus:
	var status := SurgeonStatus.new(Modifiers.new())
	status.administer("diazepam", mg)
	_run_status(status, 10.0, events)
	return status


func _run_status(status: SurgeonStatus, seconds: float, events: PackedStringArray) -> void:
	for i in int(seconds * 10.0):
		events.append_array(status.update(0.1, {}))


## Y20-Y22: in the needle view the mouse moves the hand as seen on screen; a needle pressed into the skin keeps its
## tip in place, the mouse tilting the syringe about it, and tears out when pulled on sideways; a syringe brought under the IV bag at waist height rises into its port.
func _hand_checks() -> void:
	print("--- needle_hand")
	var me := bench.surgery.local_surgeon
	var hand := me.hands[me.active]
	await bench.stage(Bench.CASES[0])
	me.zoom = Surgeon.ZOOM_FOV.size() - 1
	await bench.frames(40)
	var right := (me.camera().global_basis.x * Vector3(1, 0, 1)).normalized()
	var away := (me.camera().global_basis.y * Vector3(1, 0, 1)).normalized()
	for motion: Vector2 in [Vector2(20, 0), Vector2(0, -20)]:
		var before := hand.target
		for i in 5:
			me.steer_hand(motion)
			await bench.frames(1)
		var moved := (hand.target - before) * Vector3(1, 0, 1)
		var along := moved.dot(right if motion.x > 0.0 else away)
		# The camera follows the syringe, so it turns a little as the hand moves.
		_check(along > 0.8 * moved.length() and along > 0.01, "needle_hand: in the needle view the mouse %s moves the hand %s on screen (%.3f m of %.3f)" % ["right" if motion.x > 0.0 else "up", "right" if motion.x > 0.0 else "away", along, moved.length()])
	me.zoom = 0
	await bench.frames(40)
	await bench.stage(Bench.CASES[6])
	var tip := bench.syringe.tip_position()
	var tilt := hand.tilt
	var grip := hand.global_position
	for i in 5:
		me.steer_hand(Vector2(0, 10))
		await bench.frames(1)
	await bench.frames(5)
	var drift := bench.syringe.tip_position().distance_to(tip)
	_check(drift < 0.001 and not is_equal_approx(hand.tilt, tilt) and hand.global_position.distance_to(grip) > 0.005, "needle_hand: a needle in the skin keeps its tip in place (%.4f m) and the mouse tilts the syringe about it (tilt %.2f -> %.2f)" % [drift, tilt, hand.tilt])
	for i in 60:
		if me._needle_torn:
			break
		me.steer_hand(Vector2(20, 0))
		await bench.frames(1)
	var body := bench.surgery.patient.body
	var scratch := body.wound_map.value(WoundMap.Layer.WOUNDS, WoundMap.CUT, body.world_to_uv(bench.syringe.tip_position()))
	_check(me._needle_torn and scratch > 0.0, "needle_hand: pulled on, the needle tears out and leaves a scratch (%.2f)" % scratch)
	await bench.withdraw()
	await bench.stage(Bench.CASES[13])
	var bag := bench.surgery.tools.drip_bag()
	hand.local_target = me.to_local(me.global_position + me.global_basis * Vector3(0.1, 1.0, -0.2))
	await bench.frames(20)
	var under := bag.tip_position() * Vector3(1, 0, 1) - hand.tip_offset(bench.syringe.def.length) * Vector3(1, 0, 1)
	for i in 30:
		hand.local_target = me.to_local(hand.target.lerp(under + Vector3.UP * hand.target.y, 0.1))
		await bench.frames(1)
	var target := bench.needle_target()
	_check(target.get("container") == bag, "needle_hand: a syringe brought under the IV bag from waist height goes into it (hand at %.2f m, %s)" % [hand.global_position.y, target.kind])


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
	assert_true(ok, what)
	return ok
