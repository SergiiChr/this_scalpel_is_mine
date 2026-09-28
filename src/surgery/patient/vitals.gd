class_name Vitals
extends RefCounted
## Live patient numbers. Loosely realistic, tuned for play. Host simulates, clients receive a copy.

enum Rhythm { SINUS, VFIB, ASYSTOLE }

const NORMAL_BLOOD_ML := 5000.0
const SYNCED: PackedStringArray = [
	"heart_rate", "systolic", "spo2", "temperature", "glucose", "blood_ml", "max_blood_ml", "rhythm",
	"consciousness", "anesthesia", "local_block", "pain", "panic", "bleed_rate", "swelling", "seizing",
]

var heart_rate := 75.0
var systolic := 120.0
var spo2 := 98.0
var temperature := 36.8
var glucose := 5.5
var blood_ml := NORMAL_BLOOD_ML
var max_blood_ml := NORMAL_BLOOD_ML
var rhythm := Rhythm.SINUS
var consciousness := 1.0
var anesthesia := 0.0
var local_block := 0.0
var pain := 0.0
var panic := 0.0
## Total ml/s across every wound, for the HUD and the Hemophobia quirk.
var bleed_rate := 0.0
var swelling := 0.0
var seizing := false


func is_awake() -> bool:
	return consciousness > 0.45


func blood_ratio() -> float:
	return blood_ml / max_blood_ml


func is_arrested() -> bool:
	return rhythm != Rhythm.SINUS


func rhythm_name() -> String:
	return ["Sinus", "V-FIB", "ASYSTOLE"][rhythm]


func to_dict() -> Dictionary:
	var out: Dictionary = {}
	for key in SYNCED:
		out[key] = get(key)
	return out


func from_dict(data: Dictionary) -> void:
	for key: String in data:
		set(key, data[key])
