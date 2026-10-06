extends GutTest
## Headless syringe test: the wheel works the plunger 1 ml a notch in every case of the shared syringe bench, and the
## syringe, its target and what the patient got all add up after every notch.
## The table below is the executable source of truth for all syringe and IV cases.

const TAGS = ["slow", "smoke", "liquids", "tool_syringe_3", "tool_syringe_10", "tool_syringe_50", "tool_iv_catheter"]
const Bench := preload("res://tests/support/syringe_bench.gd")

var bench: Bench
## Doses the host gave surgeons: [peer, drug, amount].
var doses: Array = []
## Messages shown on screen since the case started (debug mode is on).
var toasts: Array[String] = []
## Where debug mode says each case's syringe pushed its liquid.
const PUSHED_INTO: Dictionary = {
	"vial": "the vial", "dish": "the kidney dish", "drip": "the IV bag", "vein": "the vein", "skin": "the skin",
	"fat": "the fat", "muscle": "the muscle", "own_hand": "Tester's hand", "doctor_hand": "Partner's hand",
	"doctor_body": "Partner's body", "doctor_down": "Partner's hand",
}


func test_syringe_iv_and_plunger_cases() -> void:
	bench = Bench.new()
	add_child(bench)
	await bench.start()
	bench.surgery.surgeon_dosed.connect(func(peer: int, drug: String, amount: float) -> void: doses.append([peer, drug, amount]))
	var debug := Settings.debug
	Settings.debug = true
	bench.surgery.hud._toasts.child_entered_tree.connect(func(toast: Node) -> void: toasts.append((toast as Label).text))
	await _control_checks()
	await _thumb_checks()
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
	Settings.debug = debug


## Y1-Y3: a syringe picked up sits with its printed scale toward the eyes, on the inner side of the hand. The wheel
## moves the plunger instead of setting a level, and the hints say so. The last zoom step fades the hands and leaves the
## camera at the eyes; once the needle is in with Use tool held, it frames the needle with the printed scale turned to
## the camera.
func _control_checks() -> void:
	# Held out over the floor: over a vial it would stand upright in it.
	await bench.stage(Bench.CASES[12])
	var me := bench.surgery.local_surgeon
	var hand := me.hands[me.active]
	var camera := me.camera()
	var along := bench.syringe.global_basis.z.normalized()
	var printed := -bench.syringe.global_basis.y.normalized()
	# A roll about its length can only face the eyes across the barrel, not along it.
	var to_eyes := (camera.global_position - ToolManager.middle(bench.syringe)).slide(along).normalized()
	var inward := me.global_basis.x * (-1.0 if me.active == 1 else 1.0)
	_check(printed.dot(to_eyes) > 0.9 and printed.dot(inward) > 0.0, "a syringe picked up shows its printed scale to the eyes, on the inner side of the hand (%.2f, %.2f)" % [printed.dot(to_eyes), printed.dot(inward)])
	await bench.stage(Bench.CASES[0])
	await bench.notch(true)
	await bench.notch(true)
	await bench.notch(false)
	_check(hand.level == 0 and not hand.lowered, "the wheel works a syringe's plunger, not an effort level, without Use tool held")
	_check(is_equal_approx(bench.syringe.ml, 1.0), "two notches down and one up leave 1 ml in the syringe (%.2f)" % bench.syringe.ml)
	var hints := "\n".join(Hud.control_lines(me))
	_check(hints.contains("Pull plunger 1 ml") and hints.contains("Push plunger 1 ml") and not hints.contains("Wheel  Plunger"), "the controls shown name the plunger on the wheel:\n" + hints)
	_check(not hints.contains("Rotate"), "the controls shown leave out rolling a syringe, which turns to the eyes on its own:\n" + hints)
	me.zoom = Surgeon.ZOOM_FOV.size() - 1
	await bench.frames(40)
	_check(camera.transform.is_equal_approx(Transform3D.IDENTITY) and _fade(hand) > 0.5, "the last zoom step fades the hands and leaves the camera at the eyes (%.2f)" % _fade(hand))
	_check(hints != "\n".join(Hud.control_lines(me)), "the controls shown say when the hands are see-through")
	var tip_before := bench.syringe.tip_position()
	await bench.press()
	await bench.frames(35)
	along = bench.syringe.global_basis.z.normalized()
	printed = -bench.syringe.global_basis.y.normalized()
	var to_camera := (camera.global_position - ToolManager.middle(bench.syringe)).slide(along).normalized()
	_check(printed.dot(to_camera) > 0.98, "with the needle in, the needle view turns the syringe's printed scale toward the camera (%.2f)" % printed.dot(to_camera))
	_check(bench.syringe.tip_position().distance_to(tip_before) < 0.01, "turning the scale leaves the needle in the vial (%.4f m)" % bench.syringe.tip_position().distance_to(tip_before))
	var head := camera.get_parent() as Node3D
	_check(camera.global_position.distance_to(head.global_position) > 0.05, "with the needle in, the camera moves over to it")
	for point: Vector3 in [bench.syringe.global_position, bench.syringe.tip_position(), ToolManager.middle(bench.container)]:
		_check(camera.is_position_in_frustum(point), "the needle view shows the syringe and the vial (%s)" % point)
	await bench.release()
	await bench.frames(40)
	_check(camera.transform.is_equal_approx(Transform3D.IDENTITY) and _fade(hand) > 0.5, "Use tool let go, the camera goes back to the eyes and the hands stay see-through")
	along = bench.syringe.global_basis.z.normalized()
	printed = -bench.syringe.global_basis.y.normalized()
	to_eyes = (head.global_position - ToolManager.middle(bench.syringe)).slide(along).normalized()
	_check(printed.dot(to_eyes) > 0.9, "Use tool let go, the syringe rolls its scale back toward the eyes (%.2f)" % printed.dot(to_eyes))
	me.zoom = 0
	await bench.frames(40)
	_check(_fade(hand) == 0.0, "zooming out makes the hands solid")


## Y3b: the syringe is held with the thumb on the plunger: drawing 8 ml pulls the plunger out and the thumb goes back
## with it, along the syringe as far as the plunger went, staying on its press.
func _thumb_checks() -> void:
	await bench.stage(Bench.CASES[0])
	var me := bench.surgery.local_surgeon
	var hand := me.hands[me.active]
	var behind: Array[float] = []
	var gaps: Array[float] = []
	for pull in [0, 8]:
		for i in pull:
			await bench.notch(true)
		await bench.frames(5)
		# The thumb's pad: its last bone, posed, carried on to the tip.
		var skeleton := hand._glove_rig.skeleton
		var last := skeleton.find_bone("Thumb3")
		var posed := skeleton.get_bone_global_pose(last)
		var to_tip: Vector3 = hand._thumb_rest().bones[2]
		var pad := skeleton.global_transform * (posed.origin + posed.basis * skeleton.get_bone_global_rest(last).basis.inverse() * to_tip)
		# Along the syringe (+Z is back, toward the plunger), as the syringe holds it.
		behind.append((bench.syringe.global_transform.affine_inverse() * pad).z)
		gaps.append(pad.distance_to(bench.syringe.global_transform * Vector3(0.0, 0.0, hand.press + SurgeonHand.PRESS_PAD)))
	var travel := bench.syringe.def.length * Surgeon.SYRINGE_TRAVEL * 0.8
	var moved := behind[1] - behind[0]
	_check(moved > 0.8 * travel and moved < 1.2 * travel, "drawing 8 ml, the thumb goes back with the plunger (%.1f cm of %.1f)" % [moved * 100.0, travel * 100.0])
	_check(gaps.all(func(gap: float) -> bool: return gap < 0.01), "the thumb stays on the plunger's press (%.1f and %.1f cm from it)" % [gaps[0] * 100.0, gaps[1] * 100.0])


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
	var drug: String = Db.tool(case.get("vial", Bench.VIAL)).drug
	var given_before := _in_body(patient.drugs, drug)
	var me := bench.surgery.local_surgeon
	me.status.drugs = DrugLevels.new()
	doses.clear()
	toasts.clear()
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
			# A drug pushed into the IV bag starts down the line at once: the bag keeps what hasn't run yet.
			var ran := ToolActions.DRIP_RATE * 3.0 / Engine.physics_ticks_per_second if case.target == "drip" and not pull else 0.0
			var change := bench.container.ml - outside_before
			_check(absf(change - (-1.0 if pull else 1.0)) <= ran + 0.0001, "%s: the %s's level changes by the same 1 ml, less what ran down the line (%+.3f)" % [case.name, bench.container.def.name, change])
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
		_check(_in_body(patient.drugs, drug) > given_before, "%s: the drug starts down the line as it's pushed into the bag, the needle still in" % case.name)
	var pushed: float = case.ml - syringe.ml
	await bench.withdraw()
	if case.target == "drip" and not pull:
		_check(bench.container.bolus > 0.0, "%s: the drug runs down the line over time, not all at once" % case.name)
		await bench.frames(ceili(pushed / ToolActions.DRIP_RATE * Engine.physics_ticks_per_second) + 10)
		var told := toasts.filter(func(toast: String) -> bool: return toast.contains("reached the patient over IV"))
		var ticks: Array[String] = []
		for ml in roundi(pushed):
			ticks.append("[debug] 1.0 ml of %s reached the patient over IV (%s ml total)" % [Db.drug(Bench.DRUG).name, String.num(ml + 1.0, 1)])
		_check(told == ticks, "%s: debug mode tells each of the %.0f ml as it reaches the patient over IV, with the total so far (%s)" % [case.name, pushed, told])
	if not pull and kind != "air":
		var named := "[debug] Injected %s ml of %s into %s" % [String.num(pushed, 1), Db.drug(Db.tool(case.get("vial", Bench.VIAL)).drug).name, PUSHED_INTO[case.target]]
		_check(named in toasts, "%s: debug mode says what went where once the needle is out: %s (%s)" % [case.name, named, toasts])
		_check(("[debug] Hit the vein" in toasts) == (kind == "vein"), "%s: debug mode says the needle hit the vein only for the vein (%s)" % [case.name, toasts])
	if into_surgeon:
		var amount: float = absi(case.notches) * Db.tool(case.vial).concentration
		if pull:
			_check(doses.is_empty() and me.status.drugs.entries.is_empty(), "%s: pulling from a hand draws nothing and gives nothing" % case.name)
		else:
			var total: float = doses.reduce(func(sum: float, dose: Array) -> float: return sum + (dose[2] if dose[0] == peer and dose[1] == drug else 0.0), 0.0)
			var given: bool = is_equal_approx(total, amount) and doses.size() == absi(case.notches)
			_check(given, "%s: surgeon %d gets %.1f %s, some with every push (%s)" % [case.name, peer, amount, drug, doses])
		if case.target == "own_hand" and not pull:
			var entry: Dictionary = me.status.drugs.entries.get(drug, {})
			var onset := Db.drug(drug).onset * DrugDef.DIRECT_ONSET
			_check(not entry.is_empty() and is_equal_approx(entry.onset, onset), "%s: the surgeon's own dose takes effect like a direct injection (%s)" % [case.name, entry])
		_check(_in_body(patient.drugs, drug) <= given_before + 0.0001, "%s: the patient gets nothing" % case.name)
		me.status.drugs = DrugLevels.new()
	elif not pull and (kind in ["vein", "tissue"] or case.target == "drip"):
		var def := Db.drug(drug)
		var share := pushed * Db.tool(Bench.VIAL).concentration / (def.dose * patient.weight_kg)
		var given := _in_body(patient.drugs, drug) - given_before
		var into_blood: bool = kind == "vein" or case.target == "drip"
		var onset: float = def.onset * (1.5 if into_blood else DrugDef.DIRECT_ONSET)
		var entry: Dictionary = patient.drugs.entries.get(drug, {})
		var how := "through the IV line" if case.target == "drip" else "into the blood" if into_blood else "as a direct injection"
		_check(absf(given - share) < share * 0.05 and is_equal_approx(entry.get("onset", 0.0), onset), "%s: all %.0f ml are given %s (%.3f of %.3f doses)" % [case.name, pushed, how, given, share])
	elif kind != "air":
		_check(_in_body(patient.drugs, drug) <= given_before + 0.0001, "%s: nothing is given" % case.name)


## Y11-Y12: the IV catheter zoomed in on goes into the forearm. On the vein the line works; beside it the
## catheter still sticks and the tubing runs to it, but a drug in the IV drip stays in the bag.
func _catheter_checks(case: Dictionary) -> void:
	print("--- ", case.name)
	await bench.stage_catheter(case.miss)
	var me := bench.surgery.local_surgeon
	var patient := bench.surgery.patient
	var hit: bool = case.miss == 0.0
	me.zoom = Surgeon.ZOOM_FOV.size() - 1
	await bench.frames(40)
	var hand := me.hands[me.active]
	_check(me.camera().transform.is_equal_approx(Transform3D.IDENTITY) and _fade(hand) > 0.5, "%s: the last zoom step fades the hands over the catheter, the camera at the eyes (%.2f)" % [case.name, _fade(hand)])
	# Checked before it goes in: the needle hurts, an awake patient's arm flinches and the vein moves with it.
	var off := (bench.catheter.tip_position() - bench.vein_point()) * Vector3(1, 0, 1)
	_check(off.length() < 0.01 if hit else off.length() > 0.02, "%s: the needle is over %s (%.1f cm across)" % [case.name, "the vein" if hit else "the arm beside the vein", off.length() * 100.0])
	toasts.clear()
	await bench.press()
	var stuck := patient.iv_set and bench.surgery.room.iv_line.is_attached()
	_check(stuck and patient.iv_in_vein == hit, "%s: the catheter sticks with the tubing run to it, %s" % [case.name, "in the vein" if hit else "but outside the vein"])
	_check(("[debug] Hit the vein" in toasts) == hit, "%s: debug mode says the catheter hit the vein only when it did (%s)" % [case.name, toasts])
	var drip := bench.surgery.tools.drip_bag()
	var given_before := _in_body(patient.drugs, Bench.DRUG)
	bench.surgery.tools.add_liquid(drip, 5.0, {Bench.DRUG: 5.0 * Db.tool(Bench.VIAL).concentration})
	drip.bolus = 5.0
	await bench.frames(ceili(5.0 / ToolActions.DRIP_RATE * Engine.physics_ticks_per_second) + 10)
	var given := _in_body(patient.drugs, Bench.DRUG) - given_before
	_check((given > 0.001) == hit and drip.contents.has(Bench.DRUG) != hit, "%s: a drug in the IV drip %s" % [case.name, "runs into the patient" if hit else "stays in the bag"])
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
	# It works once enough has run in: most of it by its onset through a line.
	await bench.frames(ceili(Db.drug("saline").onset * 1.5 * Engine.physics_ticks_per_second))
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
	me.status.drugs = DrugLevels.new()
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


## Y20-Y22: a syringe swept over a vial snaps smoothly in through its cap, only when the cap faces the surgeon, and
## lets go soon after; zoomed in, a mouse move takes the hand as far across the screen as it does zoomed out; in the
## needle view the mouse moves the hand as seen on screen; a needle pressed into the skin keeps its tip in place, the
## mouse tilting the syringe about it, and tears out when pulled on sideways; the aim shows where the needle goes in at
## any angle; a syringe brought to the IV bag from waist height snaps its needle into the middle of the bag from the
## hand's side, and moved on, comes out of it.
func _hand_checks() -> void:
	print("--- needle_hand")
	var me := bench.surgery.local_surgeon
	var hand := me.hands[me.active]
	await bench.stage(Bench.CASES[0])
	await _vial_snap_checks(me, hand)
	var across: Array[float] = []
	for step in Surgeon.ZOOM_FOV.size():
		me.zoom = step
		await bench.frames(40)
		var before := me.camera().unproject_position(hand.global_position)
		await bench.steer(Vector2(20, 0))
		await bench.frames(2)
		across.append(me.camera().unproject_position(hand.global_position).x - before.x)
		await bench.steer(Vector2(-20, 0))
		await bench.frames(2)
	_check(across[0] > 0.0 and absf(across[1] / across[0] - 1.0) < 0.15, "needle_hand: zoomed in, a mouse move takes the hand as far across the screen as zoomed out (%.1f px, %.1f px)" % across)
	# Into the dish: a vial would hold the needle where it snapped.
	await bench.stage(Bench.CASES[2])
	await bench.press()
	await bench.frames(40)
	var right := (me.camera().global_basis.x * Vector3(1, 0, 1)).normalized()
	var away := (me.camera().global_basis.y * Vector3(1, 0, 1)).normalized()
	for motion: Vector2 in [Vector2(10, 0), Vector2(0, -10)]:
		var before := hand.target
		for i in 5:
			await bench.steer(motion)
			await bench.frames(1)
		var moved := (hand.target - before) * Vector3(1, 0, 1)
		var along := moved.dot(right if motion.x > 0.0 else away)
		# The camera follows the syringe, so it turns a little as the hand moves.
		_check(along > 0.8 * moved.length() and along > 0.005, "needle_hand: in the needle view the mouse %s moves the hand %s on screen (%.3f m of %.3f)" % ["right" if motion.x > 0.0 else "up", "right" if motion.x > 0.0 else "away", along, moved.length()])
	await bench.release()
	me.zoom = 0
	await bench.frames(40)
	await _back_of_hand_checks()
	await bench.stage(Bench.CASES[6])
	# Moved once the camera has come round to the needle view: turning, it would turn the mouse's way with it.
	for i in 60:
		if me._needle_framing >= 0.99:
			break
		await bench.frames(1)
	var tip := bench.syringe.tip_position()
	var tilt := hand.tilt
	var grip := hand.global_position
	for i in 5:
		await bench.steer(Vector2(0, 10))
		await bench.frames(1)
	await bench.frames(5)
	var drift := bench.syringe.tip_position().distance_to(tip)
	_check(drift < 0.001 and not is_equal_approx(hand.tilt, tilt) and hand.global_position.distance_to(grip) > 0.005, "needle_hand: a needle in the skin keeps its tip in place (%.4f m) and the mouse tilts the syringe about it (tilt %.2f -> %.2f)" % [drift, tilt, hand.tilt])
	var went_in := me._needle_anchor
	var body := bench.surgery.patient.body
	var beads_before := body.find_children("BloodBead*", "MeshInstance3D", true, false)
	for i in 60:
		if me._needle_torn:
			break
		await bench.steer(Vector2(20, 0))
		await bench.frames(1)
	var scratch := body.wound_map.value(WoundMap.Layer.WOUNDS, WoundMap.CUT, body.world_to_uv(went_in))
	_check(me._needle_torn and scratch > 0.0, "needle_hand: pulled on, the needle tears out and leaves a scratch where it was in (%.2f)" % scratch)
	var torn_beads := body.find_children("BloodBead*", "MeshInstance3D", true, false).filter(func(node: Node) -> bool: return not beads_before.has(node))
	_check(torn_beads.size() == 1, "needle_hand: tearing out leaves one bead")
	if torn_beads.size() == 1:
		var bead := torn_beads[0] as MeshInstance3D
		_check(((bead.global_position - went_in) * Vector3(1, 0, 1)).length() < 0.0005, "needle_hand: even after tearing out, the bead stays at the injection point")
	await bench.withdraw()
	await _aim_lands(me, hand)
	await _sweep_onto_patient(me, hand)
	await bench.stage(Bench.CASES[13])
	# Use tool up: brought over from waist height.
	await bench.release()
	var bag := bench.surgery.tools.drip_bag()
	hand.local_target = me.to_local(me.global_position + me.global_basis * Vector3(0.1, 1.0, -0.2))
	await bench.frames(20)
	var own_tilt := hand.tilt
	var under := ToolManager.middle(bag) * Vector3(1, 0, 1)
	for i in 30:
		# The hand's own spot, not where the snap holds it.
		var free := me.to_global(hand.local_target)
		hand.local_target = me.to_local(free.lerp(under + Vector3.UP * free.y, 0.1))
		await bench.frames(1)
	await bench.frames(15)
	var off := bench.syringe.tip_position().distance_to(ToolManager.middle(bag))
	# Pointing a little up into it: how far the tip end (-Z) rises.
	var rise := (-bench.syringe.global_basis.z.normalized()).dot(Vector3.UP)
	var side := ((hand.global_position - ToolManager.middle(bag)) * Vector3(1, 0, 1)).dot(me.to_global(hand.local_target) - ToolManager.middle(bag))
	_check(bench.needle_target().get("container") == bag and off < 0.005 and absf(rise - sin(Surgeon.DRIP_TILT)) < 0.05 and side > 0.0, "needle_hand: a syringe brought to the IV bag from waist height snaps its needle a little upward into the middle of the bag, from the hand's side (%.1f cm off, rising %.2f, hand at %.2f m)" % [off * 100.0, rise, hand.global_position.y])
	for i in 10:
		await bench.steer(Vector2(0, 30))
		await bench.frames(1)
	await bench.frames(20)
	_check(bench.needle_target().get("container") != bag and is_equal_approx(hand.tilt, own_tilt), "needle_hand: moved on from the bag, the needle comes out of it and the hand holds the syringe as before (%.2f m from its middle)" % bench.syringe.tip_position().distance_to(ToolManager.middle(bag)))


## A syringe swept over a vial's cap on the tray at a steady 0.15 m/s, Use tool up. Standing (as delivered), its needle
## snaps in through the cap along the vial, without jumping (no frame moves the tip more than 1 cm), and lets go again
## soon after the hand passes it, the hand holding the syringe as before. Lying with its cap toward the surgeon it snaps
## in level; with the cap turned away it doesn't snap at all.
func _vial_snap_checks(me: Surgeon, hand: SurgeonHand) -> void:
	var standing := await _sweep(me, hand)
	_check(standing.snapped > 0, "needle_hand: swept over a standing vial's cap, the needle snaps in through it along the vial")
	_check(standing.jump < 0.01, "needle_hand: snapping in and out, the needle moves smoothly (largest step %.1f mm a frame)" % (standing.jump * 1000.0))
	_check(standing.snapped < 40, "needle_hand: swept past at 0.15 m/s, the needle holds on the vial only briefly (%d frames)" % standing.snapped)
	_check(standing.out, "needle_hand: past the vial, the needle is out of it and the hand holds the syringe as before")
	for toward: bool in [true, false]:
		_vial_lies(me, toward)
		await bench.frames(30)
		var lying := await _sweep(me, hand)
		if toward:
			_check(lying.snapped > 0, "needle_hand: a vial lying with its cap toward the surgeon takes the needle in level through the cap")
		else:
			_check(lying.snapped == 0, "needle_hand: a vial lying with its cap turned away doesn't snap the needle (%d frames)" % lying.snapped)


## Lays the case's vial on its side where it stands, its cap toward the surgeon or away.
func _vial_lies(me: Surgeon, toward: bool) -> void:
	var vial := bench.container
	var at := ToolManager.middle(vial)
	var to_me := ((me.global_position - at) * Vector3(1, 0, 1)).normalized() * (1.0 if toward else -1.0)
	var bottom := (vial.global_transform * vial.bounds).position.y
	# Looking at a point puts a tool's tip (-Z) toward it.
	var lying := Transform3D(Basis.looking_at(to_me, Vector3.UP), Vector3(at.x, bottom + 0.0125, at.z))
	vial.global_transform = lying.translated(-(lying.basis * Vector3(0.0, 0.0, -vial.def.length * 0.5)))
	vial.linear_velocity = Vector3.ZERO
	vial.angular_velocity = Vector3.ZERO


## Sweeps the syringe's tip across the vial's cap at 0.15 m/s from 12 cm before it to 12 cm past it, the hand moved its
## own way: {"snapped": frames the needle was in the cap along the vial, "jump": the largest step the tip took in a frame,
## "out": past it, the needle is out and the hand holds the syringe its own way again}.
func _sweep(me: Surgeon, hand: SurgeonHand) -> Dictionary:
	var length := bench.syringe.def.length
	var cap := bench.container.tip_position()
	var along := (bench.container.global_position - cap).normalized()
	var across := me.global_basis.x
	var start := cap - across * 0.12
	# Off the vial first, so the hand holds the syringe its own way again.
	hand.local_target = me.to_local(me.to_global(hand.local_target) - across * 0.12)
	await bench.frames(40)
	var own := Vector2(hand.tilt, hand.turn)
	var free := start - hand.tip_offset_at(length, own.x, own.y)
	hand.local_target = me.to_local(Vector3(free.x, me.to_global(hand.local_target).y, free.z))
	await bench.frames(20)
	var last := bench.syringe.tip_position()
	var jump := 0.0
	var snapped := 0
	var frames := int(0.24 / 0.15 * 60.0)
	for i in frames:
		var tip := start.lerp(cap + across * 0.12, float(i + 1) / frames)
		free = tip - hand.tip_offset_at(length, own.x, own.y)
		hand.local_target = me.to_local(Vector3(free.x, me.to_global(hand.local_target).y, free.z))
		await bench.frames(1)
		var now := bench.syringe.tip_position()
		jump = maxf(jump, now.distance_to(last))
		last = now
		# The syringe's tip end (-Z) points along the vial, into it.
		var lengthwise := (-bench.syringe.global_basis.z.normalized()).dot(along) > 0.97
		if lengthwise and now.distance_to(cap) < 0.006:
			snapped += 1
	await bench.frames(20)
	var out: bool = bench.needle_target().get("container") != bench.container and is_equal_approx(hand.tilt, own.x)
	return {"snapped": snapped, "jump": jump, "out": out}


## Held over the skin at a shallow and a steep angle (turned with Aim tool), the aim shows where the needle goes in:
## Use tool puts its tip right there.
func _aim_lands(me: Surgeon, hand: SurgeonHand) -> void:
	await bench.stage(Bench.CASES[6])
	await bench.release()
	for tilt: float in [-0.4, -1.3]:
		await bench.aim(Vector2(0.0, (hand.tilt - tilt) / Surgeon.AIM_SENSITIVITY))
		await bench.frames(20)
		var aim := me.aim_point()
		var hovering := bench.syringe.tip_position().distance_to(aim)
		_check(hovering < 0.004, "needle_hand: held at tilt %.1f over the skin, the needle's tip is on the aim (%.1f mm off)" % [tilt, hovering * 1000.0])
		await bench.press()
		await bench.frames(20)
		var tip := bench.syringe.tip_position()
		var across := Vector2(tip.x - aim.x, tip.z - aim.z).length()
		_check(across < 0.002 and absf(tip.y - aim.y) < 0.006, "needle_hand: held at tilt %.1f, the needle goes in where the aim shows (%.1f mm across, %.1f mm down)" % [tilt, across * 1000.0, (aim.y - tip.y) * 1000.0])
		await bench.release()
		await bench.frames(10)


## Swept quickly from low beside the patient up onto the belly, Use tool up, the needle stays over the skin: it never
## sinks under it, where a wheel notch would inject.
func _sweep_onto_patient(me: Surgeon, hand: SurgeonHand) -> void:
	await bench.stage(Bench.CASES[6])
	await bench.release()
	var belly := bench.surgery.patient.body.uv_to_world(Bench.SKIN_UV)
	var beside := belly + ((me.global_position - belly) * Vector3(1, 0, 1)).normalized() * 0.3
	hand.local_target = me.to_local(beside - me.own_tip_offset(me.active))
	await bench.frames(30)
	var low := bench.syringe.tip_position().y
	var inside := 0
	for i in 12:
		var tip := beside.lerp(belly, float(i + 1) / 12.0)
		hand.local_target = me.to_local(Vector3(tip.x, me.to_global(hand.local_target).y, tip.z) - me.own_tip_offset(me.active) * Vector3(1, 0, 1))
		await bench.frames(1)
		var tip_now := bench.syringe.tip_position()
		inside += 1 if float(me._surface_below(tip_now).y) - tip_now.y > 0.003 else 0
	await bench.frames(10)
	var risen := bench.syringe.tip_position().y - low
	_check(risen > 0.05 and inside == 0, "needle_hand: swept up onto the belly (%.0f cm higher), the needle never sinks under the skin (%d frames)" % [risen * 100.0, inside])


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


## The needle aimed anywhere along the back of the surgeon's other hand, wrist to fingertips, rests on the glove and is
## in that hand: it doesn't sink into the glove or drop through it to what's under it.
func _back_of_hand_checks() -> void:
	var own: Dictionary = Bench.CASES.filter(func(case: Dictionary) -> bool: return case.name == "own_hand_push")[0]
	await bench.stage(own)
	var me := bench.surgery.local_surgeon
	var hand := me.hands[me.active]
	var other := me.hands[1 - me.active]
	var glove := other._glove
	for along: float in [0.02, 0.06, 0.1, 0.14, 0.18]:
		var aim := glove.global_position + glove.global_basis.x.normalized() * along
		for i in 40:
			hand.local_target = me.to_local(aim - hand.tip_offset(bench.syringe.def.length) + Vector3.UP * 0.04)
			await get_tree().physics_frame
		# Held still there, it glides down onto the glove (Surgeon.SYRINGE_GLIDE).
		await bench.frames(20)
		var tip := bench.syringe.tip_position()
		var back := other.glove_middle(tip).y + SurgeonHand.PALM_HALF_THICKNESS
		var target := bench.needle_target()
		var in_hand: bool = target.kind == "surgeon" and target.part == "hand" and bench.syringe.state == SurgicalTool.State.HELD
		_check(in_hand and absf(tip.y - back) < 0.004, "needle_hand: %.0f cm from the wrist the needle rests on the back of the other hand (%.1f mm off it) and is in it (%s)" % [along * 100.0, (tip.y - back) * 1000.0, target])
	await bench.withdraw()


## How much of a drug went into a body so far, in the blood or still soaking in (shares of the right dose).
static func _in_body(levels: DrugLevels, drug: String) -> float:
	var entry: Dictionary = levels.entries.get(drug, {})
	return entry.depot + entry.level if not entry.is_empty() else 0.0


func _check(ok: bool, what: String) -> bool:
	assert_true(ok, what)
	return ok
