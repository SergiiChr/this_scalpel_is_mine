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
var manual: Array[ManualPage] = []
var dialogue := ConfigFile.new()
var events := ConfigFile.new()
var scoring := ConfigFile.new()
var consequences := ConfigFile.new()
var audio := ConfigFile.new()


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
			scenarios.append(scenario)
	scenarios.sort_custom(func(a: ScenarioDef, b: ScenarioDef) -> bool: return a.order < b.order)
	for file in _list(MANUAL_DIR, "txt"):
		manual.append(ManualPage.load_file(MANUAL_DIR.path_join(file)))
	dialogue = _load_cfg("res://data/dialogue/patient_lines.cfg")
	events = _load_cfg("res://data/events.cfg")
	scoring = _load_cfg("res://data/scoring.cfg")
	consequences = _load_cfg("res://data/consequences.cfg")
	audio = _load_cfg("res://data/audio.cfg")


func scenario(id: String) -> ScenarioDef:
	for s in scenarios:
		if s.id == id:
			return s
	return null


func tool(id: String) -> ToolDef:
	return tools.get(id)


func drug(id: String) -> DrugDef:
	return drugs.get(id)


func quirk(kind: QuirkDef.Kind, id: String) -> QuirkDef:
	return (patient_quirks if kind == QuirkDef.Kind.PATIENT else surgeon_quirks).get(id)


func _load_cfg(path: String) -> ConfigFile:
	var cfg := ConfigFile.new()
	var err := cfg.load(path)
	if err != OK:
		push_error("Can't read %s (%s)" % [path, error_string(err)])
	return cfg


func _list(dir: String, extension: String) -> PackedStringArray:
	var files := Array(DirAccess.get_files_at(dir)).filter(func(f: String) -> bool: return f.get_extension() == extension)
	files.sort()
	return PackedStringArray(files)
