class_name Lab
extends Node
## Blood panels, host only. Narrow panels come back faster than the full one. Shared cooldown.

const COOLDOWN := 60.0
const PANELS: Dictionary = {
	"glucose": {"label": "Glucose", "time": 15.0},
	"coag": {"label": "Clotting", "time": 25.0},
	"counts": {"label": "Blood count and type", "time": 25.0},
	"allergy": {"label": "Allergy screen", "time": 40.0},
	"full": {"label": "Full panel", "time": 60.0},
}

var cooldown_left := 0.0
var _pending: Array[Dictionary] = []


func request(kind: String, surgery: Surgery) -> void:
	if not PANELS.has(kind) or cooldown_left > 0.0:
		return
	cooldown_left = COOLDOWN
	_pending.append({"kind": kind, "eta": PANELS[kind].time})
	surgery.announce("Blood sample sent: %s (%d s)." % [PANELS[kind].label, PANELS[kind].time])


func tick(delta: float, surgery: Surgery) -> void:
	cooldown_left = maxf(cooldown_left - delta, 0.0)
	for order in _pending.duplicate():
		order.eta -= delta
		if order.eta <= 0.0:
			_pending.erase(order)
			surgery.publish_lab(result(order.kind, surgery.patient))


static func result(kind: String, patient: Patient) -> String:
	var v := patient.vitals
	var mods := patient.mods
	var lines := PackedStringArray()
	if kind in ["glucose", "full"]:
		lines.append("Glucose %.1f mmol/L %s" % [v.glucose, "HIGH" if v.glucose > 11.0 else "LOW" if v.glucose < 4.0 else "ok"])
	if kind in ["coag", "full"]:
		var bleed := mods.mult("bleed_mult")
		lines.append("Clotting: %s%s" % ["slow" if bleed > 1.3 else "fast" if bleed < 0.8 else "normal", ", clot risk" if mods.num("clot_risk") > 0.0 else ""])
	if kind in ["counts", "full"]:
		lines.append("Hb %d g/L, volume %d%%, type %s" % [140.0 * mods.mult("blood_ml_mult") * v.blood_ratio(), v.blood_ratio() * 100.0, patient.blood_type])
	if kind in ["allergy", "full"]:
		var allergens := mods.list("allergen")
		lines.append("Reacts to: %s" % (", ".join(allergens) if not allergens.is_empty() else "nothing found"))
	if kind == "full":
		lines.append("Bone density: %s" % ["very high" if mods.num("bone_hardness") >= 2.0 else "low" if mods.flag("bone_fragile") else "normal"])
		lines.append("Temp trend: %s" % ["rising" if mods.flag("mh_trigger") else "stable"])
	return "LAB: " + "\n".join(lines)
