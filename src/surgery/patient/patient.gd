class_name Patient
extends Node3D
## Host-authoritative patient simulation.
## Every peer builds the same patient from the session seed. Only the host changes state,
## then broadcasts paint ops (reliable) and vitals/targets/organs (unreliable, 5 Hz).
## Tool code calls the public methods below, and only on the host.

signal died(reason: String)

const TICK := 0.1
const SYNC_INTERVAL := 0.2
const GRID := 16
const SKIN_TONES := Materials.SKIN_TONES
const BLOOD_TYPES: PackedStringArray = ["O+", "O-", "A+", "A-", "B+", "AB+"]
## Seconds of V-fib before it decays to asystole, and total arrest time before death.
const VFIB_TO_ASYSTOLE := 40.0
const ARREST_DEATH := 80.0
const TOURNIQUET_SAFE := 300.0
## Systolic pressure (mmHg) of a patient with a full blood volume, no drugs and no panic.
const NORMAL_PRESSURE := 120.0
## Systolic pressure (mmHg) above which closures leak and fragile vessels may burst.
const HIGH_PRESSURE := 140.0
## Systolic pressure (mmHg) below which the heart may arrest.
const ARREST_PRESSURE := 60.0
## Blood glucose (mmol/l) above which consciousness fades.
const GLUCOSE_HIGH := 20.0
## Blood glucose (mmol/l) below which any patient may seize.
const GLUCOSE_LOW := 3.0
## Core temperature rise (°C per second) during malignant hyperthermia, and the temperature that kills.
const HYPERTHERMIA_RATE := 0.03
const LETHAL_TEMPERATURE := 42.5
## Chance that a dangerous drug combination (DrugDef.danger_with) stops the heart.
const DANGER_ARREST_CHANCE := 0.5
## Total bleeding (ml/s) that frightens an awake patient: a surgical emergency.
const HEAVY_BLEEDING := 1.0
## Seconds after adrenaline in which a shock can restart a flat line.
const RESTART_WINDOW := 60.0
## Systolic rise (mmHg) of a fully panicking patient.
const PANIC_PRESSURE := 30.0
## Fragile vessel bursts per second for each mmHg above HIGH_PRESSURE.
const BURST_CHANCE := 0.005
## Seconds after a burst before the next one can happen.
const BURST_COOLDOWN := 30.0
## Blood (ml) that fills the cavity to the top, and from how much it spills over open wounds onto the skin.
const CAVITY_FULL_ML := 350.0
const CAVITY_SPILL_ML := 280.0
## Closures whose stitch tension follows the pressure level: loose leaks, tight can tear through.
const TENSIONED_CLOSURES: PackedStringArray = ["paper_clips"]
## Stitch rest length per pressure level (1 loose, 2 right, 3 tight), relative to the skin's own springs.
const STITCH_TENSION: Array[float] = [0.95, 1.25, 0.95, 0.8]
## How much load a running thread's spans take before they snap, per layer it's sewn in (by TissueSim.Depth).
const THREAD_STRENGTH: Array[float] = [0.0, 2.0, 2.15, 3.0]
## How far (uv) from a point of a wound its muscle counts as underneath it.
const MUSCLE_REACH := 0.03
## Gap in meters that counts as a fully opened wound.
const FULL_GAP := 0.012
## How much of a local block still dulls deep pain: it numbs the skin and what's under it, not the bone.
const DEEP_BLOCK := 0.3
## Wound depth (0..1) below which a cut is only through the skin, see _tissue_depth().
const SKIN_DEPTH := 0.4
## Pain from the blade scraping bone: a jolt when it first touches, then per second while it grates.
const BONE_JOLT := 0.35
const BONE_PAIN := 0.6
## How close (meters) to the blade's tip a bone has to be for the blade to grate on it, and an organ for it to be cut.
const BLADE_REACH := 0.005
const ORGAN_REACH := 0.001
## How high (meters) forceps lift a piece of skin cut out all round before it comes off whole, as a graft.
const PIECE_LIFT := 0.01
## Where a line set before the surgery goes in: the back of the right hand, in body space.
const PREOP_IV_POINT := Vector3(-0.2, 0.03, 0.26)

var body: PatientBody
var vitals := Vitals.new()
var mods := Modifiers.new()
var rolls: Array = []
var scenario: ScenarioDef
var age := "adult"
## Body weight for drug doses, on the chart. The body model is scaled to match.
var weight_kg := 75.0
var blood_type := "O+"
var wounds: Array[Wound] = []
var targets: Array[CavityTarget] = []
## What's in the patient's blood, every injection of a drug adding up.
var drugs := DrugLevels.new()
## Seconds until a lethal drug that worked ends it (INF: none has).
var _lethal_left := INF
## Counters for scoring and the post-op report, see data/consequences.cfg.
var flags: Dictionary = {}
var iv_set := false
## The catheter went into a vein (set with iv_set). Missed, the line still sticks but nothing runs through it.
var iv_in_vein := false
var tourniquet_on := false
var tourniquet_time := 0.0
var cavity_blood_ml := 0.0
var transfused_ml := 0.0
var marked_uv := 0.0
var alive := true
var rng := RandomNumberGenerator.new()

var _sanitized := PackedFloat32Array()
var _burn_cells := PackedByteArray()
var _debrided := PackedByteArray()
var _grafted := PackedByteArray()
var _arrest_time := 0.0
var _restart_window := 0.0
var _seizure_left := 0.0
var _reassure := 0.0
var _mh_active := false
var _next_wound_id := 1
var _paint_seed := 0
var _tick_acc := 0.0
var _sync_acc := 0.0
var _voice_cooldown := 0.0
var _breath_cooldown := 0.0
var _burst_cooldown := 0.0
var _stroke_wounds: Dictionary = {}
## When (seconds) a blade last grated on a bone, so touching it again after a pause hurts with a jolt again.
var _bone_touched := -INF
var _initial_suction: float = 1.0
var _organ_strain: Dictionary = {}
var _organ_damage: Dictionary = {}
var _tear_notice_msec := -100000
## Running sutures (host), thread id -> {"before": wound id -> {"skin" and "muscle": bin -> the wound's closure there
## before the thread held it}}.
var _sutures: Dictionary = {}
var _next_suture := 0


func _ready() -> void:
	body = PatientBody.new()
	body.name = "Body"
	add_child(body)
	_burn_cells.resize(GRID * GRID)
	_debrided.resize(GRID * GRID)
	_grafted.resize(GRID * GRID)
	_sanitized.resize(GRID * GRID)


## Runs on every peer with identical inputs, so everyone ends up with the same patient.
func setup(scenario_def: ScenarioDef, patient_rolls: Array, seed_value: int) -> void:
	scenario = scenario_def
	rolls = patient_rolls
	rng.seed = seed_value
	age = scenario.patient_age
	mods = Modifiers.from_rolls(rolls, Db.patient_quirks)
	var tone: Color = SKIN_TONES[rng.randi_range(0, SKIN_TONES.size() - 1)]
	body.fat_thickness = float(Db.patient_sites.get(scenario.site, {}).get("fat", PatientBody.FAT)) * (1.0 + mods.num("fat_depth") * 1.5)
	body.tissue.break_mult = mods.mult("tear_threshold_mult")
	body.tissue.tearing = multiplayer.is_server()
	body.build(scenario.site, tone, _roll_weight(seed_value))
	if scenario.environment == "or":
		body.add_drape()
	body.set_orientation(scenario.start_orientation)
	blood_type = "Bombay" if mods.flag("rare_blood") else BLOOD_TYPES[rng.randi_range(0, BLOOD_TYPES.size() - 1)]

	var volume := Vitals.NORMAL_BLOOD_ML * mods.mult("blood_ml_mult") * (0.5 if age == "child" else 1.0)
	vitals.max_blood_ml = volume
	vitals.blood_ml = volume
	vitals.spo2 += mods.num("spo2_offset")
	vitals.glucose += mods.num("glucose_drift") * 300.0
	vitals.from_dict(scenario.start_vitals)
	iv_set = scenario.preop.get("iv", false)
	iv_in_vein = iv_set
	if iv_set:
		_connect_iv(PREOP_IV_POINT)

	for data: Dictionary in scenario.wounds:
		var points: Array = data.points
		var wound := _new_wound(Wound.Kind.get(str(data.kind).to_upper(), Wound.Kind.CUT), _uv(points[0]), data.get("depth", 0.5))
		for p: Array in points.slice(1):
			wound.extend(_uv(p))
		wound.held = data.get("held", 0.0)
		_paint_wound_local(wound)
	for data: Dictionary in scenario.burns:
		_add_burn_local(_uv(data.uv), data.radius)
	for data: Dictionary in scenario.internal:
		var wound := _new_wound(Wound.Kind.INTERNAL, _uv(data.uv), 0.8)
		wound.depth_m = data.get("depth", 0.05)
	for i in scenario.targets.size():
		var target := CavityTarget.new()
		target.setup(i, scenario.targets[i], mods.flag("mirrored"))
		body.site.add_child(target)
		target.position = _site_local(target.uv, target.depth)
		targets.append(target)
		if target.covered:
			body.add_organ(target.uv + Vector2(0.03, -0.02), target.depth - 0.03, 0.04, Color(0.6, 0.35, 0.3))
	_initial_suction = maxf(targets.filter(func(t: CavityTarget) -> bool: return t.is_suction_target()).reduce(func(acc: float, t: CavityTarget) -> float: return acc + t.amount, 0.0), 1.0)
	if Db.patient_sites.get(scenario.site, {}).has("anatomy"):
		var avoid: Array[Vector2] = []
		var skip := PackedStringArray()
		for target in targets:
			if not target.covered:
				avoid.append(target.rest_uv)
			skip.append(target.kind)
		body.build_anatomy(avoid, skip)
	elif body.cavity_depth() > 0.1:
		for i in 3:
			body.add_organ(Vector2(rng.randf_range(0.2, 0.8), rng.randf_range(0.2, 0.8)), 0.06, rng.randf_range(0.03, 0.045), Color(0.55, 0.2, 0.2).lerp(Color(0.7, 0.5, 0.4), rng.randf()))
	if scenario.dirty_start:
		body.wound_map.disk(WoundMap.Layer.FLUIDS, WoundMap.GRIME, Vector2(0.5, 0.5), 0.6, 0.4, WoundMap.Mode.MAX)
	var anesthesia: float = scenario.preop.get("anesthesia", 0.0)
	if anesthesia > 0.0:
		# Already under when the surgery starts: in the blood and working, nobody gave it here.
		var propofol := Db.drug("propofol")
		drugs.give(propofol, 0.0, 0.1)
		drugs.entries[propofol.id].merge({"level": 1.0, "working": 0.0}, true)


func _physics_process(delta: float) -> void:
	if not multiplayer.is_server() or not alive or not Surgery.current or not Surgery.current.running:
		return
	body.settle_organs(delta)
	_handle_organs(delta)
	var tripped: Surgeon = Surgery.current.room.iv_line.tripped_by(Surgery.current.surgeons.values())
	if tripped:
		pull_iv(tripped)
	_tick_acc += delta
	while _tick_acc >= TICK:
		_tick_acc -= TICK
		_simulate(TICK)
	_sync_acc += delta
	if _sync_acc >= SYNC_INTERVAL:
		_sync_acc = 0.0
		_sync.rpc(vitals.to_dict(), targets.map(func(t: CavityTarget) -> Array: return t.state()), body.organ_states(), body.tissue.grips(), cavity_blood_ml, body.blood.sources)


# --- Simulation ------------------------------------------------------------------------------------


func _simulate(dt: float) -> void:
	var v := vitals
	var fx := _drug_effects(dt)
	var site_m := body.uv_to_meters(1.0)
	var bleed_mult := mods.mult("bleed_mult") * clampf(1.0 - fx.clot, 0.2, 2.0)
	if tourniquet_on and body.is_limb_site():
		bleed_mult *= 0.1
	var total := 0.0
	var heal := mods.num("heal_rate")
	var sources: Array = []
	var leak := _closure_leak(fx)
	for wound in wounds:
		if not wound.is_internal():
			wound.opened = clampf(body.tissue.gap_along(wound.points, 0.03, TissueSim.Depth.SKIN) / FULL_GAP, 0.0, 1.0)
		var rate := wound.bleed_rate(site_m, bleed_mult, leak)
		wound.bleeding = rate
		total += rate
		# An open wound fills the cavity first; once that is nearly full it spills over the edges onto the skin.
		var spills := not wound.is_internal() and cavity_blood_ml > CAVITY_SPILL_ML
		if wound.is_internal() or wound.opened > 0.3:
			cavity_blood_ml += rate * dt * 0.6
		if rate > 0.05 and (spills or not (wound.is_internal() or wound.opened > 0.3)):
			sources.append([wound.midpoint(), rate])
			if rng.randf() < dt * 0.5:
				Surgery.current.sound("blood_drip", body.uv_to_world(wound.midpoint()))
		if not wound.made_by_surgeon or wound.kind != Wound.Kind.CUT:
			wound.held = maxf(wound.held - dt * 0.004, 0.0)
		if heal > 0.0:
			for i in wound.bins.size():
				var before := wound.bins[i]
				wound.bins[i] = minf(before + heal * dt, 1.0)
				if before < 1.0 and wound.bins[i] >= 1.0 and not wound.is_internal():
					_tissue_stitch.rpc(wound.bin_position(i), STITCH_TENSION[0], TissueSim.TISSUE_BREAK)
	v.bleed_rate = total
	# The worst few external bleeds run as fluid on every peer (BloodFlow); the rest is too little to see.
	sources.sort_custom(func(a: Array, b: Array) -> bool: return a[1] > b[1])
	body.blood.sources = sources.slice(0, 6)
	v.blood_ml = clampf(v.blood_ml - total * dt + fx.volume_ml * dt, 0.0, v.max_blood_ml * 1.1)
	cavity_blood_ml = maxf(cavity_blood_ml, 0.0)
	body.set_cavity_blood(cavity_blood_ml / CAVITY_FULL_ML)

	v.anesthesia = clampf(fx.anesthesia * mods.mult("sedation_mult"), 0.0, 1.0)
	v.local_block = clampf(fx.local_block, 0.0, 1.0)
	v.glucose += (mods.num("glucose_drift") * (1.0 + v.panic) + fx.glucose) * dt
	v.pain = clampf(v.pain - dt * 0.05 - fx.pain_relief * dt * 0.2, 0.0, 1.0)
	v.swelling = maxf(v.swelling - dt * (0.004 + fx.antihistamine * 0.02 + fx.adrenaline * 0.05), 0.0)
	var shock := clampf((0.6 - v.blood_ratio()) / 0.3, 0.0, 1.0)
	var glucose_coma := clampf((v.glucose - GLUCOSE_HIGH) / 10.0, 0.0, 1.0) + clampf((2.5 - v.glucose) / 1.5, 0.0, 1.0)
	v.consciousness = 0.0 if v.is_arrested() else clampf(1.0 - maxf(v.anesthesia, fx.sedation * 0.8) - shock - glucose_coma, 0.0, 1.0)

	if _reassure > 0.0:
		add_flag("comfort_time", dt)
	_reassure = maxf(_reassure - dt, 0.0)
	if v.is_awake():
		var calming := 0.06 + fx.sedation * 0.3 + (0.25 if _reassure > 0.0 else 0.0)
		v.panic = clampf(v.panic + (v.pain * 0.12 + (0.03 if total > HEAVY_BLEEDING else 0.0) - calming) * mods.mult("panic_mult") * dt * 2.0, 0.0, 1.0)
	else:
		v.panic = maxf(v.panic - dt * 0.2, 0.0)

	var ratio := v.blood_ratio()
	var awake_factor := 1.0 if v.is_awake() else 0.2
	var target_hr := 72.0 + (1.0 - ratio) * 140.0 + v.pain * 35.0 * awake_factor + v.panic * 40.0 + fx.hr + (v.temperature - 37.0) * 10.0
	var suction_left: float = targets.filter(func(t: CavityTarget) -> bool: return t.is_suction_target() and not t.extracted).reduce(func(acc: float, t: CavityTarget) -> float: return acc + t.amount, 0.0)
	var target_spo2 := 98.0 + mods.num("spo2_offset") + fx.spo2 - (1.0 - ratio) * 30.0 - v.swelling * 15.0 - suction_left / _initial_suction * 16.0
	match v.rhythm:
		Vitals.Rhythm.SINUS:
			v.heart_rate = lerpf(v.heart_rate, target_hr, dt * 0.6) + rng.randf_range(-0.6, 0.6)
			v.systolic = lerpf(v.systolic, NORMAL_PRESSURE * pow(ratio, 1.6) + fx.bp + v.panic * PANIC_PRESSURE - v.swelling * 50.0, dt * 0.6)
			v.spo2 = clampf(lerpf(v.spo2, target_spo2, dt * 0.3), 50.0, 100.0)
			_arrest_time = 0.0
			_roll_arrest(dt, fx)
		Vitals.Rhythm.VFIB:
			v.heart_rate = rng.randf_range(180.0, 300.0)
			v.systolic = lerpf(v.systolic, 15.0, dt)
			_arrested(dt)
		Vitals.Rhythm.ASYSTOLE:
			v.heart_rate = 0.0
			v.systolic = lerpf(v.systolic, 0.0, dt)
			_arrested(dt)
	if v.rhythm != Vitals.Rhythm.SINUS:
		v.spo2 = maxf(v.spo2 - dt * 1.5, 40.0)

	if mods.flag("mh_trigger") and _has_active("mh_trigger") and not _has_active("mh_cure"):
		_mh_active = true
	if _has_active("mh_cure"):
		_mh_active = false
	var baseline := 35.4 if Surgery.current.run_mods.flag("cold") else 36.8
	v.temperature += ((HYPERTHERMIA_RATE if _mh_active else 0.0) + (baseline - v.temperature) * 0.01 + fx.temp * 0.01) * dt

	_restart_window = maxf(_restart_window - dt, 0.0)
	_update_fragile_vessels(dt)
	_update_seizure(dt)
	_update_misc(dt)
	_check_death(fx)


## Organs held out of place too long, or shoved hard, bruise and start bleeding.
func _handle_organs(delta: float) -> void:
	for i in body.organs.size():
		var organ := body.organs[i]
		var strain: float = _organ_strain.get(i, 0.0)
		if body.organ_offset(i) > 0.035:
			strain += delta
		if organ.linear_velocity.length() > 0.5:
			strain += delta * 4.0
		strain = maxf(strain - delta * 0.3, 0.0)
		if strain > 5.0:
			strain = 0.0
			_organ_damage[i] = minf(_organ_damage.get(i, 0.0) + 0.35, 1.0)
			_set_organ_damage.rpc(i, _organ_damage[i])
			var uv := Vector2(organ.position.x / body.site_size.x + 0.5, organ.position.z / body.site_size.y + 0.5)
			var wound := _new_wound(Wound.Kind.INTERNAL, uv, 0.4)
			wound.depth_m = -organ.position.y
			Surgery.current.scoring.add("organ_bruise")
			Surgery.current.announce("You've been manhandling an organ. It's bruising and oozing.", true)
		_organ_strain[i] = strain


func _roll_arrest(dt: float, fx: DrugEffects) -> void:
	var v := vitals
	var risk := 0.0
	risk += maxf(0.55 - v.blood_ratio(), 0.0) * 0.05
	risk += 0.01 if v.systolic < ARREST_PRESSURE else 0.0
	risk += 0.004 if v.heart_rate > 170.0 else 0.0
	risk += 0.01 if v.temperature > 40.5 else 0.0
	risk += 0.004 if v.glucose < 2.0 else 0.0
	risk += 0.02 if v.swelling > 0.7 else 0.0
	risk += mods.num("clot_risk") * (0.0 if fx.clot < -0.2 else 1.0)
	if rng.randf() < risk * mods.mult("arrest_mult") * dt:
		arrest()


func _arrested(dt: float) -> void:
	_arrest_time += dt
	if vitals.rhythm == Vitals.Rhythm.VFIB and _arrest_time > VFIB_TO_ASYSTOLE:
		vitals.rhythm = Vitals.Rhythm.ASYSTOLE


## How much blood gets through closures and packing (see Wound.bleed_rate): heparin, or pressure above HIGH_PRESSURE.
func _closure_leak(fx: DrugEffects) -> float:
	var thinned := clampf(-fx.clot, 0.0, 1.0) * 0.5
	var pressure := clampf((vitals.systolic - HIGH_PRESSURE) / 40.0, 0.0, 0.5)
	return minf(thinned + pressure, 0.6)


## Fragile vessels (an aneurysm) burst under pressure above HIGH_PRESSURE: a deep vessel under the site gives way.
func _update_fragile_vessels(dt: float) -> void:
	if not mods.flag("fragile_vessels"):
		return
	_burst_cooldown = maxf(_burst_cooldown - dt, 0.0)
	var excess := vitals.systolic - HIGH_PRESSURE
	if excess <= 0.0 or _burst_cooldown > 0.0 or rng.randf() >= excess * BURST_CHANCE * dt:
		return
	_burst_cooldown = BURST_COOLDOWN
	var uv := Vector2(rng.randf_range(0.3, 0.7), rng.randf_range(0.3, 0.7))
	var wound := _new_wound(Wound.Kind.INTERNAL, uv, 0.7)
	wound.depth_m = minf(0.04, body.cavity_depth() * 0.6)
	Surgery.current.sound("blood_spurt", body.uv_to_world(uv))
	Surgery.current.announce("A vessel gives way!")
	for roll: Dictionary in rolls:
		if (Db.patient_quirks[roll.id] as QuirkDef).effects(roll.variant).has("fragile_vessels"):
			_reveal(roll.id)


func _update_seizure(dt: float) -> void:
	var chance := mods.num("seizure_chance") + (0.02 if vitals.glucose < GLUCOSE_LOW else 0.0)
	if _seizure_left > 0.0:
		_seizure_left -= dt
		vitals.seizing = _seizure_left > 0.0 and not _has_active("anticonvulsant")
		if vitals.seizing and rng.randf() < dt * 1.5:
			Surgery.current.jolt_all(0.6, "The patient convulses!")
			_strain_closures(0.3)
	elif not _has_active("anticonvulsant") and rng.randf() < chance * dt:
		start_seizure()


func _update_misc(dt: float) -> void:
	if tourniquet_on:
		tourniquet_time += dt
		if tourniquet_time > TOURNIQUET_SAFE and not flags.has("tourniquet_overtime"):
			add_flag("tourniquet_overtime")
			Surgery.current.announce("The tourniquet has been on too long.")
	for target in targets:
		if target.is_suction_target() and not target.extracted and target.refill > 0.0:
			target.amount += target.refill * dt
	if vitals.is_awake() and mods.num("cough_chance") > 0.0 and rng.randf() < mods.num("cough_chance") * dt:
		Surgery.current.jolt_all(0.3, "The patient coughs violently.")
		_strain_closures(0.15)
	_breath_cooldown -= dt
	if vitals.is_awake() and vitals.panic > 0.6 and _breath_cooldown <= 0.0:
		_breath_cooldown = 2.0
		_vocal.rpc("patient_breath")
	_voice_cooldown -= dt
	if vitals.is_awake() and _voice_cooldown <= 0.0:
		_ambient_voice()


func _check_death(fx: DrugEffects) -> void:
	var v := vitals
	var reason := ""
	if v.blood_ml < v.max_blood_ml * 0.3:
		reason = "Bled out."
	elif _arrest_time > ARREST_DEATH:
		reason = "Cardiac arrest."
	elif v.temperature > LETHAL_TEMPERATURE:
		reason = "Malignant hyperthermia."
	elif fx.lethal > 0.95:
		reason = "Passed away peacefully."
	if reason:
		alive = false
		v.rhythm = Vitals.Rhythm.ASYSTOLE
		v.heart_rate = 0.0
		died.emit(reason)


# --- Drugs -----------------------------------------------------------------------------------------


func _drug_effects(dt: float) -> DrugEffects:
	var working: Array[DrugDef] = []
	for crossed: Array in drugs.update(dt, _wear):
		if crossed[1] == "works":
			_drug_works(crossed[0], working)
			working.append(crossed[0])
		else:
			add_flag("overdose")
			Surgery.current.scoring.add("overdose")
	var fx := DrugEffects.new()
	var potency := Surgery.current.run_mods.mult("drug_strength_mult") if Surgery.current else 1.0
	for entry: Dictionary in drugs.entries.values():
		var def: DrugDef = entry.def
		var strength := DrugDef.dose_strength(entry.level) * potency
		for key: String in def.effects:
			if key in ["glucose", "volume_ml"]:
				# Totals, spread over the time it takes to wear off, as fast as it does: two doses give twice as much.
				fx.add(key, def.effect(key) / maxf(def.duration, 1.0) * _wear(def, entry.level) if entry.level > 0.0 else 0.0)
			else:
				fx.add(key, def.effect(key) * strength)
		if def.has_flag("antihistamine"):
			fx.antihistamine += strength
		if def.id == "adrenaline":
			fx.adrenaline += strength
	if _lethal_left != INF:
		_lethal_left -= dt
		fx.lethal = 1.0 if _lethal_left <= 0.0 else 0.0
	return fx


## How fast a drug at `level` wears off (1: one right dose over its duration). General anesthesia lasts the whole
## surgery, kept topped up to the right dose like an anesthetist would: more than that wears off as usual, so another
## dose deepens it only for a while. A patient who burns through it (anesthesia_decay_mult) loses it all.
func _wear(def: DrugDef, level: float) -> float:
	if def.effect("anesthesia") <= 0.0:
		return 1.0
	var decay_mult := mods.mult("anesthesia_decay_mult")
	if decay_mult > 1.0:
		return decay_mult
	return 1.0 if level > 1.0 else 0.0


func _has_active(flag: String) -> bool:
	return drugs.working(flag)


## Says so when there's no line to give anything through, or it isn't in a vein.
func iv_ready() -> bool:
	if not iv_set:
		Surgery.current.announce("Nothing happens. There's no IV line in.")
	elif not iv_in_vein:
		Surgery.current.announce("Nothing goes in. The IV line missed the vein.")
	return iv_working()


## A line is in and in a vein: drugs and fluids run through it.
func iv_working() -> bool:
	return iv_set and iv_in_vein


## route: "iv" (smooth, needs a line), "vein" (a syringe straight into a vein: like "iv", no line needed)
## or "direct" (into tissue: soaks in faster).
## amount: how much was given in the drug's unit (see DrugDef.dose). Negative means just the right dose (bags, masks).
## Every injection adds to what's already in (DrugLevels): ten small ones work like one big one.
func administer(drug_id: String, route: String, amount: float = -1.0) -> void:
	var def := Db.drug(drug_id)
	if def == null:
		return
	if route == "iv" and not iv_ready():
		return
	var share := amount / (def.dose * weight_kg) if amount >= 0.0 and def.dose > 0.0 else 1.0
	if def.blood_type:
		transfused_ml += def.effect("volume_ml")
		if not _blood_compatible(def.blood_type):
			add_flag("wrong_blood")
			vitals.swelling = minf(vitals.swelling + 0.3, 1.0)
			vitals.temperature += 1.0
			Surgery.current.scoring.add("wrong_blood")
			Surgery.current.announce("Fever and shaking. Transfusion reaction!")
	if drug_id == "whiskey":
		add_flag("whiskey_given")
		if mods.flag("whiskey_friendly"):
			def = Db.drug("diazepam")
			share *= 0.5
	var fresh := drugs.give(def, share, def.onset * (DrugDef.DIRECT_ONSET if route == "direct" else 1.5))
	if fresh and drug_id in mods.list("allergen"):
		vitals.swelling = minf(vitals.swelling + 0.6, 1.0)
		vitals.systolic -= 30.0
		Surgery.current.scoring.add("allergic_reaction")
		Surgery.current.announce("Hives spread across the skin. Allergic reaction!")
		_reveal("allergy")


## A drug just reached an effective level: it does its job. `along` started working in the same step, before it: a
## dangerous pair of them reacts once, not once for each.
func _drug_works(def: DrugDef, along: Array[DrugDef]) -> void:
	for entry: Dictionary in drugs.entries.values():
		var other: DrugDef = entry.def
		if other != def and not other in along and entry.working >= 0.0 and (def.id in other.danger_with or other.id in def.danger_with):
			Surgery.current.announce("Blood pressure spikes through the roof!")
			if rng.randf() < DANGER_ARREST_CHANCE:
				arrest()
	add_flag("drug_" + def.id)
	if def.has_flag("restart"):
		_restart_window = RESTART_WINDOW
		vitals.swelling = maxf(vitals.swelling - 0.4, 0.0)
	if def.has_flag("reverse_opioid"):
		drugs.remove(func(d: DrugDef) -> bool: return d.has_flag("opioid"))
	if def.has_flag("reverse_benzo"):
		drugs.remove(func(d: DrugDef) -> bool: return d.has_flag("benzo"))
	if def.has_flag("anticonvulsant"):
		_seizure_left = 0.0
		vitals.seizing = false
	if def.has_flag("antibiotic"):
		add_flag("antibiotic")
	if def.has_flag("lethal"):
		add_flag("euthanized")
		# There's no coming back from it, however fast it wears off.
		_lethal_left = minf(_lethal_left, def.duration * 0.8)


## Rolls the weight on its own generator (so the rest of the patient stays the same) and returns the body scale for it.
## Size follows the cube root of weight: twice as heavy is about a quarter bigger. Heavy build quirks add weight.
func _roll_weight(seed_value: int) -> float:
	var weight_rng := RandomNumberGenerator.new()
	weight_rng.seed = seed_value + 3
	var ranges := {"child": [18.0, 34.0, 26.0, 0.72], "elderly": [45.0, 85.0, 68.0, 0.96]}
	var r: Array = ranges.get(age, [55.0, 105.0, 75.0, 1.0])
	weight_kg = roundf(weight_rng.randf_range(r[0], r[1]) * (1.0 + mods.num("fat_depth") * 0.3))
	return float(r[3]) * pow(weight_kg / float(r[2]), 1.0 / 3.0)


func _blood_compatible(pack: String) -> bool:
	if blood_type == "Bombay":
		return pack == "Bombay"
	return pack == "O-" or pack == blood_type or (pack == "A+" and blood_type == "AB+") or (pack == "B+" and blood_type == "AB+")


# --- Events ----------------------------------------------------------------------------------------


func arrest() -> void:
	if vitals.rhythm != Vitals.Rhythm.SINUS or not alive:
		return
	vitals.rhythm = Vitals.Rhythm.VFIB
	Surgery.current.scoring.add("arrest")
	Surgery.current.announce("V-fib! The heart has stopped pumping.")
	_reveal("heart")


func start_seizure() -> void:
	if _has_active("anticonvulsant"):
		return
	_seizure_left = rng.randf_range(6.0, 10.0)
	vitals.seizing = true
	Surgery.current.announce("Seizure!")
	_reveal("epilepsy")


func wake_up() -> void:
	drugs.remove(func(d: DrugDef) -> bool: return d.effect("anesthesia") > 0.0)
	_speak("wake_up")


func shock(power: float) -> void:
	if mods.flag("pacemaker") and scenario.site in ["chest", "abdomen"]:
		vitals.heart_rate += rng.randf_range(-30.0, 40.0)
		_reveal("pacemaker")
	hurt(0.8)
	match vitals.rhythm:
		Vitals.Rhythm.VFIB:
			var chance := 0.45 * power + (0.3 if _has_active("antiarrhythmic") else 0.0)
			if rng.randf() < chance:
				_revive()
		Vitals.Rhythm.ASYSTOLE:
			if _restart_window > 0.0 and rng.randf() < 0.4 * power:
				_revive()
		Vitals.Rhythm.SINUS:
			if rng.randf() < 0.25:
				Surgery.current.announce("You shocked a beating heart.")
				arrest()


func _revive() -> void:
	vitals.rhythm = Vitals.Rhythm.SINUS
	vitals.heart_rate = 110.0
	vitals.systolic = maxf(vitals.systolic, 70.0)
	add_flag("revived")
	Surgery.current.scoring.add("revived")
	Surgery.current.announce("Sinus rhythm. They're back.")


func reassure() -> void:
	if vitals.is_awake():
		if _reassure <= 0.0:
			Surgery.current.scoring.add("reassured", true)
			_speak("reassured")
		_reassure = 8.0


func heavy_drop(at: Vector3) -> void:
	if mods.flag("bone_fragile") and body.part_at(at, 0.1):
		bruise(body.world_to_uv(at).clamp(Vector2.ZERO, Vector2.ONE), 0.08, 0.8)
		hurt(0.7)
		add_flag("fracture")
		Surgery.current.sound("bone_crack", at)
		Surgery.current.announce("Something cracked under that. A fragile bone broke!")
		_reveal("bones")


func turn_over(to: int, fell: bool) -> void:
	_set_orientation.rpc(to)
	if fell:
		add_flag("fell")
		hurt(1.0)
		vitals.blood_ml -= 150.0
		_strain_closures(1.0)


## Seizures, coughs and falls pull on closures. Weak ones (office staples, tape) can burst.
func _strain_closures(strength: float) -> void:
	for wound in wounds:
		if wound.closure() > 0.0 and rng.randf() < strength * (1.0 - wound.closure_quality):
			for i in wound.bins.size():
				wound.bins[i] *= 0.4
			_tissue_burst.rpc(wound.midpoint(), wound.length_uv() * 0.5 + 0.03)
			Surgery.current.announce("A closure bursts open!")
			_paint_wound(wound)


# --- Damage and treatment (host only, called by tool actions) ---------------------------------------


func add_flag(key: String, amount: float = 1.0) -> void:
	flags[key] = flags.get(key, 0.0) + amount


## Pain at a spot on the site (uv) is dulled by a local block; deep pain (the blade on a bone) mostly gets through it.
func hurt(amount: float, uv: Vector2 = Vector2(-1, -1), deep: bool = false) -> void:
	var numb := vitals.anesthesia
	if uv.x >= 0.0:
		numb = maxf(numb, vitals.local_block * (DEEP_BLOCK if deep else 1.0))
	vitals.pain = clampf(vitals.pain + amount * (1.0 - numb) * mods.mult("pain_mult"), 0.0, 1.0)
	if vitals.is_awake() and amount * (1.0 - numb) > 0.15:
		_speak("pain")


## A needle dragged out sideways from `from` to `to` (world space): a short scratch that bleeds a little and hurts.
func needle_tear(from: Vector3, to: Vector3) -> void:
	var probe := body.probe(to)
	var uv: Vector2 = probe.uv
	if probe.zone == "site":
		paint(WoundMap.Layer.WOUNDS, WoundMap.CUT, body.world_to_uv(from), uv, 0.003, 0.3, WoundMap.Mode.MAX)
		paint(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, uv, uv, 0.01, 0.5, WoundMap.Mode.MAX)
	hurt(0.15, uv if probe.zone == "site" else Vector2(-1, -1))
	Surgery.current.effect("bead", from, 0)
	Surgery.current.sound("cut_skin", to)


## Touch outside the treated area. Ticklish patients flinch.
func touch(_part: String) -> void:
	if mods.flag("ticklish") and vitals.is_awake() and rng.randf() < 0.05:
		Surgery.current.jolt_all(0.25, "")
		_speak("tickle", true)
		_reveal("ticklish")


## One continuous scalpel stroke per tool grows one wound.
## Where there's no fat under the skin (a forearm), a cut deeper than the skin goes into the muscle.
func cut(stroke_key: int, a: Vector2, b: Vector2, depth: float, sharpness: float, dirty: bool, speed: float) -> void:
	depth = clampf(depth - mods.num("fat_depth") * 0.25, 0.1, 1.0)
	if body.fat_thickness < 0.0005 and depth >= SKIN_DEPTH:
		depth = maxf(depth, Wound.MUSCLE_DEPTH)
	var wound: Wound = _stroke_wounds.get(stroke_key)
	if wound == null or wound.points[wound.points.size() - 1].distance_to(a) > 0.02:
		wound = _new_wound(Wound.Kind.CUT, a, depth)
		wound.made_by_surgeon = true
		_stroke_wounds[stroke_key] = wound
	wound.extend(b)
	wound.depth = maxf(wound.depth, depth)
	wound.dirty = wound.dirty or dirty
	var jagged := speed > 0.25 or sharpness < 0.8
	var jitter := 0.004 * (1.0 - sharpness) + (0.003 if speed > 0.25 else 0.0)
	paint(WoundMap.Layer.WOUNDS, WoundMap.CUT, a, b, 0.004 + depth * 0.003, minf(depth * 0.75, 0.7), WoundMap.Mode.MAX, jitter)
	_tissue_cut.rpc(a, b, _tissue_depth(depth))
	hurt(0.12 * depth, a)
	if dirty:
		_contaminate()
	Surgery.current.scoring.add("jagged_cut" if jagged else "clean_cut", true)


## Cutting inside the body frees attached targets, or nicks whatever the blade touches. The blade grates on a bone it
## touches, which hurts through a local block.
func cut_cavity(tip_uv: Vector2, depth_m: float, power: float, dirty: bool, dt: float) -> void:
	if dirty:
		_contaminate()
	var tip := body.uv_to_world(tip_uv, depth_m)
	if scrape_bone(tip_uv, tip, dt):
		return
	for target in targets:
		if not target.extracted and target.anchor > 0.0 and target.uv.distance_to(tip_uv) < 0.06:
			target.anchor = maxf(target.anchor - power * dt * 0.5, 0.0)
			if not target.has_meta("opened"):
				target.set_meta("opened", true)
				var wound := _new_wound(Wound.Kind.INTERNAL, target.uv, 0.6)
				wound.depth_m = target.depth
			return
	if body.organ_at(tip, ORGAN_REACH) >= 0 and rng.randf() < dt * 0.8:
		var wound := _new_wound(Wound.Kind.INTERNAL, tip_uv, 0.5)
		wound.depth_m = depth_m
		wound.made_by_surgeon = true
		Surgery.current.scoring.add("organ_nick", true)
		Surgery.current.announce("That wasn't the target. Something is bleeding in there.")


## A blade touching a bone at tip (world space) grates on it, which hurts through a local block. False if there's no
## bone there.
func scrape_bone(tip_uv: Vector2, tip: Vector3, dt: float) -> bool:
	var bone := body.bone_at(tip, BLADE_REACH)
	if bone.is_empty():
		return false
	var now := Time.get_ticks_msec() * 0.001
	if now - _bone_touched > 0.5:
		hurt(BONE_JOLT, tip_uv, true)
	_bone_touched = now
	hurt(BONE_PAIN * dt, tip_uv, true)
	add_flag("bone_scraped", dt)
	if not flags.has("bone_notice"):
		add_flag("bone_notice")
		Surgery.current.announce("The blade grates on %s." % {"rib": "a rib", "sternum": "the breastbone"}.get(bone, "bone"))
	if rng.randf() < dt * 2.0:
		Surgery.current.sound("saw_bone", tip)
	return true


func tear(from: Vector2, direction: Vector2, length_uv: float) -> void:
	var to := from + direction.normalized() * length_uv
	_tissue_cut.rpc(from, to, TissueSim.Depth.FAT)
	_add_tear(from, to)


func _add_tear(from: Vector2, to: Vector2) -> void:
	var wound := _new_wound(Wound.Kind.TEAR, from, 0.8)
	wound.extend(to)
	_paint_wound(wound)
	hurt(0.5, from)
	add_flag("tears")
	Surgery.current.scoring.add("skin_tear")
	Surgery.current.sound("tear_skin", body.uv_to_world(from))
	_reveal("thin_skin")


## A new running thread for the needle: its id for place_suture_anchor() and the rest.
func new_suture() -> int:
	_next_suture += 1
	return _next_suture


## The tissue layer a thread started at uv goes through: the skin from a hole in it beside the wound, the deepest
## layer still open from a hole inside the wound's opening or on its line (within half a grid cell), and the muscle
## under a stab or bullet hole too small to reach into (the needle goes in through the hole and sews it first).
func suture_layer_at(uv: Vector2, wound: Wound) -> int:
	var line := wound.closest_point(uv)
	var at := wound.bin_position(wound.bin_at(uv))
	var hole := wound.kind in [Wound.Kind.PUNCTURE, Wound.Kind.GUNSHOT] and not body.is_open(at)
	var inside := body.layer_at(uv) != "skin" or uv.distance_to(line) < _grid_cell(uv - line) * 0.5
	if not inside and not hole:
		return TissueSim.Depth.SKIN
	if body.tissue.muscle_open_near(at, MUSCLE_REACH):
		return TissueSim.Depth.MUSCLE
	if not hole and body.tissue.fat_open_near(at, MUSCLE_REACH):
		return TissueSim.Depth.FAT
	return TissueSim.Depth.SKIN


## One click with the needle makes one puncture in a continuous thread. The first click, beside a wound, only anchors
## it in the layer under it (suture_layer_at()); later ones pass the same thread through the same layer at the new
## hole, making spans that are all pulled from the free end. The thread closes every wound it crosses. Returns false
## when no hole was made (no wound near the first, the same hole again, a thread already tied off or torn).
func place_suture_anchor(thread_id: int, uv: Vector2, tension: float) -> bool:
	var info := body.tissue.thread_info(thread_id)
	if not info.is_empty() and bool(info.final):
		return false
	# Holes sit beside the incision, not on its line: a bite reaches four grid cells (about 2.5 cm) from it. In grid
	# cells, not uv: a narrow site like a forearm has few cells across, each wide in uv.
	var wound := _nearest_wound(uv, 4.0 / mini(body.tissue.res_x, body.tissue.res_y), false)
	if wound == null and info.is_empty():
		return false
	var layer: int = info.layer if not info.is_empty() else suture_layer_at(uv, wound)
	if wound:
		uv = _hole_uv(uv, wound)
	var before := body.tissue.thread_uvs(thread_id).size()
	_suture_anchor.rpc(thread_id, uv, layer, tension, THREAD_STRENGTH[layer], body.tissue.thread_slack(thread_id, uv))
	if body.tissue.thread_uvs(thread_id).size() == before:
		return false
	if not _sutures.has(thread_id):
		_sutures[thread_id] = {"before": {}}
	if layer == TissueSim.Depth.SKIN:
		paint(WoundMap.Layer.WOUNDS, WoundMap.STITCH, uv, uv, 0.0035, 0.9, WoundMap.Mode.MAX)
	hurt(0.035 if layer == TissueSim.Depth.SKIN else 0.06, uv)
	_apply_thread_closure(thread_id)
	return true


## The wheel on the free end of a live thread: tension is the needle's (ToolActions.SUTURE_TENSION_RANGE).
func set_suture_tension(thread_id: int, tension: float) -> void:
	if body.tissue.thread_info(thread_id).is_empty() or suture_done(thread_id):
		return
	_suture_tension.rpc(thread_id, tension)
	_apply_thread_closure(thread_id)


## Ties the thread off and cuts it: the edges it holds are joined for good at the tension it has, sewn as neatly as
## `quality` (the needle's ToolDef.quality) allows.
func finish_suture(thread_id: int, quality: float) -> void:
	if body.tissue.thread_info(thread_id).is_empty() or suture_done(thread_id):
		return
	_suture_finish.rpc(thread_id)
	_apply_thread_closure(thread_id, quality)


## True once the thread can take no more holes: tied off, or torn through the tissue.
func suture_done(thread_id: int) -> bool:
	var info := body.tissue.thread_info(thread_id)
	return not info.is_empty() and bool(info.final)


## Applies the closure the thread makes where it crosses wounds. Loose thread leaves a gap; pulling harder closes it,
## then raises the pressed edges into a lip; past THREAD_TEAR it cuts through. Skin over open muscle won't meet, and
## pulled shut over it the thread tears through. A tied-off thread (quality >= 0) joins the edges for good.
## Its closure is laid over what other closures left (_sutures before), so loosening it never undoes a staple.
func _apply_thread_closure(thread_id: int, quality: float = -1.0) -> void:
	var info := body.tissue.thread_info(thread_id)
	var suture: Dictionary = _sutures[thread_id]
	var layer: int = info.layer
	var tension: float = info.tension
	var loose := TissueSim.THREAD_LOOSE[layer]
	var closed := TissueSim.THREAD_CLOSED[layer]
	var closure := clampf((loose - tension) / (loose - closed), 0.0, 1.0)
	var points := body.tissue.thread_uvs(thread_id)
	# [wound, where the thread crosses it] for every wound it crosses.
	var crossed: Array[Array] = []
	var all := PackedVector2Array()
	var over_muscle := false
	for wound in wounds:
		if wound.is_internal() or wound.kind == Wound.Kind.BURN:
			continue
		var crossings := _thread_crossings(points, wound)
		if crossings.is_empty():
			continue
		crossed.append([wound, crossings])
		all.append_array(crossings)
		over_muscle = over_muscle or layer == TissueSim.Depth.SKIN and wound.through_muscle() \
				and Array(crossings).any(func(c: Vector2) -> bool: return body.tissue.muscle_open_near(c, MUSCLE_REACH))
	if crossed.is_empty():
		return
	if tension < TissueSim.THREAD_TEAR[layer] or (over_muscle and closure >= 0.99):
		_snap_suture(thread_id, all, (points[1] - points[0]).orthogonal(), over_muscle)
		return
	if over_muscle:
		_tear_notice("The skin won't meet over the open muscle. Sew the muscle first.")
		closure = minf(closure, 0.15)
	# Pressed edges rise into a small lip rather than sliding through one another, higher the harder it's pulled.
	var overpull := clampf((closed - tension) / (closed - TissueSim.THREAD_TEAR[layer]), 0.0, 1.0)
	var lip_scale: float = [0.0, 1.0, 0.7, 0.55][layer]
	_tissue_pucker.rpc(all, 0.018, (clampf((closure - 0.5) * 2.0, 0.0, 1.0) * 0.0008 + overpull * 0.0015) * lip_scale)
	var tied := quality >= 0.0 and closure >= 0.99
	for pair in crossed:
		var wound: Wound = pair[0]
		var crossings: PackedVector2Array = pair[1]
		var bins := _supported_bins(wound, crossings)
		var before: Dictionary = suture.before.get_or_add(wound.id, {"skin": {}, "muscle": {}})
		if layer == TissueSim.Depth.SKIN:
			wound.bins = _lay_closure(wound.bins, before.skin, bins, closure)
		elif layer == TissueSim.Depth.MUSCLE:
			wound.muscle = _lay_closure(wound.muscle, before.muscle, bins, closure)
		if tied:
			_tie_off(wound, crossings, bins, layer, quality)


## A tied off thread joins the edges of `wound` it holds (`bins`, crossing it at `crossings`) for good: the skin with a
## seam, or the muscle or fat under it.
func _tie_off(wound: Wound, crossings: PackedVector2Array, bins: PackedInt32Array, layer: int, quality: float) -> void:
	var path := PackedVector2Array()
	for bin in bins:
		path.append(wound.bin_position(bin))
	if layer != TissueSim.Depth.SKIN:
		_tissue_close_layer.rpc(path, MUSCLE_REACH, layer)
		return
	# The thread gathers the edges at its holes: every severed edge the bites hold is joined too, not only the exact
	# crossings, which are a little shorter so the thread dimples the skin where it pulls.
	for crossing in crossings:
		_tissue_stitch.rpc(crossing, 0.98, THREAD_STRENGTH[layer])
	_tissue_stitch_path.rpc(path, Wound.BIN_LENGTH_UV * 1.2, 1.0, THREAD_STRENGTH[layer])
	# Meeting edges squeeze the broad wet groove and blood out, but a narrow pink incision line remains under the
	# thread. A separate seam mask reveals that line without overloading closure quality in the stitch channel.
	for i in path.size():
		var previous := path[maxi(i - 1, 0)]
		paint(WoundMap.Layer.WOUNDS, WoundMap.CUT, previous, path[i], 0.012, 0.0, WoundMap.Mode.MIN)
		paint(WoundMap.Layer.WOUNDS, WoundMap.CUT, previous, path[i], 0.004, 0.09, WoundMap.Mode.MAX)
		paint(WoundMap.Layer.SEAMS, WoundMap.CLOSED_SEAM, previous, path[i], 0.0045, 1.0, WoundMap.Mode.MAX)
		paint(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, previous, path[i], 0.026, 1.0, WoundMap.Mode.SUB)
	wound.closure_quality = lerpf(wound.closure_quality, quality, 0.5)
	if wound.closure() >= 0.99 and wound.closure_quality > 0.9:
		add_flag("neat_closure")
		Surgery.current.scoring.add("good_suture", true)


## The grid point (uv) a hole clicked at uv goes through: the nearest one on the clicked side of the wound, never one
## on its line, so every bite goes across it.
func _hole_uv(uv: Vector2, wound: Wound) -> Vector2:
	var line := wound.closest_point(uv)
	var away := (uv - line).normalized()
	if away == Vector2.ZERO:
		return uv
	var cell := _grid_cell(away)
	var hole := body.tissue.uv_of(body.tissue.nearest(uv))
	for i in 3:
		if (hole - line).dot(away) > cell * 0.3:
			break
		uv += away * cell * 0.5
		hole = body.tissue.uv_of(body.tissue.nearest(uv))
	return hole


## The tissue grid's cell size (uv) along `direction`: cells are square in meters, not in uv.
func _grid_cell(direction: Vector2) -> float:
	direction = direction.normalized()
	return absf(direction.x) / body.tissue.res_x + absf(direction.y) / body.tissue.res_y


## Where a thread's spans (holes `points`) cross its wound. Each end of the wound counts a grid cell further: a bite
## round the end of a short stab or bullet hole goes past it, and the holes snap to the grid.
func _thread_crossings(points: PackedVector2Array, wound: Wound) -> PackedVector2Array:
	var line := wound.points.duplicate()
	if line.size() < 2:
		# A puncture or gunshot can be a single point. Give it a short virtual incision axis perpendicular to the first
		# bite so a span across the hole supports its one closure bin instead of intersecting a zero-length segment.
		var bite := points[1] - points[0] if points.size() >= 2 else Vector2.RIGHT
		var along := bite.orthogonal().normalized() if bite.length_squared() > 0.000001 else Vector2.RIGHT
		var half := _grid_cell(along)
		line[0] -= along * half
		line.append(wound.points[0] + along * half)
	for end: Array in [[0, 1], [-1, -2]]:
		var out := (line[end[0]] - line[end[1]]).normalized()
		line[end[0]] += out * _grid_cell(out)
	var crossings := PackedVector2Array()
	for i in range(1, points.size()):
		for n in range(1, line.size()):
			var crossing: Variant = Geometry2D.segment_intersects_segment(points[i - 1], points[i], line[n - 1], line[n])
			if crossing != null and (crossings.is_empty() or crossings[-1].distance_to(crossing) > 0.001):
				crossings.append(crossing)
	return crossings


## The wound's bins the thread holds, in order: each bite holds the edge halfway to its neighbours (and as far past
## the first and last), so they run on unbroken. Bins are much finer than practical stitch spacing: the bins at the
## exact crossings alone would leave a dotted closure.
static func _supported_bins(wound: Wound, crossings: PackedVector2Array) -> PackedInt32Array:
	var at := PackedInt32Array()
	for crossing in crossings:
		at.append(wound.bin_at(crossing))
	at.sort()
	var reach := 1
	for i in range(1, at.size()):
		reach = maxi(reach, ceili((at[i] - at[i - 1]) * 0.5))
	# A bite within one stitch spacing of an end holds that end too. Holes snap to the tissue grid, so the first
	# crossing can land a bin or two in even when the player clicks right beside the end of the incision.
	var first := 0 if at[0] <= reach * 2 else at[0] - reach
	var last := wound.bins.size() if wound.bins.size() - 1 - at[-1] <= reach * 2 else at[-1] + reach + 1
	return PackedInt32Array(range(first, last))


## `values` (a wound's closure per bin) with `closure` on `bins`, over the value each had before this thread first held
## it (remembered in `base`).
static func _lay_closure(values: PackedFloat32Array, base: Dictionary, bins: PackedInt32Array, closure: float) -> PackedFloat32Array:
	for bin in bins:
		if not base.has(bin):
			base[bin] = values[bin]
		values[bin] = maxf(base[bin], closure)
	return values


## The thread cut through the tissue (where it first crosses a wound, of `crossings`): it lets go everywhere, and what it
## held is as open as before it.
func _snap_suture(thread_id: int, crossings: PackedVector2Array, across: Vector2, over_muscle: bool) -> void:
	var suture: Dictionary = _sutures[thread_id]
	for id: int in suture.before:
		var wound := _wound(id)
		for bin: int in suture.before[id].skin:
			wound.bins[bin] = suture.before[id].skin[bin]
		for bin: int in suture.before[id].muscle:
			wound.muscle[bin] = suture.before[id].muscle[bin]
	_tissue_pucker.rpc(crossings, 0.018, 0.0)
	_suture_snap.rpc(thread_id)
	var layer: int = body.tissue.thread_info(thread_id).layer
	if layer == TissueSim.Depth.SKIN:
		tear(crossings[0], across, 0.018)
		Surgery.current.scoring.add("suture_tear_through")
		_tear_notice("The stitch tore through: the muscle under it is still open." if over_muscle else "The thread was pulled too tight and tore through the skin.")
	else:
		Surgery.current.announce("The thread cut through the %s." % ("fat" if layer == TissueSim.Depth.FAT else "muscle"), true)


func close_at(uv: Vector2, def: ToolDef, dt: float, improvised_mult: float, pressure: int) -> bool:
	var wound := _nearest_wound(uv, 0.02, false)
	if wound == null:
		return false
	var bin := wound.bin_at(uv)
	if wound.bins[bin] >= 1.0:
		return false
	var quality := def.quality
	if def.action == "suture" and def.id in ["surgical_tape", "duct_tape"] and wound.depth > 0.6:
		quality *= 0.5
	if def.improvised:
		quality = lerpf(quality, 1.0, 1.0 - improvised_mult)
	var cap := 1.0
	if def.id in TENSIONED_CLOSURES:
		if pressure == 1:
			quality *= 0.6
			cap = 0.9
		elif pressure == 3:
			quality = minf(quality * 1.05, 1.0)
	# Skin pulled shut over open muscle carries the muscle's pull: the edges won't meet, and a tight stitch
	# gets them there only to tear through.
	if wound.through_muscle() and body.tissue.muscle_open_near(wound.bin_position(bin), MUSCLE_REACH):
		# A stab or a bullet hole is too small to reach into: the needle goes in through it and sews the muscle first.
		if wound.kind in [Wound.Kind.PUNCTURE, Wound.Kind.GUNSHOT] and not body.is_open(uv):
			return close_muscle_at(uv, def, dt)
		if not (def.id in TENSIONED_CLOSURES and pressure == 3):
			_tear_notice("The skin won't meet over the open muscle. Sew the muscle first.")
			return false
		wound.bins[bin] = minf(wound.bins[bin] + def.power * 1.5 * dt, cap)
		if wound.bins[bin] >= cap:
			wound.bins[bin] = 0.0
			tear(wound.bin_position(bin), Vector2(rng.randf_range(-1, 1), rng.randf_range(-1, 1)), 0.02)
			Surgery.current.scoring.add("suture_tear_through")
			_tear_notice("The stitch tore through: the muscle under it is still open.")
		return false
	var before := wound.bins[bin]
	wound.bins[bin] = minf(before + def.power * 1.5 * dt, cap)
	wound.closure_quality = lerpf(wound.closure_quality, quality, 0.2)
	if def.id in TENSIONED_CLOSURES and pressure == 3 and before < cap and wound.bins[bin] >= cap:
		if rng.randf() < 0.2 / mods.mult("tear_threshold_mult"):
			wound.bins[bin] = 0.3
			tear(wound.bin_position(bin), Vector2(rng.randf_range(-1, 1), rng.randf_range(-1, 1)), 0.02)
			_tissue_burst.rpc(wound.bin_position(bin), 0.03)
			Surgery.current.scoring.add("suture_tear_through")
			Surgery.current.announce("Pulled too tight. The stitch tore through the skin.", true)
			return false
	if before < cap and wound.bins[bin] >= cap:
		var p := wound.bin_position(bin)
		paint(WoundMap.Layer.WOUNDS, WoundMap.CUT, p, p, 0.012, 0.35, WoundMap.Mode.MIN)
		paint(WoundMap.Layer.WOUNDS, WoundMap.STITCH, p - Vector2(0.006, 0.0), p + Vector2(0.006, 0.0), 0.002, 1.0, WoundMap.Mode.MAX)
		var tension := STITCH_TENSION[pressure] if def.id in TENSIONED_CLOSURES else STITCH_TENSION[0]
		_tissue_stitch.rpc(p, tension, 1.2 + quality)
		if def.id == "office_stapler":
			add_flag("office_staples")
		elif def.id == "duct_tape":
			add_flag("duct_tape")
		if wound.closure() >= 0.99 and wound.closure_quality > 0.9:
			add_flag("neat_closure")
			Surgery.current.scoring.add("good_suture", true)
		hurt(0.06, uv)
		return true
	return false


## Sewing inside the opening of a wound through the muscle closes the muscle, bin by bin. Tape can't.
## Returns true when a bin of muscle closed.
func close_muscle_at(uv: Vector2, def: ToolDef, dt: float) -> bool:
	var wound := _nearest_wound(uv, 0.03, false)
	if wound == null or not wound.through_muscle() or def.id in ["surgical_tape", "duct_tape"]:
		return false
	var bin := wound.bin_at(uv)
	var before := wound.muscle[bin]
	wound.muscle[bin] = minf(before + def.power * 1.5 * dt, 1.0)
	if before < 1.0 and wound.muscle[bin] >= 1.0:
		_tissue_muscle.rpc(wound.bin_position(bin), MUSCLE_REACH)
		hurt(0.06, uv)
		return true
	return false


## Returns true while it's sewing an internal wound that isn't closed yet.
func close_internal_at(uv: Vector2, def: ToolDef, dt: float) -> bool:
	var sewing := false
	for wound in wounds:
		if wound.is_internal() and _reaches(wound, uv) and wound.bins[0] < 1.0:
			wound.bins[0] = minf(wound.bins[0] + def.power * 0.4 * dt, 1.0)
			wound.closure_quality = lerpf(wound.closure_quality, def.quality, 0.1)
			sewing = true
	return sewing


## A tool tip at uv works inside the body: in an opening, or through a stab or bullet hole in the skin there.
func _inside(zone: String, uv: Vector2) -> bool:
	if zone == "cavity":
		return true
	var hole := _nearest_wound(uv, 0.02, false)
	return zone == "site" and hole != null and hole.kind in [Wound.Kind.PUNCTURE, Wound.Kind.GUNSHOT]


## A tool tip inside the opening at uv reaches an internal wound under it, whatever its depth: a tool can't hover in
## the cavity, it comes down onto whatever lies there (the organ the wound is in, or the floor once a target is out).
static func _reaches(wound: Wound, uv: Vector2) -> bool:
	return wound.points[0].distance_to(uv) < 0.05


func cauterize_at(zone: String, uv: Vector2, def: ToolDef, dt: float) -> void:
	var radius := body.meters_to_uv(def.radius)
	var sealed := false
	for wound in wounds:
		# An opened skin wound's edges lie apart from where it was cut: up to the gap of a fully open one.
		var near := _reaches(wound, uv) if wound.is_internal() else wound.distance_to(uv) < radius + 0.01 + body.meters_to_uv(FULL_GAP) * wound.opened
		if near and (_inside(zone, uv) if wound.is_internal() else zone == "site"):
			wound.cauterized = minf(wound.cauterized + def.power * 0.6 * dt, 0.95)
			sealed = true
	if zone == "site":
		paint(WoundMap.Layer.WOUNDS, WoundMap.BURN, uv, uv, radius * 1.5, def.power * dt * 3.0, WoundMap.Mode.ADD)
		hurt(0.25 * dt * 10.0, uv)
		if def.id == "lighter":
			add_flag("lighter_burns", dt)
		if not sealed:
			Surgery.current.scoring.add("burn_damage", true)
	if mods.flag("pacemaker") and scenario.site in ["chest", "abdomen"] and rng.randf() < dt * 0.3:
		vitals.heart_rate += rng.randf_range(-25.0, 35.0)
		Surgery.current.announce("The monitor stutters. Pacemaker interference.")
		_reveal("pacemaker")


func mark(a: Vector2, b: Vector2) -> void:
	marked_uv += a.distance_to(b)
	paint(WoundMap.Layer.FLUIDS, WoundMap.INK, a, b, 0.004, 1.0, WoundMap.Mode.MAX)


## drug: what the swab is soaked in, when that's not fixed by the tool (a cotton pad dipped in iodine).
func swab_at(zone: String, uv: Vector2, def: ToolDef, dt: float, soaked_in: String = "") -> void:
	var drug := soaked_in if soaked_in else def.drug
	var radius := body.meters_to_uv(def.radius)
	var sanitize := drug in ["iodine", "whiskey"]
	if zone == "site":
		# One pass over the disk for every channel: a wipe runs every physics frame, so it has to be cheap.
		var ops: Array = [[WoundMap.BLOOD, def.power * dt * 2.0, WoundMap.Mode.SUB]]
		if sanitize:
			var strength := 1.0 if drug == "iodine" else 0.5
			_mark_grid(_sanitized, uv, radius, strength)
			ops.append([WoundMap.GRIME, dt * 2.0, WoundMap.Mode.SUB])
			ops.append([WoundMap.IODINE if drug == "iodine" else WoundMap.GRIME, 0.3 * dt * 10.0, WoundMap.Mode.ADD])
			if drug == "whiskey" and _nearest_wound(uv, 0.03, false):
				hurt(0.5 * dt * 10.0, uv)
		_paint_ops.rpc(WoundMap.Layer.FLUIDS, uv, radius, ops)
		var wound := _nearest_wound(uv, 0.02, false)
		if wound and def.id == "gauze":
			wound.held = minf(wound.held + dt * 2.0, 0.7)
	elif zone == "cavity":
		cavity_blood_ml = maxf(cavity_blood_ml - def.power * 15.0 * dt, 0.0)


func suction_at(zone: String, uv: Vector2, def: ToolDef, dt: float) -> void:
	var radius := body.meters_to_uv(def.radius)
	if zone == "site":
		paint(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, uv, uv, radius, def.power * dt * 4.0, WoundMap.Mode.SUB)
	elif zone == "cavity":
		cavity_blood_ml = maxf(cavity_blood_ml - def.power * 40.0 * dt, 0.0)
	for target in targets:
		if target.is_suction_target() and not target.extracted and target.uv.distance_to(uv) < 0.08:
			target.amount = maxf(target.amount - def.power * dt, 0.0)
			if target.amount <= 0.0:
				_extract(target)


func saw_at(tip_uv: Vector2, def: ToolDef, dt: float) -> bool:
	for target in targets:
		if target.remove_with in ["saw", "smash"] and not target.extracted and target.uv.distance_to(tip_uv) < 0.08:
			var hard := mods.num("bone_hardness") >= 2.0
			var rate := def.power * 0.12 * mods.mult("saw_speed_mult")
			if hard and def.power < 2.0:
				Surgery.current.announce("The saw skids off. This bone is too hard for it.", true)
				_reveal("bones")
				return true
			target.anchor = maxf(target.anchor - rate * dt, 0.0)
			hurt(0.1 * dt * 10.0, tip_uv)
			if target.anchor <= 0.0:
				_extract(target)
			return true
	return false


func smash_at(tip_uv: Vector2, def: ToolDef) -> void:
	bruise(tip_uv, body.meters_to_uv(0.04), 0.5 * def.power)
	hurt(0.4, tip_uv)
	Surgery.current.sound("mallet_hit", body.uv_to_world(tip_uv))
	for target in targets:
		if target.remove_with in ["saw", "smash"] and not target.extracted and target.uv.distance_to(tip_uv) < 0.1:
			target.anchor = maxf(target.anchor - def.power * 0.12, 0.0)
			if target.anchor <= 0.0:
				_extract(target)
				Surgery.current.sound("bone_crack", body.uv_to_world(tip_uv))


func bruise(uv: Vector2, radius: float, strength: float) -> void:
	paint(WoundMap.Layer.WOUNDS, WoundMap.BRUISE, uv, uv, radius * mods.mult("bruise_mult"), strength, WoundMap.Mode.ADD)


func apply_tourniquet() -> void:
	tourniquet_on = true
	tourniquet_time = 0.0
	add_flag("tourniquet")


func remove_tourniquet() -> void:
	tourniquet_on = false


## A catheter went into the arm at `at` (world space): tubing now runs from the stand to there.
## in_vein: it hit a vein, so the line works. Host only (what runs through the line is decided here).
func set_iv(at: Vector3, in_vein: bool) -> void:
	if not iv_set:
		iv_set = true
		iv_in_vein = in_vein
		hurt(0.1)
		_iv_placed.rpc(body.root().to_local(at))


## Someone walked into the tubing: the catheter rips out of the arm and the stand rattles.
## The catheter ends up on the floor at their feet, where it can be picked up, washed and used again.
func pull_iv(surgeon: Surgeon) -> void:
	if not iv_set:
		return
	iv_set = false
	iv_in_vein = false
	hurt(0.35)
	add_flag("iv_pulled")
	Surgery.current.scoring.add("iv_pulled")
	Surgery.current.sound("cable_yank", surgeon.global_position)
	Surgery.current.announce("%s catches the IV line. It rips out of the arm." % surgeon.display_name)
	Surgery.current.jolt_peer(surgeon.peer_id, 0.8)
	# At their feet: a step ahead could be under the table, which is solid down to the floor.
	Surgery.current.tools.drop_new("iv_catheter", surgeon.global_position + Vector3.UP * 0.1)
	_iv_removed.rpc()


## point is local to the body's root. The dressing rides the forearm it's on (PatientBody.iv_site()).
func _connect_iv(point: Vector3) -> void:
	if Surgery.current and Surgery.current.room.iv_line:
		var site := body.iv_site(body.root().to_global(point))
		Surgery.current.room.connect_iv(site.node, site.frame, site.radius)


func graft_at(uv: Vector2, def: ToolDef) -> bool:
	var cell := _cell(uv)
	if cell < 0 or _burn_cells[cell] == 0 or _grafted[cell] == 1:
		return false
	_grafted[cell] = 1
	paint(WoundMap.Layer.WOUNDS, WoundMap.BURN, uv, uv, 1.0 / GRID, 0.25, WoundMap.Mode.MIN)
	paint(WoundMap.Layer.WOUNDS, WoundMap.STITCH, uv, uv, 0.4 / GRID, 1.0 * def.quality, WoundMap.Mode.MAX)
	return true


func debride_at(uv: Vector2) -> void:
	var cell := _cell(uv)
	if cell >= 0 and _burn_cells[cell] == 1 and _debrided[cell] == 0:
		_debrided[cell] = 1
		paint(WoundMap.Layer.WOUNDS, WoundMap.BURN, uv, uv, 0.8 / GRID, 0.45, WoundMap.Mode.MIN)
		paint(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, uv, uv, 0.5 / GRID, 0.4, WoundMap.Mode.MAX)
		hurt(0.2, uv)


func contaminate_site(reason: String) -> void:
	_contaminate()
	if reason:
		Surgery.current.announce(reason)


func _contaminate() -> void:
	var opened := wounds.any(func(w: Wound) -> bool: return w.opened > 0.2 or w.is_internal())
	if not opened:
		add_flag("dirty_skin")
	else:
		add_flag("dirty_cavity_limb" if body.is_limb_site() else "dirty_cavity")


# --- Grips (forceps, clamps, retractors) -----------------------------------------------------------


## Called when a clamp tool closes at tip. Returns grip info that update_grip() and release_grip() use.
func grip(tool_uid: int, zone: String, uv: Vector2, depth_m: float) -> Dictionary:
	# The nearest target the jaws close on, not one another tool already holds (two broken ends lie close together).
	var nearest: CavityTarget = null
	for target in targets:
		if target.extracted or target.is_suction_target() or target.remove_with in ["saw", "smash"] and target.anchor > 0.0:
			continue
		if target.gripped_by != 0 and target.gripped_by != tool_uid:
			continue
		if zone in ["cavity", "site"] and target.uv.distance_to(uv) < 0.05 and absf(target.depth - depth_m) < 0.05 and not _covered(target):
			if nearest == null or target.uv.distance_to(uv) < nearest.uv.distance_to(uv):
				nearest = target
	if nearest:
		nearest.gripped_by = tool_uid
		return {"type": "target", "target": nearest.index, "start_depth": depth_m}
	if _inside(zone, uv):
		for wound in wounds:
			if wound.is_internal() and wound.points[0].distance_to(uv) < 0.05:
				wound.clamped = 0.9
				return {"type": "vessel", "wound": wound.id}
	if zone == "cavity":
		# An organ in the way can be taken hold of and moved aside, to get at what's under it.
		var organ := body.organ_at(body.uv_to_world(uv, depth_m), 0.02)
		if organ >= 0:
			body.hold_organ(organ, body.organs[organ].position)
			return {"type": "organ", "organ": organ, "offset": body.organs[organ].position - body.site.to_local(body.uv_to_world(uv, depth_m))}
	var wound := _nearest_wound(uv, 0.03, false)
	if wound and zone == "cavity" and wound.bleed_rate(1.0, 1.0) > 0.0 and wound.depth > 0.6:
		wound.clamped = 0.85
	# Skin can be pinched anywhere on the site, but from inside the cavity only near a wound edge.
	if zone == "site" or zone == "cavity" and wound:
		body.tissue.grip(tool_uid, uv)
		var info := {"type": "skin", "wound": wound.id if wound else 0, "anchor": uv}
		if zone == "site" and not body.tissue.piece_of(body.tissue.nearest(uv)).is_empty():
			info.piece = true
		return info
	return {"type": "none"}


## Called when a spreader (the Gelpi retractor) is set into the skin with its jaws' tips at `tips` (world, see
## ToolActions.spread_tips()): each jaw takes hold of the edge on its own side of the middle.
## Returns grip info for open_spreader() and release_grip(), {"type": "none"} when a tip isn't on the site.
func set_spreader(tool_uid: int, tips: Array[Vector3], spread: float) -> Dictionary:
	var uvs: Array[Vector2] = []
	for tip in tips:
		var probe := body.probe(tip)
		if not probe.zone in ["site", "cavity"]:
			return {"type": "none"}
		uvs.append(probe.uv)
	var middle := (uvs[0] + uvs[1]) * 0.5
	var keys: Array[int] = []
	var starts: Array[Vector3] = []
	for side in 2:
		var key := spreader_key(tool_uid, side)
		var held := body.tissue.grip_beside(key, uvs[side], middle, uvs[side] - middle)
		if held < 0:
			for k in keys:
				body.tissue.release(k)
			return {"type": "none"}
		keys.append(key)
		starts.append(body.tissue.pos[held])
	var axis := body.site.to_local(tips[1]) - body.site.to_local(tips[0])
	return {"type": "spread", "keys": keys, "starts": starts, "axis": Vector3(axis.x, 0.0, axis.z).normalized(), "spread": spread}


## Opens or closes a set spreader to `spread` (meters between its tips): each jaw moves its edge half the change
## away from the middle (toward it when closing).
func open_spreader(grip_info: Dictionary, spread: float) -> void:
	var move: Vector3 = grip_info.axis * (spread - float(grip_info.spread)) * 0.5
	for side in 2:
		body.tissue.move_grip(grip_info.keys[side], grip_info.starts[side] + move * (1.0 if side == 1 else -1.0))


## The tissue grip key of a spreader's jaw (side 0 or 1). Negative, so it never meets a clamp's, which is its uid.
static func spreader_key(tool_uid: int, side: int) -> int:
	return -(tool_uid * 2 + side)


func update_grip(tool_uid: int, grip_info: Dictionary, tip: Vector3, power: float, dt: float, speed: float) -> Dictionary:
	match grip_info.type:
		"target":
			var target := targets[grip_info.target]
			var local := body.site.to_local(tip)
			var height := local.y
			if target.anchor > 0.0:
				var pulled := -target.depth - height
				if pulled < -0.02:
					if speed < 0.06:
						target.anchor = maxf(target.anchor - dt * 0.3, 0.0)
					if speed > 0.25 or pulled < -0.05:
						Surgery.current.scoring.add("forced_extraction")
						Surgery.current.announce("Ripped it out!")
						tear(target.uv, Vector2(rng.randf_range(-1, 1), rng.randf_range(-1, 1)), 0.05)
						target.anchor = 0.0
				return grip_info
			target.position = local
			target.uv = Vector2(local.x / body.site_size.x + 0.5, local.z / body.site_size.y + 0.5)
			target.depth = -height
			if not target.is_fragment() and height > 0.03:
				_extract(target)
				return {"type": "carry", "target": target.index}
		"carry":
			targets[grip_info.target].global_position = tip
		"skin":
			body.tissue.move_grip(tool_uid, body.site.to_local(tip))
			if grip_info.get("piece", false) and body.site.to_local(tip).y - body.surface_height(grip_info.anchor) > PIECE_LIFT:
				_take_piece(tool_uid, grip_info.anchor)
				return {"type": "none"}
		"organ":
			body.hold_organ(grip_info.organ, body.site.to_local(tip) + (grip_info.offset as Vector3))
	return grip_info


func release_grip(tool_uid: int, grip_info: Dictionary, self_retaining: bool) -> void:
	body.tissue.release(tool_uid)
	match grip_info.get("type", "none"):
		"target":
			targets[grip_info.target].gripped_by = 0
		"carry":
			# Taken out: let go of, it's put aside instead of hanging over the opening.
			targets[grip_info.target].gripped_by = 0
			targets[grip_info.target].set_aside()
		"organ":
			body.release_organ(grip_info.organ)
		"spread":
			for key: int in grip_info.keys:
				body.tissue.release(key)
		"vessel":
			if not self_retaining:
				_wound(grip_info.wound).clamped = 0.0
		"skin":
			var wound := _wound(grip_info.wound)
			if wound and not self_retaining:
				wound.clamped = 0.0


## Host: the piece of skin cut out all round at uv comes off in the forceps that lifted it, as a skin graft for a burn.
func _take_piece(tool_uid: int, uv: Vector2) -> void:
	body.tissue.release(tool_uid)
	_tissue_excise.rpc(body.tissue.nearest(uv))
	Surgery.current.tools.give_graft(tool_uid)
	add_flag("graft_taken")
	Surgery.current.announce("The skin comes away in one piece.")


func _covered(target: CavityTarget) -> bool:
	for organ in body.organs:
		var organ_uv := Vector2(organ.position.x / body.site_size.x + 0.5, organ.position.z / body.site_size.y + 0.5)
		if organ_uv.distance_to(target.uv) < 0.07 and organ.position.y > body.surface_height(target.uv) - target.depth:
			return true
	return false


func _extract(target: CavityTarget) -> void:
	if target.extracted:
		return
	target.extracted = true
	if target.is_suction_target():
		target.set_aside()
	else:
		target.update_look()
	if target.surge > 0.0:
		var wound := _new_wound(Wound.Kind.INTERNAL, target.uv, clampf(0.4 + target.surge * 0.15, 0.0, 1.0))
		wound.depth_m = target.depth
		Surgery.current.announce("Blood wells up where it came out!")
		Surgery.current.sound("blood_spurt", body.uv_to_world(target.uv))
	Surgery.current.announce("%s: done." % target.kind.capitalize())


# --- Queries for objectives ------------------------------------------------------------------------


func sanitized_fraction() -> float:
	var total := 0.0
	for value in _sanitized:
		total += value
	return total / _sanitized.size()


func grid_fraction(which: String) -> float:
	var burned := _burn_cells.count(1)
	if burned == 0:
		return 1.0
	var grid: PackedByteArray = _debrided if which == "debrided" else _grafted
	var done := 0
	for i in grid.size():
		done += 1 if grid[i] == 1 and _burn_cells[i] == 1 else 0
	return float(done) / burned


func surgeon_cut_length_m(min_depth: float) -> float:
	var total := 0.0
	for wound in wounds:
		if wound.made_by_surgeon and wound.kind == Wound.Kind.CUT and wound.depth >= min_depth:
			total += body.uv_to_meters(wound.length_uv())
	return total


func skin_closure() -> float:
	var skin := wounds.filter(func(w: Wound) -> bool: return not w.is_internal() and w.kind != Wound.Kind.BURN)
	if skin.is_empty():
		return 1.0
	return skin.reduce(func(acc: float, w: Wound) -> float: return acc + w.closure(), 0.0) / skin.size()


func internal_closed() -> bool:
	return wounds.all(func(w: Wound) -> bool: return not w.is_internal() or w.closure() >= 0.85 or w.cauterized >= 0.9)


func any_clamped() -> bool:
	return wounds.any(func(w: Wound) -> bool: return w.clamped >= 0.8)


func target_by_kind(kind: String, skip_extracted: bool = false) -> CavityTarget:
	for target in targets:
		if target.kind == kind and not (skip_extracted and target.extracted):
			return target
	return null


func revealed_card_lines() -> PackedStringArray:
	var lines := PackedStringArray()
	for roll: Dictionary in rolls:
		var line := (Db.patient_quirks[roll.id] as QuirkDef).text("card", roll.variant)
		if line:
			lines.append(line)
	return lines


# --- Internals -------------------------------------------------------------------------------------


func _new_wound(kind: Wound.Kind, at: Vector2, depth: float) -> Wound:
	var wound := Wound.new(_next_wound_id, kind, at, depth)
	_next_wound_id += 1
	wounds.append(wound)
	return wound


func _wound(id: int) -> Wound:
	for wound in wounds:
		if wound.id == id:
			return wound
	return null


func _nearest_wound(uv: Vector2, max_dist: float, internal: bool) -> Wound:
	var best: Wound = null
	var best_dist := max_dist
	for wound in wounds:
		if wound.is_internal() != internal or wound.kind == Wound.Kind.BURN:
			continue
		var dist := wound.distance_to(uv)
		if dist < best_dist:
			best_dist = dist
			best = wound
	return best


func _uv(raw: Array) -> Vector2:
	# Mirrored anatomy flips left and right: uv.y runs across the body.
	return Vector2(raw[0], 1.0 - raw[1] if mods.flag("mirrored") else raw[1])


func _site_local(uv: Vector2, depth: float) -> Vector3:
	return Vector3((uv.x - 0.5) * body.site_size.x, body.surface_height(uv) - depth, (uv.y - 0.5) * body.site_size.y)


func _cell(uv: Vector2) -> int:
	var c := Vector2i((uv * GRID).floor())
	return c.y * GRID + c.x if c.x >= 0 and c.y >= 0 and c.x < GRID and c.y < GRID else -1


func _mark_grid(grid: PackedFloat32Array, uv: Vector2, radius: float, strength: float) -> void:
	var r := ceili(radius * GRID)
	var center := Vector2i((uv * GRID).floor())
	for y in range(center.y - r, center.y + r + 1):
		for x in range(center.x - r, center.x + r + 1):
			if x >= 0 and y >= 0 and x < GRID and y < GRID:
				grid[y * GRID + x] = maxf(grid[y * GRID + x], strength)


func _paint_wound(wound: Wound) -> void:
	for i in range(1, wound.points.size()):
		var jitter := 0.006 if wound.kind == Wound.Kind.TEAR else 0.0
		paint(WoundMap.Layer.WOUNDS, WoundMap.CUT, wound.points[i - 1], wound.points[i], 0.005, minf(wound.depth * 0.75, 0.7), WoundMap.Mode.MAX, jitter)


## Setup-time painting, run locally on every peer (no RPC, clients may not be listening yet).
func _paint_wound_local(wound: Wound) -> void:
	var map := body.wound_map
	for i in range(1, wound.points.size()):
		map.stroke(WoundMap.Layer.WOUNDS, WoundMap.CUT, wound.points[i - 1], wound.points[i], 0.006, minf(wound.depth * 0.75, 0.7), WoundMap.Mode.MAX, 0.003 if wound.kind == Wound.Kind.TEAR else 0.0, wound.id)
		body.tissue.cut(wound.points[i - 1], wound.points[i], _tissue_depth(wound.depth))
		map.stroke(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, wound.points[i - 1], wound.points[i], 0.02, 0.8, WoundMap.Mode.MAX)
	if wound.points.size() == 1:
		map.disk(WoundMap.Layer.WOUNDS, WoundMap.CUT, wound.points[0], 0.012, 0.7, WoundMap.Mode.MAX, false)
		map.disk(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, wound.points[0], 0.04, 0.9, WoundMap.Mode.MAX)
		# A puncture is a small cross-shaped hole in the grid.
		for d: Vector2 in [Vector2(0.025, 0.0), Vector2(0.0, 0.025)]:
			body.tissue.cut(wound.points[0] - d, wound.points[0] + d, _tissue_depth(wound.depth))


func _add_burn_local(uv: Vector2, radius: float) -> void:
	body.wound_map.disk(WoundMap.Layer.WOUNDS, WoundMap.BURN, uv, radius, 1.4, WoundMap.Mode.MAX)
	var r := ceili(radius * GRID)
	var center := Vector2i((uv * GRID).floor())
	for y in range(center.y - r, center.y + r + 1):
		for x in range(center.x - r, center.x + r + 1):
			if x >= 0 and y >= 0 and x < GRID and y < GRID and Vector2(x + 0.5, y + 0.5).distance_to(uv * GRID) <= radius * GRID:
				_burn_cells[y * GRID + x] = 1
	var burn := _new_wound(Wound.Kind.BURN, uv, 0.3)
	burn.bins.fill(1.0)


func _ambient_voice() -> void:
	var talk := mods.num("talk_rate", 1.0)
	_voice_cooldown = rng.randf_range(12.0, 25.0) / talk
	if vitals.panic > 0.6:
		_speak("panic")
	elif talk > 1.0 and rng.randf() < 0.3:
		_speak("hint" if rng.randf() < 0.6 else "lie")
	else:
		_speak("calm")


func _speak(trigger: String, force: bool = false) -> void:
	if not force and _voice_cooldown > 0.0 and trigger in ["pain", "calm"]:
		return
	_voice_cooldown = maxf(_voice_cooldown, 4.0)
	var sound: String = {"pain": "patient_groan", "panic": "patient_scream", "tickle": "patient_groan"}.get(trigger, "")
	if sound:
		_vocal.rpc(sound)
	var lines: Array = Db.dialogue.get_value(trigger, age, Db.dialogue.get_value(trigger, "any", []))
	if lines.is_empty():
		return
	var index := rng.randi_range(0, lines.size() - 1)
	Surgery.current.say(lines[index], "%s_%d" % [trigger, index])


## Next ramble line for the Last Request scenario. Plays in order.
func ramble(index: int) -> bool:
	var lines: Array = Db.dialogue.get_value("ramble", "any", [])
	if index >= lines.size() or not alive:
		return false
	Surgery.current.say(lines[index], "ramble_%d" % index)
	return true


## A hidden quirk showed itself. Players unlock it in the codex from the report either way.
func _reveal(quirk_id: String) -> void:
	if rolls.any(func(r: Dictionary) -> bool: return r.id == quirk_id):
		add_flag("revealed_" + quirk_id)


# --- Networking ------------------------------------------------------------------------------------


## Host: paint on every peer.
func paint(layer: WoundMap.Layer, channel: int, a: Vector2, b: Vector2, radius: float, value: float, mode: WoundMap.Mode, jitter: float = 0.0) -> void:
	_paint_seed += 1
	_paint.rpc(layer, channel, a, b, radius, value, mode, jitter, _paint_seed)


@rpc("authority", "call_local", "reliable")
func _paint(layer: int, channel: int, a: Vector2, b: Vector2, radius: float, value: float, mode: int, jitter: float, seed_value: int) -> void:
	if a == b:
		body.wound_map.disk(layer as WoundMap.Layer, channel, a, radius, value, mode as WoundMap.Mode)
	else:
		body.wound_map.stroke(layer as WoundMap.Layer, channel, a, b, radius, value, mode as WoundMap.Mode, jitter, seed_value)


@rpc("authority", "call_local", "reliable")
func _paint_ops(layer: int, uv: Vector2, radius: float, ops: Array) -> void:
	body.wound_map.disk_ops(layer as WoundMap.Layer, uv, radius, ops)


@rpc("authority", "call_remote", "unreliable_ordered")
func _sync(vital_data: Dictionary, target_states: Array, organ_positions: Array, grips: Array, cavity_ml: float, bleeds: Array) -> void:
	body.blood.sources = bleeds
	vitals.from_dict(vital_data)
	for i in mini(target_states.size(), targets.size()):
		targets[i].apply_state(target_states[i])
	body.apply_organ_states(organ_positions)
	body.set_cavity_blood(cavity_ml / CAVITY_FULL_ML)
	body.tissue.set_grips(grips)


func _process(delta: float) -> void:
	body.animator.animate(vitals, alive, delta)
	if body.skin_material:
		body.set_pallor(clampf(1.0 - vitals.blood_ratio() * 1.4 + 0.4, 0.0, 1.0))
	if multiplayer.is_server():
		for entry: Array in body.tissue.snapped:
			_on_snap(entry[0], entry[1], entry[2], entry[3])
		body.tissue.snapped.clear()


## Host: a spring in the tissue sim was stretched too far and snapped.
func _on_snap(a: Vector2, b: Vector2, kind: int, spring: int) -> void:
	var mid := (a + b) * 0.5
	_tissue_snap.rpc(spring)
	if not Surgery.current or not Surgery.current.running:
		return
	var wound := _nearest_wound(mid, 0.04, false)
	if kind == TissueSim.Kind.STITCH:
		if wound:
			wound.bins[wound.bin_at(mid)] = 0.3
			paint(WoundMap.Layer.WOUNDS, WoundMap.STITCH, mid, mid, 0.008, 0.0, WoundMap.Mode.MIN)
		Surgery.current.scoring.add("suture_tear_through")
		_tear_notice("A stitch tore through the skin.")
		hurt(0.3, mid)
		return
	# Neighbouring springs tend to go together; grow the fresh tear instead of making one wound per spring.
	if wound and wound.kind == Wound.Kind.TEAR and wound.points[wound.points.size() - 1].distance_to(mid) < 0.06:
		paint(WoundMap.Layer.WOUNDS, WoundMap.CUT, wound.points[wound.points.size() - 1], mid, 0.005, 0.6, WoundMap.Mode.MAX, 0.006)
		wound.extend(mid)
		return
	_add_tear(a, b)
	_tear_notice("The skin tore!")


func _tear_notice(text: String) -> void:
	if Time.get_ticks_msec() - _tear_notice_msec > 3000:
		_tear_notice_msec = Time.get_ticks_msec()
		Surgery.current.announce(text, true)


## Wound depth (0..1) to how many tissue layers the cut goes through.
static func _tissue_depth(depth: float) -> int:
	if depth < SKIN_DEPTH:
		return TissueSim.Depth.SKIN
	return TissueSim.Depth.FAT if depth < Wound.MUSCLE_DEPTH else TissueSim.Depth.MUSCLE


@rpc("authority", "call_local", "reliable")
func _tissue_cut(a: Vector2, b: Vector2, depth: int) -> void:
	body.tissue.cut(a, b, depth)


@rpc("authority", "call_local", "reliable")
func _tissue_excise(k: int) -> void:
	body.tissue.excise(k)


@rpc("authority", "call_local", "reliable")
func _tissue_stitch(uv: Vector2, tension: float, strength: float) -> void:
	body.tissue.stitch(uv, tension, strength)


@rpc("authority", "call_local", "reliable")
func _tissue_stitch_path(points: PackedVector2Array, radius: float, tension: float, strength: float) -> void:
	body.tissue.stitch_path(points, radius, tension, strength)


@rpc("authority", "call_local", "reliable")
func _suture_anchor(id: int, uv: Vector2, layer: int, tension: float, strength: float, slack: float) -> void:
	body.tissue.thread_anchor(id, uv, layer, tension, strength, slack)


@rpc("authority", "call_local", "reliable")
func _suture_tension(id: int, tension: float) -> void:
	body.tissue.thread_tension(id, tension)


@rpc("authority", "call_local", "reliable")
func _suture_finish(id: int) -> void:
	body.tissue.finish_thread(id)


@rpc("authority", "call_local", "reliable")
func _suture_snap(id: int) -> void:
	body.tissue.snap_thread(id)


@rpc("authority", "call_local", "reliable")
func _tissue_muscle(uv: Vector2, radius: float) -> void:
	body.tissue.muscle_stitch(uv, radius)


@rpc("authority", "call_local", "reliable")
func _tissue_close_layer(points: PackedVector2Array, radius: float, depth: int) -> void:
	body.tissue.close_layer(points, radius, depth)


@rpc("authority", "call_local", "reliable")
func _tissue_pucker(points: PackedVector2Array, radius: float, amount: float) -> void:
	body.tissue.suture_pucker(points, radius, amount)


@rpc("authority", "call_local", "reliable")
func _tissue_burst(uv: Vector2, radius: float) -> void:
	body.tissue.burst(uv, radius)


## The host's sim already snapped the spring; clients mirror it.
@rpc("authority", "call_remote", "reliable")
func _tissue_snap(spring: int) -> void:
	body.tissue.snap_spring(spring)


@rpc("authority", "call_local", "reliable")
func _iv_placed(point: Vector3) -> void:
	iv_set = true
	_connect_iv(point)


@rpc("authority", "call_local", "reliable")
func _iv_removed() -> void:
	iv_set = false
	iv_in_vein = false
	Surgery.current.room.iv_line.detach()


@rpc("authority", "call_local", "reliable")
func _set_organ_damage(index: int, amount: float) -> void:
	body.set_organ_damage(index, amount)


@rpc("authority", "call_local", "reliable")
func _vocal(sound: String) -> void:
	Sfx.play(sound, body.global_position + Vector3(0.7, 0.2, 0), "Voice")


@rpc("authority", "call_local", "reliable")
func _set_orientation(value: int) -> void:
	body.set_orientation(value)
