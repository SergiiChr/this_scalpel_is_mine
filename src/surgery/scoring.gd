class_name Scoring
extends Node
## Good and bad actions, host only. Values come from data/scoring.cfg.
## Every scored event also rattles both surgeons by its stress value.

const THROTTLE_SECONDS := 4.0

var points := 0
## id -> {"text": String, "points": int, "count": int}
var entries: Dictionary = {}
var _last_time: Dictionary = {}


## throttled: repeated calls within a few seconds count once (for things that fire every frame).
func add(id: String, throttled: bool = false) -> void:
	if not multiplayer.is_server() or not Db.scoring.has_section(id):
		return
	var now := Time.get_ticks_msec() * 0.001
	if throttled and now - float(_last_time.get(id, -INF)) < THROTTLE_SECONDS:
		return
	_last_time[id] = now
	var value: int = Db.scoring.get_value(id, "points", 0)
	points += value
	var entry: Dictionary = entries.get(id, {"text": Db.scoring.get_value(id, "text", id), "points": 0, "count": 0})
	entry.points += value
	entry.count += 1
	entries[id] = entry
	var stress: float = Db.scoring.get_value(id, "stress", 0.0)
	if stress > 0.0 and Surgery.current:
		Surgery.current.broadcast_stress(stress)


static func stars_for(total: int, passed: bool) -> int:
	if not passed:
		return 0
	var two: int = Db.scoring.get_value("stars", "two_star", 60)
	var three: int = Db.scoring.get_value("stars", "three_star", 120)
	return 3 if total >= three else 2 if total >= two else 1
