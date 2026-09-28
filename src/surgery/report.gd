class_name Report
extends RefCounted
## Builds the post-op report on the host: score lines, delayed consequences, stars.
## Returns a plain dictionary so it can be sent to every peer as is.


static func build(surgery: Surgery, success: bool, reason: String) -> Dictionary:
	var patient := surgery.patient
	var scenario := surgery.scenario
	var rng := RandomNumberGenerator.new()
	rng.seed = Net.session_seed + 99
	var retained := surgery.tools.retained_count()
	if retained > 0:
		patient.add_flag("retained_tool", retained)
		surgery.scoring.add("retained_item")
	var opened := patient.wounds.any(func(w: Wound) -> bool: return w.made_by_surgeon and w.depth >= 0.7)
	if opened and not patient.flags.has("antibiotic"):
		patient.add_flag("no_antibiotic_open")

	var total: int = Db.scoring.get_value("stars", "base", 50) + surgery.scoring.points
	var consequences: Array = []
	if success:
		for section in Db.consequences.get_sections():
			var cfg := Db.consequences
			if patient.flags.get(cfg.get_value(section, "flag"), 0.0) < float(cfg.get_value(section, "min", 1)):
				continue
			if rng.randf() > float(cfg.get_value(section, "chance", 1.0)):
				continue
			var points: int = cfg.get_value(section, "points", 0)
			total += points
			consequences.append([str(cfg.get_value(section, "text", "")).replace("{site}", scenario.site.replace("_", " ")), points])
			if cfg.get_value(section, "stop", false):
				break
		if scenario.time_limit > 0:
			var bonus := int((scenario.time_limit - surgery.elapsed) / 20.0) * int(Db.scoring.get_value("time_bonus", "points", 1))
			total += bonus
			consequences.append(["Finished with %d s to spare." % int(scenario.time_limit - surgery.elapsed), bonus])

	var events: Array = []
	for entry: Dictionary in surgery.scoring.entries.values():
		events.append([entry.text, entry.points, entry.count])
	events.sort_custom(func(a: Array, b: Array) -> bool: return absi(a[1]) > absi(b[1]))
	return {
		"scenario": scenario.id,
		"success": success,
		"reason": reason,
		"score": total,
		"stars": Scoring.stars_for(total, success),
		"events": events,
		"consequences": consequences,
		"patient_quirks": patient.rolls,
		"time": surgery.elapsed,
	}
