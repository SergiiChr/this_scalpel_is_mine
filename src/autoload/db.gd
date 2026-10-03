extends Node
## Loads every file in res://data once at startup. Game code reads definitions from here and never parses files itself.

const PATIENT_QUIRKS := "res://data/quirks/patient_quirks.md"
const SURGEON_QUIRKS := "res://data/quirks/surgeon_quirks.md"
const SCENARIO_DIR := "res://data/scenarios"
const MANUAL_DIR := "res://data/manual"

var patient_quirks: Dictionary = {}
var surgeon_quirks: Dictionary = {}
var tools: Dictionary = {}
var drugs: Dictionary = {}
var scenarios: Array[ScenarioDef] = []
## Scenarios with disabled=true: not offered to play, still found by scenario(id) for tests.
var disabled_scenarios: Array[ScenarioDef] = []
var manual: Array[ManualPage] = []
var dialogue := ConfigFile.new()
var events := ConfigFile.new()
var scoring := ConfigFile.new()
var consequences := ConfigFile.new()
var audio := ConfigFile.new()
var run_modifiers := ConfigFile.new()
## Tool ids every surgery starts with, see data/starter_kit.cfg.
var starter_kit: Array = []
## Surgical sites on the patient body, see data/patient_sites.json.
var patient_sites: Dictionary = {}
## How each tool model's grip is fitted to the glove, per hand, see SurgeonHand.fit and tests/support/fit_grips.tscn.
var grip_fits: Dictionary = {}


func _ready() -> void:
	patient_quirks = QuirkParser.parse(PATIENT_QUIRKS, QuirkDef.Kind.PATIENT)
	surgeon_quirks = QuirkParser.parse(SURGEON_QUIRKS, QuirkDef.Kind.SURGEON)
	var cfg := _load_cfg("res://data/tools.cfg")
	for section in cfg.get_sections():
		tools[section] = ToolDef.from_config(cfg, section)
	cfg = _load_cfg("res://data/drugs.cfg")
	for section in cfg.get_sections():
		drugs[section] = DrugDef.from_config(cfg, section)
	for file in _list(SCENARIO_DIR, "cfg"):
		var scenario := ScenarioDef.load_file(SCENARIO_DIR.path_join(file))
		if scenario:
			(disabled_scenarios if scenario.disabled else scenarios).append(scenario)
	scenarios.sort_custom(func(a: ScenarioDef, b: ScenarioDef) -> bool: return a.order < b.order)
	for file in _list(MANUAL_DIR, "txt"):
		manual.append(ManualPage.load_file(MANUAL_DIR.path_join(file)))
	dialogue = _load_cfg("res://data/dialogue/patient_lines.cfg")
	events = _load_cfg("res://data/events.cfg")
	scoring = _load_cfg("res://data/scoring.cfg")
	consequences = _load_cfg("res://data/consequences.cfg")
	audio = _load_cfg("res://data/audio.cfg")
	run_modifiers = _load_cfg("res://data/run_modifiers.cfg")
	starter_kit = _load_cfg("res://data/starter_kit.cfg").get_value("starter_kit", "tools", [])
	patient_sites = _load_json("res://data/patient_sites.json")
	grip_fits = _load_json("res://data/grips.json")


func scenario(id: String) -> ScenarioDef:
	for s in scenarios + disabled_scenarios:
		if s.id == id:
			return s
	return null


func tool(id: String) -> ToolDef:
	return tools.get(id)


## How a hand's grip is fitted to this tool's model: {"lift": meters, "curl": [5 floats]} or empty.
func grip_fit(def: ToolDef, hand: int) -> Dictionary:
	var fits: Dictionary = grip_fits.get(def.model if def.model else def.id, {})
	return fits.get("left" if hand == 0 else "right", {})


func drug(id: String) -> DrugDef:
	return drugs.get(id)


## Combined effects of the given run modifier ids.
func run_modifier_effects(ids: Array) -> Modifiers:
	var mods := Modifiers.new()
	for id: String in ids:
		mods.add(Modifiers.parse_effects(run_modifiers.get_value(id, "effects", "")))
	return mods


func quirk(kind: QuirkDef.Kind, id: String) -> QuirkDef:
	return (patient_quirks if kind == QuirkDef.Kind.PATIENT else surgeon_quirks).get(id)


func _load_cfg(path: String) -> ConfigFile:
	var cfg := ConfigFile.new()
	var err := cfg.load(path)
	if err != OK:
		push_error("Can't read %s (%s)" % [path, error_string(err)])
	return cfg


func _load_json(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		return {}
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(path))
	return parsed if parsed is Dictionary else {}


func _list(dir: String, extension: String) -> PackedStringArray:
	var files := Array(DirAccess.get_files_at(dir)).filter(func(f: String) -> bool: return f.get_extension() == extension)
	files.sort()
	return PackedStringArray(files)
