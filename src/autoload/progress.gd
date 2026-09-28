extends Node
## Save file: unlocked quirks, best scenario results, known hosts. Stored in user://save.cfg.
## The host's save is what counts for scenario progress. Quirk unlocks are always local.

signal quirk_unlocked(quirk: QuirkDef, variant: String)

const PATH := "user://save.cfg"

var player_name := "Doctor"
var unlocked_quirks: PackedStringArray = []
## scenario id -> best star count (0-3)
var best_stars: Dictionary = {}
## [{"name": String, "ip": String, "port": int}]
var known_hosts: Array = []


func _ready() -> void:
	var cfg := ConfigFile.new()
	if cfg.load(PATH) != OK:
		return
	player_name = cfg.get_value("player", "name", player_name)
	unlocked_quirks = cfg.get_value("codex", "unlocked", unlocked_quirks)
	best_stars = cfg.get_value("scenarios", "best_stars", best_stars)
	known_hosts = cfg.get_value("network", "known_hosts", known_hosts)


func save() -> void:
	var cfg := ConfigFile.new()
	cfg.set_value("player", "name", player_name)
	cfg.set_value("codex", "unlocked", unlocked_quirks)
	cfg.set_value("scenarios", "best_stars", best_stars)
	cfg.set_value("network", "known_hosts", known_hosts)
	cfg.save(PATH)


func is_unlocked(quirk: QuirkDef) -> bool:
	return quirk.unlock_key() in unlocked_quirks


func unlock(quirk: QuirkDef, variant: String = "") -> void:
	if quirk == null or is_unlocked(quirk):
		return
	unlocked_quirks.append(quirk.unlock_key())
	save()
	quirk_unlocked.emit(quirk, variant)


func record_result(scenario_id: String, stars: int) -> void:
	best_stars[scenario_id] = maxi(best_stars.get(scenario_id, 0), stars)
	save()


func remember_host(host_name: String, ip: String, port: int) -> void:
	known_hosts = known_hosts.filter(func(h: Dictionary) -> bool: return h.ip != ip or h.port != port)
	known_hosts.push_front({"name": host_name, "ip": ip, "port": port})
	save()


func forget_host(ip: String, port: int) -> void:
	known_hosts = known_hosts.filter(func(h: Dictionary) -> bool: return h.ip != ip or h.port != port)
	save()
