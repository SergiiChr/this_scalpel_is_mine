class_name Nurse
extends Node
## Tool requests, host only. One global cooldown shared by both surgeons, delivery takes the tool's delay.

const COOLDOWN := 45.0

var cooldown_left := 0.0
var _pending: Array[Dictionary] = []


func request(peer: int, tool_id: String, surgery: Surgery) -> void:
	var def := Db.tool(tool_id)
	if def == null or not def.orderable:
		return
	if cooldown_left > 0.0:
		surgery.tell(peer, "The nurse is still busy (%d s)." % ceili(cooldown_left))
		return
	var surgeon: Surgeon = surgery.surgeons.get(peer)
	var delay := def.delay * (surgeon.mods.mult("nurse_delay_mult") if surgeon else 1.0)
	_pending.append({"id": tool_id, "eta": delay})
	cooldown_left = COOLDOWN
	Sfx.play("nurse_bell")
	surgery.announce("Nurse: \"%s. Give me %d seconds.\"" % [def.name, ceili(delay)])


func tick(delta: float, surgery: Surgery) -> void:
	cooldown_left = maxf(cooldown_left - delta, 0.0)
	for order in _pending.duplicate():
		order.eta -= delta
		if order.eta <= 0.0:
			_pending.erase(order)
			surgery.tools.spawn(order.id, surgery.room.delivery_spot())
			Sfx.play("nurse_delivery")
			surgery.announce("Nurse drops the %s on the cart and leaves." % Db.tool(order.id).name)
