class_name DrugLevels
extends RefCounted
## How much of each drug is in a body, as a share of the right dose for its weight (DrugDef.dose), however many
## injections it came in: ten 1 ml shots add up to one 10 ml shot. A dose goes into a depot first and soaks into the
## blood over its route's onset; the level in the blood then drops by one right dose every `duration` seconds, so twice
## the dose lasts twice as long. From DrugDef.DOSE_EFFECTIVE in the blood the drug does its job. From DOSE_OVERDOSE
## taken in (in the blood or still soaking in, so one big dose counts in full though some wears off meanwhile) it's an
## overdose; only for drugs that are dosed (DrugDef.dose), not bags of fluid or blood.

## Below this share of DOSE_EFFECTIVE a drug that worked counts as worn off, so another dose makes it work again.
const WORN_OFF := 0.5
## What's left of a depot once it's this small soaks in at once.
const DEPOT_LEFT := 0.0001

## Drug id -> {"def": DrugDef, "depot": share still soaking in, "level": share in the blood, "onset": seconds the
## last dose takes to soak in, "working": seconds since it reached DOSE_EFFECTIVE (-1 while it hasn't), "overdosed"}.
var entries: Dictionary = {}


## Adds `share` of the right dose, soaking in over `onset` seconds. True when the drug wasn't in the body yet.
func give(def: DrugDef, share: float, onset: float) -> bool:
	var fresh := not entries.has(def.id)
	if fresh:
		entries[def.id] = {"def": def, "depot": 0.0, "level": 0.0, "onset": onset, "working": -1.0, "overdosed": false}
	var entry: Dictionary = entries[def.id]
	entry.depot += share
	entry.onset = maxf(onset, 0.1)
	return fresh


## Moves every drug on by `dt` seconds: soaking in, then wearing off as fast as `wear` (DrugDef, level -> float: 1 for a
## dose over its duration, 0 holds it) says. Returns what crossed a threshold: [[DrugDef, "works" or "overdose"], ...].
func update(dt: float, wear: Callable) -> Array:
	var crossed: Array = []
	for id: String in entries.keys():
		var entry: Dictionary = entries[id]
		var def: DrugDef = entry.def
		# All of it as it came in, before this step wears any off.
		var taken: float = entry.level + entry.depot
		# Fast at first, most of it in by the onset.
		var soaked: float = entry.depot if entry.depot < DEPOT_LEFT else entry.depot * (1.0 - exp(-3.0 * dt / entry.onset))
		entry.depot -= soaked
		entry.level = maxf(entry.level + soaked - dt / maxf(def.duration, 0.01) * float(wear.call(def, entry.level)), 0.0)
		if entry.working < 0.0 and entry.level >= DrugDef.DOSE_EFFECTIVE:
			entry.working = 0.0
			crossed.append([def, "works"])
		elif entry.working >= 0.0:
			entry.working = -1.0 if entry.level < DrugDef.DOSE_EFFECTIVE * WORN_OFF else entry.working + dt
		if not entry.overdosed and def.dose > 0.0 and taken >= DrugDef.DOSE_OVERDOSE:
			entry.overdosed = true
			crossed.append([def, "overdose"])
		elif entry.overdosed and taken < DrugDef.DOSE_HIGH:
			entry.overdosed = false
		if entry.level <= 0.0 and entry.depot <= 0.0:
			entries.erase(id)
	return crossed


## The share of the right dose of this drug in the blood (0 when there's none).
func level(id: String) -> float:
	return entries[id].level if entries.has(id) else 0.0


## Whether a drug with this flag is in at an effective level.
func working(flag: String) -> bool:
	return entries.values().any(func(e: Dictionary) -> bool: return e.working >= 0.0 and (e.def as DrugDef).has_flag(flag))


## Takes every drug `gone` (DrugDef -> bool) says out of the body.
func remove(gone: Callable) -> void:
	for id: String in entries.keys():
		if gone.call(entries[id].def):
			entries.erase(id)
