class_name EventDirector
extends Node
## Escalation, host only. Rolls random events from the scenario's list, more often as time runs out,
## and fires scripted ones on schedule. Event tuning is in data/events.cfg.

const CALM_INTERVAL := 75.0
const FRANTIC_INTERVAL := 25.0
const RAMBLE_INTERVAL := 9.0

var _pool: PackedStringArray = []
var _scripted: Array = []
var _cooldowns: Dictionary = {}
var _next_roll := 40.0
var _ramble_timer := 6.0
var _ramble_index := 0
var _time_limit := 1200.0
var _rng := RandomNumberGenerator.new()
var _chart_update_at := INF


func setup(scenario: ScenarioDef, seed_value: int) -> void:
	_chart_update_at = (scenario.time_limit if scenario.time_limit > 0 else 900.0) * randf_range(0.25, 0.5)
	_pool = PackedStringArray(scenario.events)
	_scripted = scenario.scripted_events.duplicate(true)
	_time_limit = scenario.time_limit if scenario.time_limit > 0 else 1200.0
	_rng.seed = seed_value + 7


func tick(delta: float, surgery: Surgery) -> void:
	var elapsed := surgery.elapsed
	if surgery.run_mods.flag("card_error") and not surgery.chart_corrected and elapsed >= _chart_update_at:
		surgery.correct_chart()
	for event: Dictionary in _scripted:
		if not event.get("fired", false) and elapsed >= event.at:
			event.fired = true
			fire(event.id, surgery)
	for id: String in _cooldowns.keys():
		_cooldowns[id] -= delta
	var patient := surgery.patient
	if "panic_flail" in _pool and patient.vitals.panic > 0.8 and _off_cooldown("panic_flail"):
		fire("panic_flail", surgery)
	if "ramble" in _pool:
		_ramble_timer -= delta
		if _ramble_timer <= 0.0 and patient.ramble(_ramble_index):
			_ramble_index += 1
			_ramble_timer = RAMBLE_INTERVAL
	_next_roll -= delta
	if _next_roll > 0.0:
		return
	var intensity := clampf(elapsed / _time_limit, 0.0, 1.0)
	_next_roll = lerpf(CALM_INTERVAL, FRANTIC_INTERVAL, intensity) * _rng.randf_range(0.7, 1.3)
	var id := _pick(elapsed, surgery)
	if id:
		fire(id, surgery)


func fire(id: String, surgery: Surgery) -> void:
	_cooldowns[id] = Db.events.get_value(id, "cooldown", 30.0)
	var text: String = Db.events.get_value(id, "text", "")
	var patient := surgery.patient
	match id:
		"bleed_spike":
			patient.spontaneous_bleed(Vector2(_rng.randf_range(0.3, 0.7), _rng.randf_range(0.3, 0.7)))
			surgery.announce(text)
		"arrest":
			patient.arrest()
		"wake_up":
			if patient.vitals.anesthesia > 0.5:
				patient.wake_up()
				surgery.announce(text)
		"seizure":
			patient.start_seizure()
		"panic_flail":
			surgery.jolt_all(0.5, text)
		"cough":
			surgery.jolt_all(0.3, text)
		"pothole":
			surgery.jolt_all(0.6, text)
		"pedestrian_bump":
			var peers := surgery.surgeons.keys()
			surgery.jolt_peer(peers[_rng.randi_range(0, peers.size() - 1)], 0.9)
			surgery.announce(text)
		"lights_flicker":
			surgery.flicker_lights()


func _off_cooldown(id: String) -> bool:
	return _cooldowns.get(id, 0.0) <= 0.0


func _pick(elapsed: float, surgery: Surgery) -> String:
	var total := 0.0
	var options: Array = []
	for id in _pool + PackedStringArray(["lights_flicker"]):
		var weight: float = Db.events.get_value(id, "weight", 0.0)
		if id == "lights_flicker":
			weight += surgery.run_mods.num("flicker_weight")
		if weight <= 0.0 or not _off_cooldown(id) or elapsed < float(Db.events.get_value(id, "min_time", 0.0)):
			continue
		if Db.events.get_value(id, "requires", "") == "awake" and not surgery.patient.vitals.is_awake():
			continue
		total += weight
		options.append([id, weight])
	var roll := _rng.randf() * total
	for option: Array in options:
		roll -= option[1]
		if roll <= 0.0:
			return option[0]
	return ""
