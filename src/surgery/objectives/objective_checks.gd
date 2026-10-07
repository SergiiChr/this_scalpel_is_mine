class_name ObjectiveChecks
extends RefCounted
## One check per objective step type. Add a new type by adding a match branch.
## Step fields come from the scenario file, state is a scratch dictionary that persists between ticks.


static func check(step: Dictionary, state: Dictionary, surgery: Surgery, delta: float) -> bool:
	var patient := surgery.patient
	var v := patient.vitals
	match step.type:
		"sanitize":
			return patient.sanitized_fraction() >= step.get("amount", 0.5)
		"iv":
			return patient.iv_working()
		"anesthesia":
			return v.anesthesia >= step.get("level", 0.7)
		"local_block":
			return v.local_block >= step.get("level", 0.5)
		"mark":
			return patient.body.uv_to_meters(patient.marked_uv) >= step.get("length", 0.1)
		"incise":
			return patient.surgeon_cut_length_m(0.7) >= step.get("length", 0.1)
		"extract":
			var kind: String = step.target
			return patient.targets.filter(func(t: CavityTarget) -> bool: return t.kind == kind).all(func(t: CavityTarget) -> bool: return t.extracted)
		"close":
			return patient.skin_closure() >= Patient.CLOSED_ENOUGH
		"close_internal":
			return patient.internal_closed()
		"stop_bleeding":
			return _held(state, v.bleed_rate <= step.get("max_ml_s", 0.3), 3.0, delta)
		"stabilize":
			var stable := not v.is_arrested() and v.spo2 >= 94.0 and v.systolic >= 90.0
			return _held(state, stable, step.get("seconds", 30.0), delta)
		"calm":
			return _held(state, v.panic < step.get("max_panic", 0.6), 60.0, delta)
		"inject":
			return patient.flags.has("drug_" + step.drug) if step.has("drug") else patient.flags.has(step.flag)
		"defib":
			return patient.flags.has("revived") and not v.is_arrested()
		"tourniquet":
			return patient.tourniquet_on
		"clamp":
			return patient.any_clamped()
		"transfuse":
			return patient.transfused_ml > 0.0 and v.blood_ml >= step.get("min_ml", 4000.0) * v.max_blood_ml / Vitals.NORMAL_BLOOD_ML
		"flip":
			return patient.body.orientation == step.get("orientation", 2)
		"align":
			var holders: Dictionary = {}
			var aligned := true
			for target in patient.targets:
				if target.is_fragment():
					aligned = aligned and target.is_aligned(0.012)
					var tool: SurgicalTool = surgery.tools.tools.get(target.gripped_by)
					if tool:
						holders[tool.holder] = true
			var enough_hands := holders.size() >= mini(2, surgery.surgeons.size())
			return _held(state, aligned and enough_hands, step.get("seconds", 6.0), delta)
		"debride":
			return patient.grid_fraction("debrided") >= step.get("amount", 0.5)
		"graft":
			return patient.grid_fraction("grafted") >= step.get("amount", 0.7)
		"listen":
			return _held(state, patient.flags.has("euthanized"), step.get("seconds", 90.0), delta)
		"comfort":
			return patient.flags.get("comfort_time", 0.0) >= step.get("seconds", 20.0)
		"wait":
			return _held(state, true, step.get("seconds", 30.0), delta)
	push_warning("Unknown objective type '%s'" % step.type)
	return false


## True once condition has held continuously for the given seconds.
static func _held(state: Dictionary, condition: bool, seconds: float, delta: float) -> bool:
	state.timer = state.timer + delta if condition else 0.0
	return state.timer >= seconds
