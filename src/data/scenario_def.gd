class_name ScenarioDef
extends RefCounted
## One data/scenarios/*.cfg file. See docs/DESIGN.md#scenarios for the format.

var id: String
var order: int
var title: String
var difficulty: int
var group: String
## Teaser for the menu and lobby. Never says what to do.
var description: String
## What the patient chart says brought them in: symptoms and history only, no treatment plan.
var complaint: String
var hidden: bool
## Left out of the menu and lobby while its positive flow test is broken (the cfg names the test).
var disabled: bool
var time_limit: float
var anesthesia: String
var site: String
var side: String
var start_orientation: int
var patient_age: String
var environment: String
var nurse: bool
var dirty_start: bool
var patient_quirks_min: int
var patient_quirks_max: int
var patient_quirk_pool: Array
var fixed_patient_quirks: Array
var starting_tools: Array
var random_tools: Array
var random_tool_count: int
var missing_tool_chance: float
var events: Array
var scripted_events: Array
var start_vitals: Dictionary
var preop: Dictionary
var wounds: Array
var burns: Array
var internal: Array
var targets: Array
var steps: Array


static func load_file(path: String) -> ScenarioDef:
	var cfg := ConfigFile.new()
	if cfg.load(path) != OK:
		push_error("Can't read scenario %s" % path)
		return null
	var def := ScenarioDef.new()
	def.id = path.get_file().get_basename().substr(3)
	for key: String in cfg.get_section_keys("scenario"):
		def.set(key, cfg.get_value("scenario", key))
	for section: String in ["patient", "objectives"]:
		for key: String in cfg.get_section_keys(section):
			def.set(key, cfg.get_value(section, key))
	return def


## Picks the tools that actually spawn on the tray for this run: the starter kit plus the scenario's own tools.
## With a nurse, the scenario adds only what she can't fetch; the rest has to be ordered.
func roll_tools(rng: RandomNumberGenerator, extra_missing_chance: float = 0.0) -> PackedStringArray:
	var own := starting_tools.duplicate()
	var extras := random_tools.duplicate()
	for i in mini(random_tool_count, extras.size()):
		own.append(extras.pop_at(rng.randi_range(0, extras.size() - 1)))
	if nurse:
		own = own.filter(func(id: String) -> bool: return Db.tool(id) == null or not Db.tool(id).orderable)
	# The starter kit already covers one of each of its tools.
	for id: String in Db.starter_kit:
		own.erase(id)
	var tools := PackedStringArray()
	for tool_id: String in Db.starter_kit + own:
		if rng.randf() >= missing_tool_chance + extra_missing_chance:
			tools.append(tool_id)
	return tools


func stars_text() -> String:
	return "★".repeat(difficulty) + "☆".repeat(5 - difficulty)
