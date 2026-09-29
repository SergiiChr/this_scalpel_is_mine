class_name ToolActions
extends RefCounted
## What each tool action does to the patient. Host only, runs every physics frame for held and standing tools.
## Tools share actions: a lighter and a cautery pen both "cauterize", with different ToolDef numbers.

## How each action is controlled. Every tool is lowered onto its spot with Lower tool (LMB) first.
## Actions listed here take an effort level from the wheel (0 does nothing, 3 the most), named by the value.
const LEVEL_NAMES: Dictionary = {
	"cut": "Depth", "suture": "Tension", "cauterize": "Heat", "saw": "Speed", "suction": "Suction",
	"swab": "Pressure", "inject": "Plunger", "pour": "Pour",
}
## Actions listed here do their thing on Tool action (RMB) instead, named by the value.
const TRIGGER_NAMES: Dictionary = {
	"clamp": "Pinch / let go", "smash": "Strike", "tourniquet": "Tighten", "graft": "Place graft",
	"shock": "Charge (hold), let go to shock",
}
## Cut depth per level (0 just rests on the skin, 3 deep). 0.7+ goes through the skin.
const DEPTH_BY_LEVEL: Array[float] = [0.0, 0.3, 0.6, 1.0]
const DEFIB_CHARGE_TIME := 2.0
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


## Where a blade's edge runs on the skin: where the blade plane meets a flat surface, so rotating the tool turns it.
static func blade_direction(tool: SurgicalTool) -> Vector3:
	var edge := tool.global_basis.x.cross(Vector3.UP)
	if edge.length() < 0.2:
		edge = -tool.global_basis.z * Vector3(1, 0, 1)
	return edge.normalized()


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
	var pressed := trigger and not tool.trigger_before
	var released := not trigger and tool.trigger_before
	var level_up := level > tool.level_before
	tool.trigger_before = trigger
	tool.level_before = level
	if lowered and not tool.lowered_before:
		tool.stroke += 1
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
	match def.action:
		"cut":
			if lowered and level > 0 and zone == "site":
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
			elif lowered and level > 0 and zone == "cavity":
				patient.cut_cavity(uv, probe.depth, def.sharpness, not tool.sterile, dt * effort)
			else:
				tool.last_uv = Vector2(-1, -1)
		"clamp":
			var power := def.power * mods.mult("grip_strength_mult")
			var tools := Surgery.current.tools
			var pad := tools.carried_by(tool)
			var loose_pad := tools.nearest_of("cotton_pad", tip, PAD_REACH) if pressed and lowered and def.id in PAD_HOLDERS else null
			if pad:
				if pressed:
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
		"suture":
			if lowered and level > 0 and zone == "site" and tool.charges != 0:
				if patient.close_at(uv, def, dt, mods.mult("improvised_mult"), level):
					tool.charges -= 1 if tool.charges > 0 else 0
					Surgery.current.sound({"skin_stapler": "staple", "office_stapler": "office_staple", "surgical_tape": "tape_rip", "duct_tape": "tape_rip"}.get(def.id, "suture_pull"), tip)
			elif lowered and level > 0 and zone == "cavity":
				patient.close_internal_at(uv, probe.depth, def, dt)
		"cauterize":
			if level_up and level == 1 and def.id == "lighter":
				Surgery.current.sound("lighter_flick", tip)
			if lowered and level > 0 and zone in ["site", "cavity"] and tool.charges != 0:
				patient.cauterize_at(zone, uv, probe.depth, def, dt * effort)
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
				patient.swab_at(zone, uv, def, dt * effort)
		"pour":
			if lowered and level > 0:
				var dish := Surgery.current.tools.nearest_of("iodine_dish", tip, DISH_REACH)
				if dish:
					Surgery.current.tools.set_fill(dish, dish.fill + def.power * effort * dt)
				elif touching:
					Surgery.current.announce("Pour the %s into the iodine dish." % def.name.to_lower(), true)
		"tourniquet":
			var limb: bool = str(probe.get("part", "")).begins_with("arm") or str(probe.get("part", "")).begins_with("leg") or zone == "site" and patient.body.is_limb_site()
			if pressed and lowered and limb:
				patient.apply_tourniquet()
				Surgery.current.tools.leave_standing(tool)
		"graft":
			if pressed and lowered and zone == "site" and tool.charges != 0 and patient.graft_at(uv, def):
				_use_charge(tool)
		"iv_line":
			# Held against an arm, not only on the frame the button went down: the tip may land a moment later.
			var arm: bool = str(probe.get("part", "")).begins_with("arm") or zone == "site" and patient.scenario.site == "forearm"
			if lowered and arm and not patient.iv_set:
				patient.set_iv(tip)
				Surgery.current.effect("bead", tip, 0)
				_use_charge(tool)


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
	patient.swab_at(zone, uv, pad.def, dt, "iodine" if soaked else "")
	if soaked:
		tools.set_fill(pad, pad.fill - PAD_DRAIN * dt)
		if (gloved or not pad.sterile) and zone == "site" and not pad.reported.has("dirty"):
			pad.reported["dirty"] = true
			patient.contaminate_site("")
			Surgery.current.scoring.add("dirty_tool")


## Standing (self-retaining) clamps keep holding their grip after the hand lets go.
static func update_standing(tool: SurgicalTool, patient: Patient, dt: float) -> void:
	if not tool.grip_info.is_empty():
		tool.grip_info = patient.update_grip(tool.uid, tool.grip_info, tool.tip_position(), tool.def.power, dt, 0.0)
		if tool.grip_info.type == "none":
			tool.grip_info = {}


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
static func _bloody(tool: SurgicalTool, zone: String, uv: Vector2, patient: Patient, dt: float) -> void:
	var wet := patient.body.blood_at(uv) if zone == "site" else 0.0
	if zone == "cavity" or tool.def.action == "cut" and zone == "site":
		wet = maxf(wet, 0.6)
	if wet > 0.15:
		var rate := 1.2 if tool.def.action == "swab" else 0.5
		Surgery.current.tools.add_blood(tool, wet * rate * dt)


static func _use_charge(tool: SurgicalTool) -> void:
	if tool.charges > 0:
		tool.charges -= 1
		if tool.charges == 0 and tool.def.action in ["inject", "iv_line"]:
			Surgery.current.tools.consume(tool)
