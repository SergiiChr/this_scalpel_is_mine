class_name DrugEffects
extends RefCounted
## Sum of all active drug effects for one simulation tick. Field names match DrugDef.NUMERIC_KEYS.
## glucose and volume_ml are per-second rates, everything else is the current strength.

var hr := 0.0
var bp := 0.0
var glucose := 0.0
var volume_ml := 0.0
var anesthesia := 0.0
var local_block := 0.0
var sedation := 0.0
var pain_relief := 0.0
var clot := 0.0
var spo2 := 0.0
var temp := 0.0
var antihistamine := 0.0
var adrenaline := 0.0
var lethal := 0.0


func add(key: String, amount: float) -> void:
	set(key, float(get(key)) + amount)
