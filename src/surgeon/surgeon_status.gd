class_name SurgeonStatus
extends RefCounted
## Personal gauges of the local surgeon: stress, sickness, breath, sweat, drink and smoke buffs, drugs given to them.
## Runs only on the surgeon's own peer. update() returns event names for Surgeon to act on.

const BREATH_HOLD_TIME := 8.0
const BREATH_REFILL_TIME := 12.0
const PASS_OUT_TIME := 6.0
## A smoke break: no stress gain for this long, hands and feet faster by SMOKE_SPEED.
const SMOKE_TIME := 180.0
const SMOKE_SPEED := 1.2
## Stress shakes the hands in steps. Up to SHAKE_VISUAL only the glove twitches now and then (see shiver()), the
## tool stays put. Up to SHAKE_LIGHT a light shake reaches the tool, above it a plain one that grows with stress.
## Quirks only change how fast stress builds and how low it can drain (stress_floor).
const SHAKE_VISUAL := 0.3
const SHAKE_LIGHT := 0.6
const LIGHT_TREMOR := 0.002
const PLAIN_TREMOR := 0.005
const MAX_TREMOR := 0.009
## The calm glove's twitch: how far (m) at SHAKE_VISUAL, how long one lasts and the wait between them (s).
const SHIVER := 0.0015
const SHIVER_TIME := 0.8
const SHIVER_GAP := Vector2(4.0, 10.0)
## A coffee keeps stress this much higher while it works.
const COFFEE_STRESS := 0.15
## Stacked stress floors stop here, short of passing out.
const MAX_FLOOR := 0.9
## A surgeon's weight without quirks (kg). Doses given to a surgeon are worked out from it, like a patient's.
const BASE_WEIGHT := 80.0
## A sedative (flag "benzo") at the right dose steadies the shaking of stress, blurs the view and delays hand moves
## by SEDATED_DELAY. From DrugDef.DOSE_HIGH times the dose on the view darkens and the delay grows, by up to
## OVERDOSE_DELAY at KNOCKOUT_SHARE. There the surgeon is knocked out for KNOCKOUT_TIME.
const SEDATED_DELAY := 0.1
const OVERDOSE_DELAY := 0.2
const KNOCKOUT_SHARE := 2.0
const KNOCKOUT_TIME := 300.0
## Knocked out, a moan every so often (s, random in between).
const MOAN_GAP := Vector2(8.0, 20.0)

var mods: Modifiers
var stress := 0.0
var sickness := 0.0
var breath := 1.0
var sweat := 0.0
var holding_breath := false
var cap_on := false
var passed_out := 0.0
var coffee_left := 0.0
var whiskey_left := 0.0
var smoke_left := 0.0
var since_coffee := 0.0
var _drip_timer := 20.0
## Broken heating run modifier: stiff, slightly shaky fingers for everyone.
var cold_tremor := 0.0
var _cough_cooldown := 0.0
var weight_kg := BASE_WEIGHT
## Drugs given to this surgeon, every injection adding up (shares of the right dose for weight_kg).
var drugs := DrugLevels.new()
## How calm a sedative makes the surgeon now (0..1, 1 from the right dose) and how far past the right dose it is (0 up
## to DOSE_HIGH, 1 at KNOCKOUT_SHARE).
var calm := 0.0
var overdose := 0.0
## Seconds left knocked out by a sedative, and of being kept up meanwhile by a stimulant.
var knocked_out := 0.0
var kept_up := 0.0
## Set once a sedative knocks the surgeon out, so the same dose doesn't do it again.
var _knocked := false
var _shiver_wait := 3.0
var _shiver_left := 0.0
var _moan_wait := 5.0


func _init(modifiers: Modifiers) -> void:
	mods = modifiers
	weight_kg = weight_of(modifiers)


## A surgeon's weight (kg) with these quirks.
static func weight_of(modifiers: Modifiers) -> float:
	return BASE_WEIGHT + modifiers.num("weight_kg")


func is_out() -> bool:
	return passed_out > 0.0 or is_knocked_out()


func is_knocked_out() -> bool:
	return knocked_out > 0.0 and kept_up <= 0.0


func add_stress(amount: float) -> void:
	if smoke_left > 0.0:
		return
	stress = clampf(stress + amount * mods.mult("stress_mult"), 0.0, 1.0)


func add_sickness(amount: float) -> void:
	if not mods.flag("sickness_immune"):
		sickness = clampf(sickness + amount, 0.0, 1.0)


## How low stress drains: quirks set it, coffee lifts it, a steadying drink takes it away.
func stress_floor() -> float:
	if whiskey_left > 0.0 and mods.flag("drink_steady"):
		return 0.0
	return minf(mods.num("stress_floor") + (COFFEE_STRESS if coffee_left > 0.0 else 0.0), MAX_FLOOR)


## Shake that moves the tool (m): stress, which a sedative steadies, and cold, which it doesn't.
func tremor_amount() -> float:
	var shake := 0.0
	if stress > SHAKE_LIGHT:
		shake = lerpf(PLAIN_TREMOR, MAX_TREMOR, (stress - SHAKE_LIGHT) / (1.0 - SHAKE_LIGHT))
	elif stress > SHAKE_VISUAL:
		shake = LIGHT_TREMOR
	return _steadied(shake * (1.0 - calm) + cold_tremor)


## The glove's twitch while stress is low (m): now and then, and the glove only.
func shiver() -> float:
	if _shiver_left <= 0.0 or stress > SHAKE_VISUAL:
		return 0.0
	return _steadied(SHIVER * stress / SHAKE_VISUAL * (1.0 - calm))


func _steadied(amount: float) -> float:
	if holding_breath and breath > 0.0:
		amount *= 0.1
	return amount * mods.mult("tremor_mult")


## How long a sedative holds back what the mouse does to a hand (s).
func input_delay() -> float:
	return SEDATED_DELAY * calm + OVERDOSE_DELAY * overdose


func hand_speed() -> float:
	var speed := (1.3 if coffee_left > 0.0 else 1.0) * _smoke_speed()
	if mods.flag("caffeine") and coffee_left <= 0.0:
		speed *= 1.0 - minf(since_coffee / 300.0, 0.4)
	return speed


func move_speed() -> float:
	return mods.mult("move_speed_mult") * _smoke_speed()


func _smoke_speed() -> float:
	return SMOKE_SPEED if smoke_left > 0.0 else 1.0


func smoke() -> void:
	smoke_left = SMOKE_TIME


## A dose (in the drug's unit) injected into this surgeon. Only sedatives and what wakes from them do anything.
func administer(drug_id: String, amount: float) -> void:
	var def := Db.drug(drug_id)
	if def and def.dose > 0.0:
		drugs.give(def, amount / (def.dose * weight_kg), def.onset * DrugDef.DIRECT_ONSET)


func drink(tool_id: String) -> void:
	match tool_id:
		"coffee_thermos":
			coffee_left = 60.0
			since_coffee = 0.0
		"whiskey_flask":
			whiskey_left = 45.0
			stress = maxf(stress - 0.3, 0.0)
		"surgical_cap":
			cap_on = true


## context: {"bleed_rate": float, "partner_breath": float} -> events: "pass_out", "vomit", "cough", "slip", "drip", "gasp",
## "knocked_out", "came_round", "moan"
func update(delta: float, context: Dictionary) -> PackedStringArray:
	var events := PackedStringArray()
	_update_drugs(delta, events)
	if is_knocked_out():
		_moan_wait -= delta
		if _moan_wait <= 0.0:
			_moan_wait = randf_range(MOAN_GAP.x, MOAN_GAP.y)
			events.append("moan")
		return events
	if passed_out > 0.0:
		passed_out -= delta
		return events
	stress = maxf(stress - delta * 0.01, stress_floor())
	coffee_left = maxf(coffee_left - delta, 0.0)
	whiskey_left = maxf(whiskey_left - delta, 0.0)
	smoke_left = maxf(smoke_left - delta, 0.0)
	since_coffee += delta
	_shiver_left -= delta
	_shiver_wait -= delta
	if _shiver_wait <= 0.0:
		_shiver_wait = randf_range(SHIVER_GAP.x, SHIVER_GAP.y)
		_shiver_left = SHIVER_TIME
	if holding_breath:
		breath -= delta / BREATH_HOLD_TIME
		if breath <= 0.0:
			breath = 0.0
			holding_breath = false
			events.append("gasp")
	else:
		breath = minf(breath + delta / BREATH_REFILL_TIME, 1.0)
	add_sickness(context.get("partner_breath", 0.0) * delta)
	add_sickness(mods.num("blood_sickness_rate") * clampf(context.get("bleed_rate", 0.0) / 2.0, 0.0, 1.0) * delta)
	sickness = maxf(sickness - delta * 0.005, 0.0)
	sweat = minf(sweat + mods.num("sweat_rate") * delta * (1.0 + stress), 1.0)
	_cough_cooldown -= delta
	if _cough_cooldown <= 0.0 and randf() < mods.num("cough_chance") * delta:
		_cough_cooldown = 20.0
		events.append("cough")
	if sweat > 0.85 and randf() < 0.02 * delta * 10.0:
		events.append("slip")
	if sweat > 0.5 and not cap_on:
		_drip_timer -= delta
		if _drip_timer <= 0.0:
			_drip_timer = 25.0
			events.append("drip")
	if stress >= 1.0:
		passed_out = PASS_OUT_TIME
		stress = 0.5
		events.append("pass_out")
	if sickness >= 1.0:
		sickness = 0.3
		events.append("vomit")
	return events


## Sedatives add up into calm and overdose, and knock the surgeon out at KNOCKOUT_SHARE. Flumazenil (reverse_benzo)
## takes them all away; a stimulant only keeps a knocked out surgeon up while it lasts.
func _update_drugs(delta: float, events: PackedStringArray) -> void:
	for crossed: Array in drugs.update(delta, func(_def: DrugDef) -> float: return 1.0):
		var def: DrugDef = crossed[0]
		if crossed[1] != "works":
			continue
		if def.has_flag("reverse_benzo"):
			drugs.remove(func(d: DrugDef) -> bool: return d.has_flag("benzo"))
			if knocked_out > 0.0:
				knocked_out = 0.0
				kept_up = 0.0
				events.append("came_round")
		elif def.has_flag("stimulant") and knocked_out > 0.0:
			kept_up = def.duration
			events.append("came_round")
	var sedative := 0.0
	for entry: Dictionary in drugs.entries.values():
		if (entry.def as DrugDef).has_flag("benzo"):
			sedative += entry.level
	calm = minf(DrugDef.dose_strength(sedative), 1.0)
	overdose = clampf((sedative - DrugDef.DOSE_HIGH) / (KNOCKOUT_SHARE - DrugDef.DOSE_HIGH), 0.0, 1.0)
	if sedative >= KNOCKOUT_SHARE and not _knocked:
		_knocked = true
		knocked_out = KNOCKOUT_TIME
		passed_out = 0.0
		events.append("knocked_out")
	elif sedative < DrugDef.DOSE_HIGH:
		_knocked = false
	if knocked_out <= 0.0:
		kept_up = 0.0
		return
	knocked_out = maxf(knocked_out - delta, 0.0)
	if kept_up > 0.0:
		kept_up = maxf(kept_up - delta, 0.0)
		if kept_up == 0.0:
			# The stimulant wore off: still too much sedative in the blood and they go down again.
			if sedative >= KNOCKOUT_SHARE and knocked_out > 0.0:
				events.append("knocked_out")
			else:
				knocked_out = 0.0
	elif knocked_out == 0.0:
		events.append("came_round")
