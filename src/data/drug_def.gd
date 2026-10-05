class_name DrugDef
extends RefCounted
## One entry of data/drugs.cfg. Field meaning is documented at the top of that file.

const NUMERIC_KEYS: PackedStringArray = [
	"hr", "bp", "glucose", "volume_ml", "anesthesia", "local_block", "sedation", "pain_relief", "clot", "spo2", "temp",
]
## Leeway around the right dose (as a share of it): anything in between works like the right dose.
const DOSE_LOW := 0.7
const DOSE_HIGH := 1.4
## Below this share of the right dose a drug only has a faint effect and doesn't do its job (restart, antibiotic...).
const DOSE_EFFECTIVE := 0.5
## From this share on the chart calls it an overdose.
const DOSE_OVERDOSE := 2.5
## A direct injection (into tissue, not a vein) works this much sooner.
const DIRECT_ONSET := 0.4

var id: String
var name: String
var onset: float
var duration: float
var flags: PackedStringArray
var danger_with: PackedStringArray
var blood_type: String
## The right dose per kg of body weight in `unit`, 0 when it isn't dosed (bags, masks, drinks).
var dose: float
var unit: String
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
	def.dose = cfg.get_value(section, "dose", 0.0)
	def.unit = cfg.get_value(section, "unit", "mg")
	for key in NUMERIC_KEYS:
		if cfg.has_section_key(section, key):
			def.effects[key] = float(cfg.get_value(section, key))
	return def


func has_flag(flag: String) -> bool:
	return flag in flags


func effect(key: String) -> float:
	return effects.get(key, 0.0)


## How strongly a dose works, from its share of the right dose. Roughly right counts as right.
static func dose_strength(share: float) -> float:
	if share < DOSE_LOW:
		return share / DOSE_LOW
	return share / DOSE_HIGH if share > DOSE_HIGH else 1.0
