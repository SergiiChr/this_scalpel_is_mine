class_name SurgeonStatus
extends RefCounted
## Personal gauges of the local surgeon: stress, sickness, breath, sweat and drink buffs.
## Runs only on the surgeon's own peer. update() returns event names for Surgeon to act on.

const BREATH_HOLD_TIME := 8.0
const BREATH_REFILL_TIME := 12.0
const PASS_OUT_TIME := 6.0

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
var since_coffee := 0.0
var _drip_timer := 20.0
var _cough_cooldown := 0.0


func _init(modifiers: Modifiers) -> void:
	mods = modifiers


func is_out() -> bool:
	return passed_out > 0.0


func add_stress(amount: float) -> void:
	stress = clampf(stress + amount * mods.mult("stress_mult"), 0.0, 1.0)


func add_sickness(amount: float) -> void:
	if not mods.flag("sickness_immune"):
		sickness = clampf(sickness + amount, 0.0, 1.0)


func tremor_amount() -> float:
	var amount := mods.num("tremor") + (0.0015 if coffee_left > 0.0 else 0.0) + maxf(stress - 0.6, 0.0) * 0.006
	if whiskey_left > 0.0 and mods.flag("drink_steady"):
		amount *= 0.2
	if holding_breath and breath > 0.0:
		amount *= 0.1
	return amount * mods.mult("tremor_mult")


func hand_speed() -> float:
	var speed := 1.3 if coffee_left > 0.0 else 1.0
	if mods.flag("caffeine") and coffee_left <= 0.0:
		speed *= 1.0 - minf(since_coffee / 300.0, 0.4)
	return speed


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


## context: {"bleed_rate": float, "partner_breath": float} -> events: "pass_out", "vomit", "cough", "slip", "drip", "gasp"
func update(delta: float, context: Dictionary) -> PackedStringArray:
	var events := PackedStringArray()
	if passed_out > 0.0:
		passed_out -= delta
		return events
	stress = maxf(stress - delta * 0.01, 0.0)
	coffee_left = maxf(coffee_left - delta, 0.0)
	whiskey_left = maxf(whiskey_left - delta, 0.0)
	since_coffee += delta
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
