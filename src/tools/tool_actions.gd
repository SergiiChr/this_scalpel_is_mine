class_name ToolActions
extends RefCounted
## What each tool action does to the patient. Host only, runs every physics frame for held and standing tools.
## Tools share actions: a lighter and a cautery pen both "cauterize", with different ToolDef numbers.

## Skin depth per pressure level (1 light, 2 normal, 3 deep). 0.7+ goes through the skin.
const DEPTH_BY_PRESSURE: Array[float] = [0.0, 0.3, 0.6, 1.0]
const DEFIB_CHARGE_TIME := 2.0


## hand: {"engaged": bool, "pressure": int, "speed": float, "peer": int, "mods": Modifiers}
static func update(tool: SurgicalTool, hand: Dictionary, patient: Patient, dt: float) -> void:
	var engaged: bool = hand.engaged
	var pressed := engaged and not tool.engaged_before
	var released := not engaged and tool.engaged_before
	tool.engaged_before = engaged
	if pressed:
		tool.stroke += 1
	var tip := tool.tip_position()
	var probe := patient.body.probe(tip)
	var zone: String = probe.zone
	var uv: Vector2 = probe.uv
	var touching := zone in ["site", "cavity", "body"]
	if engaged and touching:
		_on_contact(tool, zone, probe, patient)
		_bloody(tool, zone, uv, patient, dt)
	var def := tool.def
	var mods: Modifiers = hand.mods
	match def.action:
		"cut":
			if engaged and zone == "site":
				if tool.last_uv.x >= 0.0 and tool.last_uv.distance_to(uv) > 0.003:
					patient.cut(tool.uid * 1000 + tool.stroke, tool.last_uv, uv, DEPTH_BY_PRESSURE[hand.pressure], def.sharpness, not tool.sterile, hand.speed)
					patient.debride_at(uv)
					Surgery.current.sound("cut_deep" if hand.pressure >= 3 else "cut_skin", tip)
				if tool.last_uv.x < 0.0 or tool.last_uv.distance_to(uv) > 0.003:
					tool.last_uv = uv
			elif engaged and zone == "cavity":
				patient.cut_cavity(uv, probe.depth, def.sharpness, not tool.sterile, dt)
			else:
				tool.last_uv = Vector2(-1, -1)
		"clamp":
			var power := def.power * mods.mult("grip_strength_mult")
			if pressed:
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
			if engaged and zone == "site" and tool.charges != 0:
				if patient.close_at(uv, def, dt, mods.mult("improvised_mult"), hand.pressure):
					tool.charges -= 1 if tool.charges > 0 else 0
					Surgery.current.sound({"skin_stapler": "staple", "office_stapler": "office_staple", "surgical_tape": "tape_rip", "duct_tape": "tape_rip"}.get(def.id, "suture_pull"), tip)
			elif engaged and zone == "cavity":
				patient.close_internal_at(uv, probe.depth, def, dt)
		"cauterize":
			if pressed and def.id == "lighter":
				Surgery.current.sound("lighter_flick", tip)
			if engaged and zone in ["site", "cavity"] and tool.charges != 0:
				patient.cauterize_at(zone, uv, probe.depth, def, dt)
				Surgery.current.effect("smoke", tip, 180)
				if randf() < dt * 1.2:
					Surgery.current.sound("cautery_sizzle", tip)
				if def.id == "lighter" and randf() < dt:
					tool.charges -= 1
		"mark":
			if engaged and zone == "site":
				if tool.last_uv.x >= 0.0:
					patient.mark(tool.last_uv, uv)
				tool.last_uv = uv
			else:
				tool.last_uv = Vector2(-1, -1)
		"inject":
			if pressed and touching and tool.charges != 0:
				if def.iv_only:
					Surgery.current.announce("%s goes on the IV stand, not in the patient." % def.name, true)
				else:
					patient.administer(def.drug, "direct")
					Surgery.current.sound("syringe_inject", tip)
					Surgery.current.effect("bead", tip, 0)
					_use_charge(tool)
		"shock":
			var on_chest: bool = zone == "site" and patient.scenario.site in ["chest", "abdomen"] or probe.get("part", "") == "torso"
			if engaged and on_chest:
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
			elif not engaged:
				tool.charge_time = 0.0
		"saw":
			if engaged and zone in ["site", "cavity"]:
				if patient.saw_at(uv, def, dt):
					Surgery.current.effect("dust", tip, 150)
				elif zone == "site" and tool.last_uv.x >= 0.0 and tool.last_uv.distance_to(uv) > 0.004:
					patient.cut(tool.uid * 1000 + tool.stroke, tool.last_uv, uv, 1.0, 0.3, not tool.sterile, 0.5)
				if randf() < dt * 2.0:
					Surgery.current.sound("saw_bone", tip)
				tool.last_uv = uv
		"smash":
			if pressed and touching:
				patient.smash_at(uv, def)
				if zone in ["site", "cavity"]:
					Surgery.current.effect("spatter", tip, 0)
		"suction":
			if engaged and zone in ["site", "cavity"]:
				patient.suction_at(zone, uv, def, dt)
				if randf() < dt * 1.2:
					Surgery.current.sound("suction_slurp", tip)
				if def.id == "metal_straw":
					Surgery.current.add_sickness(hand.peer, dt * 0.08)
		"swab":
			if engaged and zone in ["site", "cavity"]:
				patient.swab_at(zone, uv, def, dt)
		"tourniquet":
			var limb: bool = str(probe.get("part", "")).begins_with("arm") or str(probe.get("part", "")).begins_with("leg") or zone == "site" and patient.body.is_limb_site()
			if pressed and limb:
				patient.apply_tourniquet()
				Surgery.current.tools.leave_standing(tool)
		"graft":
			if engaged and zone == "site" and tool.charges != 0 and patient.graft_at(uv, def):
				_use_charge(tool)
		"iv_line":
			# Held against an arm, not only on the frame the button went down: the tip may land a moment later.
			var arm: bool = str(probe.get("part", "")).begins_with("arm") or zone == "site" and patient.scenario.site == "forearm"
			if engaged and arm and not patient.iv_set:
				patient.set_iv(tip)
				Surgery.current.effect("bead", tip, 0)
				_use_charge(tool)


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
