class_name ToolActions
extends RefCounted
## What each tool action does to the patient. Host only, runs every physics frame for held and standing tools.
## Tools share actions: a lighter and a cautery pen both "cauterize", with different ToolDef numbers.

## How each action is controlled. Use tool (LMB, held) lowers every tool onto its spot and presses its trigger.
## Actions listed here take an effort level from the wheel (0 does nothing, 3 the most), named by the value.
const LEVEL_NAMES: Dictionary = {
	"cut": "Depth", "suture": "Tension", "cauterize": "Heat", "saw": "Speed", "suction": "Suction",
	"swab": "Pressure", "inject": "Plunger", "pour": "Pour",
}
## Actions listed here do their thing the moment Use tool is pressed (or while held), named by the value.
const TRIGGER_NAMES: Dictionary = {
	"clamp": "Pinch / let go", "smash": "Strike", "tourniquet": "Tighten", "graft": "Place graft",
	"shock": "Charge (hold), let go to shock", "sew": "Stitch", "spread": "Set in / take out",
}
## Cut depth per level (0 just rests on the skin, 3 deep). 0.7+ goes through the skin.
const DEPTH_BY_LEVEL: Array[float] = [0.0, 0.3, 0.6, 1.0]
const DEFIB_CHARGE_TIME := 2.0
## How long a cut the point of a blade makes pressed straight in (meters).
const STAB_LENGTH := 0.006
## A blade only cuts along its edge: a move further off the edge line than this (cosine) just drags it.
const ALONG_BLADE := 0.8
## Clamps that can pinch a cotton pad, and how close to the pad their tip has to be.
const PAD_HOLDERS: PackedStringArray = ["forceps", "hemostat"]
const PAD_REACH := 0.04
## How close to the iodine dish a bottle or pad has to be to pour into it or dip in it.
const DISH_REACH := 0.07
## A full dish soaks this many pads. A soaked pad runs dry after 1 / PAD_DRAIN seconds of wiping.
const PADS_PER_DISH := 4.0
const PAD_DRAIN := 0.12
## A syringe has its own wheel instead of an effort level: one notch moves the plunger this many ml (see plunge()).
const PLUNGER_STEP := 1.0
## A needle's wheel works the free end of its thread: one notch changes its tension (a rest length ratio, see
## TissueSim.THREAD_CLOSED) this much, down tightens and up loosens. Use tool held this long (seconds) adds the last
## hole and ties the thread off.
const SUTURE_TENSION_STEP := 0.08
const SUTURE_TENSION_RANGE := Vector2(0.52, 1.56)
const SUTURE_TIE_HOLD := 0.65
## A spreader's (the Gelpi retractor's) wheel opens and closes it: one notch moves its tips this much further apart
## (meters), between closed and fully open (SPREAD_RANGE). Closed, its points still sit SPREAD_RANGE.x apart
## (GELPI_CLOSED in tools/assetgen/instruments.py).
const SPREAD_STEP := 0.005
const SPREAD_RANGE := Vector2(0.012, 0.08)
## Wipes paint big soft disks: at most this often, or once the tool moved PAINT_MOVE (uv) since the last one.
const PAINT_INTERVAL := 1.0 / 15.0
const PAINT_MOVE := 0.02


## Where a blade's edge runs on the skin: where the blade plane meets a flat surface, so rotating the tool turns it.
static func blade_direction(tool: SurgicalTool) -> Vector3:
	var edge := tool.global_basis.x.cross(Vector3.UP)
	if edge.length() < 0.2:
		edge = -tool.global_basis.z * Vector3(1, 0, 1)
	return edge.normalized()


## Which way a spreader opens across the floor: along the tool's own X axis, where its jaws swing apart. It's held
## upright (Surgeon._local_update()), so that axis lies flat however the tool is rolled.
static func spread_axis(tool: SurgicalTool) -> Vector3:
	return (tool.global_basis.x * Vector3(1, 0, 1)).normalized()


## Where a spreader's two tips are, opened `spread` meters apart about its tip: the one toward -X first.
static func spread_tips(tool: SurgicalTool) -> Array[Vector3]:
	var half := spread_axis(tool) * tool.spread * 0.5
	return [tool.tip_position() - half, tool.tip_position() + half]


## The tool is doing its job right now (for animation and fingers), not only resting on something.
static func in_use(action: String, lowered: bool, trigger: bool, level: int) -> bool:
	if TRIGGER_NAMES.has(action):
		return trigger
	return lowered and (level > 0 or not LEVEL_NAMES.has(action))


## hand: {"lowered": bool, "trigger": bool, "level": int, "speed": float, "peer": int, "mods": Modifiers}
static func update(tool: SurgicalTool, hand: Dictionary, patient: Patient, dt: float) -> void:
	var lowered: bool = hand.lowered
	var trigger: bool = hand.trigger
	var level: int = hand.level
	# Use lowers the tool and presses its trigger at once: the press counts a frame later, once the tool has come down
	# onto whatever is under it (hovering, a tool can be just out of touch of a rounded limb).
	var pressed := trigger and tool.trigger_before and not tool.pressed_before
	var released := not trigger and tool.trigger_before
	var level_up := level > tool.level_before
	# Lowered since the last frame too: the hand has come down onto whatever is under it by now.
	var settled := lowered and tool.lowered_before
	tool.pressed_before = trigger and tool.trigger_before
	tool.trigger_before = trigger
	tool.level_before = level
	if lowered and not tool.lowered_before:
		tool.stroke += 1
		tool.stabbed_level = 0
	tool.lowered_before = lowered
	# Powered and pressed tools work harder at higher levels.
	var effort := level / 3.0
	var tip := tool.tip_position()
	var probe := patient.body.probe(tip)
	var zone: String = probe.zone
	var uv: Vector2 = probe.uv
	var touching := zone in ["site", "cavity", "body"]
	if lowered and touching:
		_on_contact(tool, zone, probe, patient)
		_bloody(tool, zone, uv, patient, dt)
	var def := tool.def
	var mods: Modifiers = hand.mods
	if lowered and zone in ["site", "cavity"]:
		_contact_sound(tool, hand.speed, level, effort, tip)
	match def.action:
		"cut":
			if lowered and level > 0 and zone == "site" and level > tool.stabbed_level:
				# Pressed in at a new depth: the point goes in as wide as the blade, before it's moved at all.
				tool.stabbed_level = level
				var half := blade_direction(tool) * STAB_LENGTH * 0.5
				var from := patient.body.world_to_uv(tip - half)
				var to := patient.body.world_to_uv(tip + half)
				patient.cut(tool.uid * 1000 + tool.stroke, from, to, DEPTH_BY_LEVEL[level], def.sharpness, not tool.sterile, 0.0)
				Surgery.current.sound("cut_deep" if level >= 3 else "cut_skin", tip)
			# Pressed in at full effort where the muscle is thin (no fat over it), the point reaches the bone under it.
			if lowered and level == 3 and zone == "site" and probe.depth >= patient.body.muscle_bottom():
				patient.scrape_bone(uv, tip, dt)
			# Moved along its edge, the blade cuts what it passes through, also where it runs on past the end of an
			# opening it's already in.
			if lowered and level > 0 and zone in ["site", "cavity"]:
				if zone == "cavity":
					patient.cut_cavity(uv, probe.depth, def.sharpness, not tool.sterile, dt * effort)
				if tool.last_uv.x >= 0.0 and tool.last_uv.distance_to(uv) > 0.003:
					var moved := (tip - tool.last_tip) * Vector3(1, 0, 1)
					if absf(moved.normalized().dot(blade_direction(tool))) >= ALONG_BLADE:
						patient.cut(tool.uid * 1000 + tool.stroke, tool.last_uv, uv, DEPTH_BY_LEVEL[level], def.sharpness, not tool.sterile, hand.speed)
						patient.debride_at(uv)
						Surgery.current.sound("cut_deep" if level >= 3 else "cut_skin", tip)
					else:
						# Dragged sideways: the next stroke starts here.
						tool.stroke += 1
				if tool.last_uv.x < 0.0 or tool.last_uv.distance_to(uv) > 0.003:
					tool.last_uv = uv
					tool.last_tip = tip
			else:
				tool.last_uv = Vector2(-1, -1)
		"clamp":
			var power := def.power * mods.mult("grip_strength_mult")
			var tools := Surgery.current.tools
			var pad := tools.carried_by(tool)
			var loose_pad := tools.nearest_of("cotton_pad", tip, PAD_REACH) if pressed and lowered and def.id in PAD_HOLDERS else null
			if pad and pad.def.action == "graft":
				# A graft taken from the skin goes down where it's pressed onto a cleaned burn; pressed in the air it's let go.
				if pressed and lowered and zone == "site" and patient.graft_at(uv, pad.def):
					_use_charge(pad)
					if pad.charges == 0:
						tools.consume(pad)
				elif pressed and not touching:
					tools.drop_carried(tool)
			elif pad:
				# Use lowers the pad to wipe or dip it; pressed in the air, away from the dish, it lets the pad go.
				if pressed and not touching and tools.nearest_of("iodine_dish", tip, DISH_REACH) == null:
					tools.drop_carried(tool)
				elif lowered:
					_wipe(pad, zone, uv, tip, patient, dt, false)
			elif loose_pad and tool.grip_info.is_empty():
				tools.carry(loose_pad, tool)
			# Pinching takes hold only on something the jaws were lowered onto; letting go works anywhere.
			elif pressed and (lowered or not tool.grip_info.is_empty()):
				if tool.grip_info.is_empty():
					tool.grip_info = patient.grip(tool.uid, zone, uv, probe.depth)
					if tool.grip_info.type == "none":
						tool.grip_info = {}
				else:
					patient.release_grip(tool.uid, tool.grip_info, false)
					tool.grip_info = {}
				Surgery.current.set_attached(hand.peer, tool.slot, not tool.grip_info.is_empty())
			elif not tool.grip_info.is_empty():
				tool.grip_info = patient.update_grip(tool.uid, tool.grip_info, tip, power, dt, hand.speed)
				if tool.grip_info.type == "none":
					tool.grip_info = {}
					Surgery.current.set_attached(hand.peer, tool.slot, false)
		"sew":
			_sew(tool, patient, zone, uv, lowered, trigger, released, dt, tip)
		"spread":
			# Pressed onto the skin, the jaws go in on both sides of the aim and stay there; pressed again they come out.
			if pressed and tool.grip_info.is_empty() and lowered:
				var info := patient.set_spreader(tool.uid, spread_tips(tool), tool.spread)
				if info.type != "none":
					tool.grip_info = info
					Surgery.current.set_attached(hand.peer, tool.slot, true)
					Surgery.current.tools.sync_spread(tool)
			elif pressed and not tool.grip_info.is_empty():
				patient.release_grip(tool.uid, tool.grip_info, false)
				tool.grip_info = {}
				Surgery.current.set_attached(hand.peer, tool.slot, false)
				Surgery.current.tools.sync_spread(tool)
		"suture":
			if lowered and level > 0 and zone == "site" and tool.charges != 0:
				# Where the skin and fat still gape and the muscle shows, the needle reaches it: sewing the muscle on both
				# sides closes the opening before the muscle right here is done.
				if patient.body.layer_at(uv) == "muscle" and patient.close_muscle_at(uv, def, dt):
					tool.charges -= 1 if tool.charges > 0 else 0
					Surgery.current.sound("suture_pull", tip)
				elif patient.close_at(uv, def, dt, mods.mult("improvised_mult"), level):
					tool.charges -= 1 if tool.charges > 0 else 0
					Surgery.current.sound({"skin_stapler": "staple", "office_stapler": "office_staple", "surgical_tape": "tape_rip", "duct_tape": "tape_rip"}.get(def.id, "suture_pull"), tip)
			elif lowered and level > 0 and zone == "cavity" and tool.charges != 0:
				# An internal injury under the needle comes first: sewing the muscle of the opening shut would close the
				# way in to it. Then, inside a wound through the muscle, the muscle.
				if not patient.close_internal_at(uv, def, dt) and patient.close_muscle_at(uv, def, dt):
					tool.charges -= 1 if tool.charges > 0 else 0
					Surgery.current.sound("suture_pull", tip)
		"cauterize":
			if level_up and level == 1 and def.id == "lighter":
				Surgery.current.sound("lighter_flick", tip)
			if lowered and level > 0 and zone in ["site", "cavity"] and tool.charges != 0:
				patient.cauterize_at(zone, uv, def, dt * effort)
				Surgery.current.effect("smoke", tip, 180)
				if randf() < dt * 1.2:
					Surgery.current.sound("cautery_sizzle", tip)
				if def.id == "lighter" and randf() < dt:
					tool.charges -= 1
		"mark":
			if lowered and zone == "site":
				if tool.last_uv.x >= 0.0:
					patient.mark(tool.last_uv, uv)
				tool.last_uv = uv
			else:
				tool.last_uv = Vector2(-1, -1)
		"inject":
			# The needle goes in while lowered; pushing the plunger all the way gives the dose.
			if lowered and touching and level_up and level == 3 and tool.charges != 0:
				if def.iv_only:
					Surgery.current.announce("%s goes on the IV stand, not in the patient." % def.name, true)
				else:
					patient.administer(def.drug, "direct")
					Surgery.current.sound("syringe_inject", tip)
					Surgery.current.effect("bead", tip, 0)
					_use_charge(tool)
		"syringe":
			# The wheel works the plunger (plunge()). What went into the patient is given once the needle is out.
			if not tool.injecting.is_empty() and not needle_target(tool, patient).kind in ["vein", "tissue", "surgeon"]:
				finish_injection(tool, patient)
		"shock":
			var on_chest: bool = zone == "site" and patient.scenario.site in ["chest", "abdomen"] or probe.get("part", "") == "torso"
			if trigger and lowered and on_chest:
				if tool.charge_time == 0.0:
					Surgery.current.sound("defib_charge", tip)
				tool.charge_time += dt
			elif released and tool.charge_time >= DEFIB_CHARGE_TIME * mods.mult("defib_charge_mult"):
				patient.shock(def.power * 0.5)
				Surgery.current.sound("defib_shock", tip)
				Surgery.current.effect("spark", tip, 0)
				if zone == "site":
					# Paddle contact leaves a faint red mark on the skin.
					patient.paint(WoundMap.Layer.WOUNDS, WoundMap.BURN, uv, uv, 0.06, 0.1, WoundMap.Mode.MAX)
				Surgery.current.shock_bystanders(hand.peer)
				tool.charge_time = 0.0
			elif not trigger:
				tool.charge_time = 0.0
		"saw":
			if lowered and level > 0 and zone in ["site", "cavity"]:
				if patient.saw_at(uv, def, dt * effort):
					Surgery.current.effect("dust", tip, 150)
				elif zone == "site" and tool.last_uv.x >= 0.0 and tool.last_uv.distance_to(uv) > 0.004:
					patient.cut(tool.uid * 1000 + tool.stroke, tool.last_uv, uv, 1.0, 0.3, not tool.sterile, 0.5)
				if randf() < dt * 2.0:
					Surgery.current.sound("saw_bone", tip)
				tool.last_uv = uv
		"smash":
			if pressed and lowered and touching:
				patient.smash_at(uv, def)
				if zone in ["site", "cavity"]:
					Surgery.current.effect("spatter", tip, 0)
		"suction":
			if lowered and level > 0 and zone in ["site", "cavity"]:
				patient.suction_at(zone, uv, def, dt * effort)
				if randf() < dt * 1.2:
					Surgery.current.sound("suction_slurp", tip)
				if def.id == "metal_straw":
					Surgery.current.add_sickness(hand.peer, dt * 0.08)
		"swab":
			if def.id == "cotton_pad" and lowered and level > 0:
				_wipe(tool, zone, uv, tip, patient, dt * effort, true)
			elif lowered and level > 0 and zone in ["site", "cavity"]:
				var wiped := _gather(tool, uv, dt * effort)
				if wiped > 0.0:
					patient.swab_at(zone, uv, def, wiped)
		"pour":
			if lowered and level > 0:
				var dish := Surgery.current.tools.nearest_of("iodine_dish", tip, DISH_REACH)
				if dish:
					Surgery.current.tools.set_fill(dish, dish.fill + def.power * effort * dt)
				elif touching:
					Surgery.current.announce("Pour the %s into the iodine dish." % def.name.to_lower(), true)
		"tourniquet":
			# Pressed onto an arm or a leg, the band goes around the limb there and stays when the hand lets go.
			var ring := patient.body.limb_ring(tip) if pressed and lowered else {}
			if not ring.is_empty():
				patient.apply_tourniquet()
				Surgery.current.tools.wrap(tool, ring)
		"graft":
			if pressed and lowered and zone == "site" and tool.charges != 0 and patient.graft_at(uv, def):
				_use_charge(tool)
		"iv_line":
			# Held against an arm, not only on the frame the button went down: the tip may land a moment later.
			var arm: bool = str(probe.get("part", "")).begins_with("arm") or zone == "site" and patient.scenario.site == "forearm"
			# It sticks wherever it goes into the arm, but only a needle in a vein lets anything through.
			if settled and arm and not patient.iv_set:
				patient.set_iv(tip, patient.body.vein_at(tip))
				Surgery.current.effect("bead", tip, 0)
				_use_charge(tool)


## The needle: a click (Use tool let go before SUTURE_TIE_HOLD) where it rests on a wound passes its thread through a
## new hole there, a longer hold adds the last hole (where there's room for one) and ties the thread off. Held over an
## internal injury in the opening, it sews that instead.
static func _sew(tool: SurgicalTool, patient: Patient, zone: String, uv: Vector2, lowered: bool, trigger: bool, released: bool, dt: float, tip: Vector3) -> void:
	if trigger:
		tool.suture_hold += dt
		# Let go anywhere but on the patient, a click makes no hole.
		tool.suture_at = uv if lowered and zone in ["site", "cavity"] else Vector2(-1, -1)
		if lowered and zone == "cavity" and patient.close_internal_at(uv, tool.def, dt):
			tool.suture_press_used = true
		elif tool.suture_hold >= SUTURE_TIE_HOLD and not tool.suture_press_used and tool.suture_thread != 0:
			# The last hole, if there's room for one here, then the knot either way.
			tool.suture_press_used = true
			_add_suture_hole(tool, patient)
			patient.finish_suture(tool.suture_thread, tool.def.quality)
			Surgery.current.sound("suture_pull", tip)
	elif released:
		if not tool.suture_press_used and _add_suture_hole(tool, patient):
			Surgery.current.sound("suture_pull", tip)
		tool.suture_hold = 0.0
		tool.suture_press_used = false
	_end_finished_suture(tool, patient)


## Passes the needle's thread through a new hole where it was last on the patient, starting a thread if it has none.
static func _add_suture_hole(tool: SurgicalTool, patient: Patient) -> bool:
	if tool.suture_at.x < 0.0:
		return false
	if tool.suture_thread == 0:
		tool.suture_thread = patient.new_suture()
	var placed := patient.place_suture_anchor(tool.suture_thread, tool.suture_at, tool.suture_tension)
	var info := patient.body.tissue.thread_info(tool.suture_thread)
	if info.is_empty():
		tool.suture_thread = 0
	elif tool.suture_layer != info.layer:
		tool.suture_layer = info.layer
		Surgery.current.tools.sync_suture(tool)
	return placed


## A thread tied off or torn through is done with: the needle's next hole starts a new one.
static func _end_finished_suture(tool: SurgicalTool, patient: Patient) -> void:
	if tool.suture_thread != 0 and patient.suture_done(tool.suture_thread):
		tool.suture_thread = 0
		tool.suture_layer = TissueSim.Depth.NONE
		Surgery.current.tools.sync_suture(tool)


## Wheel direction is positive for up (loosen), negative for down (tighten).
static func adjust_suture_tension(tool: SurgicalTool, direction: int, patient: Patient) -> void:
	tool.suture_tension = clampf(tool.suture_tension + direction * SUTURE_TENSION_STEP, SUTURE_TENSION_RANGE.x, SUTURE_TENSION_RANGE.y)
	if tool.suture_thread != 0:
		patient.set_suture_tension(tool.suture_thread, tool.suture_tension)
		_end_finished_suture(tool, patient)


## Wheel direction is positive for up (open), negative for down (close). Set in a cut, the jaws take its edges along.
static func adjust_spread(tool: SurgicalTool, direction: int, patient: Patient) -> void:
	tool.spread = clampf(tool.spread + direction * SPREAD_STEP, SPREAD_RANGE.x, SPREAD_RANGE.y)
	if not tool.grip_info.is_empty():
		patient.open_spreader(tool.grip_info, tool.spread)


## How a needle's thread reads at its tension in the layer it's in (skin before a thread is started): "loose" (the
## edges don't meet), "closed", or "too tight" (past halfway from closed to tearing through).
static func thread_state(tool: SurgicalTool) -> String:
	var layer := tool.suture_layer if tool.suture_layer != TissueSim.Depth.NONE else TissueSim.Depth.SKIN
	var closed := TissueSim.THREAD_CLOSED[layer]
	if tool.suture_tension > closed:
		return "loose"
	return "too tight" if tool.suture_tension < (closed + TissueSim.THREAD_TEAR[layer]) * 0.5 else "closed"


## What a syringe's needle is in, the same on every peer: {"kind": "container", "container": the vial, kidney dish
## or IV drip},
## {"kind": "vein"} (a drawn forearm vein), {"kind": "tissue", "layer": "skin", "fat", "muscle" or "cavity"} (the
## deepest layer a cut shows there), {"kind": "surgeon", "peer", "part": "hand" or "body", "at"} (a glove, the other
## one of the hand holding it too, or a partner's body), or {"kind": "air"}. The tip resting just above something
## counts as in it. A glove comes before the patient under it, a partner's body after.
static func needle_target(tool: SurgicalTool, patient: Patient) -> Dictionary:
	var tip := tool.tip_position()
	var container := Surgery.current.tools.nearest_container(tip)
	if container:
		return {"kind": "container", "container": container}
	var holder: Surgeon = Surgery.current.surgeons.get(tool.holder) if tool.state == SurgicalTool.State.HELD else null
	var holding := holder.hands[tool.slot] if holder else null
	var in_body := {}
	for surgeon: Surgeon in Surgery.current.surgeons.values():
		var hit := surgeon.needle_part(tip, holding)
		if not hit.is_empty():
			hit.merge({"kind": "surgeon", "peer": surgeon.peer_id})
			if hit.part == "hand":
				return hit
			in_body = hit
	if patient.body.vein_at(tip):
		return {"kind": "vein"}
	var probe := patient.body.probe(tip)
	match probe.zone:
		"site":
			return {"kind": "tissue", "layer": patient.body.layer_at(probe.uv)}
		"cavity":
			return {"kind": "tissue", "layer": "cavity"}
		"body":
			return {"kind": "tissue", "layer": "skin"}
	return in_body if not in_body.is_empty() else {"kind": "air"}


## One move of a syringe's plunger, host only: ml > 0 pulls it out, ml < 0 pushes it in, whether or not the needle
## is lowered. Pulled, it draws what the needle is in: a vial's or dish's liquid, blood from a vein, or air.
## In skin, fat or muscle nothing comes and the plunger stays put. Pushed, the air at the needle goes first, then the
## liquid: into a vial (as much as fits) or the dish, a vein (blood goes back, drugs are given as IV), the tissue
## (given as a direct injection), a surgeon (given to them, see Surgery.dose_surgeon()) or squirted out.
static func plunge(tool: SurgicalTool, ml: float, patient: Patient) -> void:
	var tools := Surgery.current.tools
	var target := needle_target(tool, patient)
	var container: SurgicalTool = target.get("container")
	if ml > 0.0:
		var amount := minf(ml, tool.def.volume - tool.ml - tool.air)
		if amount <= 0.0:
			return
		match target.kind:
			"container":
				# An emptied vial gives air.
				var drawn := minf(amount, container.ml)
				tools.transfer(container, tool, drawn)
				tools.add_liquid(tool, 0.0, {}, amount - drawn)
			"vein":
				patient.vitals.blood_ml -= amount
				tools.add_liquid(tool, amount, {"blood": amount})
			"air":
				tools.add_liquid(tool, 0.0, {}, amount)
		return
	var air := minf(-ml, tool.air)
	var liquid := minf(-ml - air, tool.ml)
	if container:
		# A vial or bag is sealed: once it's full the plunger won't push more liquid. Past a dish's rim it spills.
		var fits := minf(liquid, container.def.volume - container.ml)
		if not container.def.action in ["vial", "drip"]:
			tools.transfer(tool, null, liquid - fits)
		liquid = fits
	if air > 0.0:
		tools.add_liquid(tool, 0.0, {}, -air)
	if liquid <= 0.0:
		return
	match target.kind:
		"container":
			tools.transfer(tool, container, liquid)
		"vein", "tissue", "surgeon":
			# A surgeon's route names who gets it: "surgeon:<peer>".
			var route: String = {"vein": "vein", "tissue": "direct"}.get(target.kind, "surgeon:%d" % target.get("peer", 0))
			if route != tool.injecting_route:
				finish_injection(tool, patient)
				tool.injecting_route = route
			var pushed := tools.transfer(tool, null, liquid)
			for drug: String in pushed:
				if drug != "blood":
					tool.injecting[drug] = tool.injecting.get(drug, 0.0) + pushed[drug]
				elif route == "vein":
					# Blood pushed back into a vein is the patient's again.
					patient.vitals.blood_ml += pushed[drug]
		_:
			tools.transfer(tool, null, liquid)


## The needle came out (or the syringe left the hand): everything pushed in takes effect as one dose, in the patient
## or the surgeon it went into.
static func finish_injection(tool: SurgicalTool, patient: Patient) -> void:
	if tool.injecting.is_empty():
		return
	var route := tool.injecting_route
	for drug: String in tool.injecting:
		if route.begins_with("surgeon:"):
			Surgery.current.dose_surgeon(route.get_slice(":", 1).to_int(), drug, tool.injecting[drug])
		else:
			patient.administer(drug, route, tool.injecting[drug])
	tool.injecting.clear()
	var tip := tool.tip_position()
	Surgery.current.sound("syringe_inject", tip)
	Surgery.current.effect("bead", tip, 0)


## A cotton pad soaks up iodine in the dish, then leaves it on the skin until it runs dry.
## Iodine only stays sterile on the way in if a clean pad is held with forceps: a glove on it spoils the site.
static func _wipe(pad: SurgicalTool, zone: String, uv: Vector2, tip: Vector3, patient: Patient, dt: float, gloved: bool) -> void:
	var tools := Surgery.current.tools
	# On the patient it always wipes, even with a dish left right beside the site.
	if not zone in ["site", "cavity"]:
		var dish := tools.nearest_of("iodine_dish", tip, DISH_REACH)
		var soak := minf(minf(dt * 2.0, 1.0 - pad.fill), dish.fill * PADS_PER_DISH) if dish else 0.0
		if soak > 0.0:
			tools.set_fill(pad, pad.fill + soak)
			tools.set_fill(dish, dish.fill - soak / PADS_PER_DISH)
		return
	var soaked := pad.fill > 0.0
	var wiped := _gather(pad, uv, dt)
	if wiped > 0.0:
		patient.swab_at(zone, uv, pad.def, wiped, "iodine" if soaked else "")
	if soaked:
		tools.set_fill(pad, pad.fill - PAD_DRAIN * dt)
		if (gloved or not pad.sterile) and zone == "site" and not pad.reported.has("dirty"):
			pad.reported["dirty"] = true
			patient.contaminate_site("")
			Surgery.current.scoring.add("dirty_tool")


## Collects wiping time and returns it once it's worth painting (0 until then), so a pad held still or moved
## slowly paints a few times a second instead of every physics frame.
static func _gather(tool: SurgicalTool, uv: Vector2, dt: float) -> float:
	tool.paint_dt += dt
	if tool.paint_dt < PAINT_INTERVAL and tool.paint_uv.distance_to(uv) < PAINT_MOVE:
		return 0.0
	var gathered := tool.paint_dt
	tool.paint_dt = 0.0
	tool.paint_uv = uv
	return gathered


## Standing (self-retaining) clamps keep holding their grip after the hand lets go.
static func update_standing(tool: SurgicalTool, patient: Patient, dt: float) -> void:
	if tool.def.action == "drip":
		drip(tool, patient)
	if not tool.grip_info.is_empty():
		tool.grip_info = patient.update_grip(tool.uid, tool.grip_info, tool.tip_position(), tool.def.power, dt, 0.0)
		if tool.grip_info.type == "none":
			tool.grip_info = {}


## The IV drip runs what was pushed into it down the line once no needle is in it, if the line is in a vein.
## Its own fluid just drips (it does nothing), and so does blood in it.
static func drip(bag: SurgicalTool, patient: Patient) -> void:
	var drugs := bag.contents.keys().filter(func(drug: String) -> bool: return drug != "blood")
	if drugs.is_empty() or not patient.iv_working() or Surgery.current.tools.needle_in(bag):
		return
	for drug: String in drugs:
		patient.administer(drug, "iv", bag.contents[drug])
		bag.contents.erase(drug)


## A looping bed under a blade, swab, clamp or suction tip working the site, louder the faster it moves.
## It plays even when nothing gets cut: the one-shots (cut, sizzle, saw, slurp) mark the actual effect.
static func _contact_sound(tool: SurgicalTool, speed: float, level: int, effort: float, tip: Vector3) -> void:
	var id := ""
	var strength := 0.0
	match tool.def.action:
		"cut":
			if level > 0 and speed > 0.015:
				id = "contact_cut"
				strength = clampf(speed * 2.5, 0.2, 1.0)
		"swab":
			if level > 0 and speed > 0.01:
				id = "contact_swab"
				strength = clampf(speed * 2.0, 0.15, 0.75)
		"clamp":
			if not tool.grip_info.is_empty() and speed > 0.01:
				id = "contact_swab"
				strength = clampf(speed * 1.5, 0.15, 0.6)
		"suction":
			if level > 0:
				id = "contact_suction"
				strength = effort
	if not id.is_empty():
		Surgery.current.contact_sound(tool.uid, id, tip, strength)


static func _on_contact(tool: SurgicalTool, zone: String, probe: Dictionary, patient: Patient) -> void:
	if zone == "body":
		patient.touch(probe.get("part", ""))
		return
	if not tool.sterile and not tool.reported.has("dirty") and tool.def.action != "swab":
		tool.reported["dirty"] = true
		patient.contaminate_site("")
		Surgery.current.scoring.add("dirty_tool")
	if tool.is_improvised() and not tool.reported.has("improvised"):
		tool.reported["improvised"] = true
		Surgery.current.scoring.add("improvised_tool")


## Working in blood leaves it on the tool: blades, clamps and suction pick it up fast, gauze soaks it up.
## A blade comes away bloody from skin it has cut, not from resting on whole skin.
static func _bloody(tool: SurgicalTool, zone: String, uv: Vector2, patient: Patient, dt: float) -> void:
	var wet := patient.body.blood_at(uv) if zone == "site" else 0.0
	var cut := tool.def.action == "cut" and zone == "site" and patient.body.wound_map.value(WoundMap.Layer.WOUNDS, WoundMap.CUT, uv) > 0.1
	if zone == "cavity" or cut:
		wet = maxf(wet, 0.6)
	if wet > 0.15:
		var rate := 1.2 if tool.def.action == "swab" else 0.5
		Surgery.current.tools.add_blood(tool, wet * rate * dt)


static func _use_charge(tool: SurgicalTool) -> void:
	if tool.charges > 0:
		tool.charges -= 1
		if tool.charges == 0 and tool.def.action in ["inject", "iv_line"]:
			Surgery.current.tools.consume(tool)
