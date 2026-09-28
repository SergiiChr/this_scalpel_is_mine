class_name DrugDef
extends RefCounted
## One entry of data/drugs.cfg. Field meaning is documented at the top of that file.

const NUMERIC_KEYS: PackedStringArray = [
	"hr", "bp", "glucose", "volume_ml", "anesthesia", "local_block", "sedation", "pain_relief", "clot", "spo2", "temp",
]

var id: String
var name: String
var onset: float
var duration: float
var flags: PackedStringArray
var danger_with: PackedStringArray
var blood_type: String
## Peak effect per NUMERIC_KEYS entry.
var effects: Dictionary = {}


static func from_config(cfg: ConfigFile, section: String) -> DrugDef:
	var def := DrugDef.new()
	def.id = section
	def.name = cfg.get_value(section, "name", section.capitalize())
	def.onset = cfg.get_value(section, "onset", 5.0)
	def.duration = cfg.get_value(section, "duration", 60.0)
	def.flags = PackedStringArray(cfg.get_value(section, "flags", []))
	def.danger_with = PackedStringArray(cfg.get_value(section, "danger_with", []))
	def.blood_type = cfg.get_value(section, "blood_type", "")
	for key in NUMERIC_KEYS:
		if cfg.has_section_key(section, key):
			def.effects[key] = float(cfg.get_value(section, key))
	return def


func has_flag(flag: String) -> bool:
	return flag in flags


func effect(key: String) -> float:
	return effects.get(key, 0.0)
