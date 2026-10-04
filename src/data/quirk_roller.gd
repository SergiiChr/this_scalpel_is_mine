class_name QuirkRoller
extends RefCounted
## Random quirk rolls. A roll is {"id": String, "variant": String}, plain dictionaries so they travel over RPC.

const MAX_ATTEMPTS := 64


static func roll_surgeon(rng: RandomNumberGenerator) -> Array[Dictionary]:
	var table: Dictionary = Db.surgeon_quirks
	var count := rng.randi_range(1, 3)
	for attempt in MAX_ATTEMPTS:
		var rolls := _pick(table, table.keys(), count, rng)
		if rolls.size() == 1 and (table[rolls[0].id] as QuirkDef).is_exclusive():
			return rolls
		if rolls.any(func(r: Dictionary) -> bool: return (table[r.id] as QuirkDef).is_exclusive()):
			continue
		if count < 3 or _is_balanced(rolls, table):
			return rolls
	return [{"id": "normal_dude", "variant": ""}]


static func roll_patient(scenario: ScenarioDef, rng: RandomNumberGenerator) -> Array[Dictionary]:
	var table: Dictionary = Db.patient_quirks
	var rolls: Array[Dictionary] = []
	rolls.assign(scenario.fixed_patient_quirks.duplicate(true))
	var pool: Array = scenario.patient_quirk_pool if not scenario.patient_quirk_pool.is_empty() else table.keys()
	pool = pool.filter(func(id: String) -> bool: return not rolls.any(func(r: Dictionary) -> bool: return r.id == id))
	pool = pool.filter(func(id: String) -> bool: return (table[id] as QuirkDef).fits_site(scenario.site))
	var count := rng.randi_range(scenario.patient_quirks_min, scenario.patient_quirks_max) - rolls.size()
	rolls.append_array(_pick(table, pool, count, rng))
	return rolls


static func _pick(table: Dictionary, pool: Array, count: int, rng: RandomNumberGenerator) -> Array[Dictionary]:
	var ids := pool.duplicate()
	var rolls: Array[Dictionary] = []
	for i in mini(count, ids.size()):
		var id: String = ids.pop_at(rng.randi_range(0, ids.size() - 1))
		var quirk: QuirkDef = table[id]
		var variant := "" if quirk.variants.is_empty() else quirk.variants[rng.randi_range(0, quirk.variants.size() - 1)]
		rolls.append({"id": id, "variant": variant})
	return rolls


## Mixed quirks count as both positive and negative.
static func _is_balanced(rolls: Array[Dictionary], table: Dictionary) -> bool:
	var polarities := rolls.map(func(r: Dictionary) -> String: return (table[r.id] as QuirkDef).polarity(r.variant))
	return ("positive" in polarities or "mixed" in polarities) and ("negative" in polarities or "mixed" in polarities)
