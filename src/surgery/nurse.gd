class_name Nurse
extends Node
## Tool requests, host only. One order at a time shared by both surgeons: the delivery takes the tool's delay,
## then the nurse needs a breather before the next one. The first few deliveries come without one.

const COOLDOWN := 15.0
const FREE_ORDERS := 5

var cooldown_left := 0.0
var delivered := 0
## The order on its way: {"id", "eta", "total"}, empty when there's none.
var _order: Dictionary = {}


func request(peer: int, tool_id: String, surgery: Surgery) -> void:
	var def := Db.tool(tool_id)
	if def == null or not def.orderable:
		return
	if not _order.is_empty():
		surgery.tell(peer, "The nurse is still fetching the %s (%d s)." % [Db.tool(_order.id).name, ceili(_order.eta)])
		return
	if cooldown_left > 0.0:
		surgery.tell(peer, "The nurse is still busy (%d s)." % ceili(cooldown_left))
		return
	var surgeon: Surgeon = surgery.surgeons.get(peer)
	var delay := def.delay * (surgeon.mods.mult("nurse_delay_mult") if surgeon else 1.0) * surgery.run_mods.mult("nurse_delay_mult")
	if def.drug.begins_with("blood"):
		delay *= surgery.run_mods.mult("blood_delay_mult")
	_order = {"id": tool_id, "eta": delay, "total": delay}
	surgery.sound("nurse_bell")
	surgery.announce("Nurse: \"%s. Give me %d seconds.\"" % [def.name, ceili(delay)])


## For the board over the bell: [tool name, seconds left, fraction done], empty when nothing is on its way.
func order() -> Array:
	return [] if _order.is_empty() else [Db.tool(_order.id).name, _order.eta, 1.0 - _order.eta / _order.total]


func tick(delta: float, surgery: Surgery) -> void:
	cooldown_left = maxf(cooldown_left - delta, 0.0)
	if _order.is_empty():
		return
	_order.eta -= delta
	if _order.eta <= 0.0:
		# Bottles come standing, cap up, ready to draw from.
		if Db.tool(_order.id).tray == "bottles":
			surgery.tools.spawn_standing(_order.id, surgery.room.delivery_spot())
		else:
			surgery.tools.spawn(_order.id, surgery.room.delivery_spot())
		surgery.sound("nurse_delivery")
		surgery.announce("Nurse leaves the %s on the delivery tray." % Db.tool(_order.id).name)
		_order = {}
		delivered += 1
		if delivered > FREE_ORDERS:
			cooldown_left = COOLDOWN * surgery.run_mods.mult("nurse_cooldown_mult")
