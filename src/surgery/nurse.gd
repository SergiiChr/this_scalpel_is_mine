class_name Nurse
extends Node
## Tool requests, host only. One order at a time shared by both surgeons: a batch of up to BATCH items (the same one
## more than once too), fetched together, so it takes as long as its slowest item.
## Then the nurse needs a breather before the next one.
## The first few deliveries come without one.

const COOLDOWN := 15.0
const FREE_ORDERS := 5
const BATCH := 5

var cooldown_left := 0.0
var delivered := 0
## The order on its way: {"ids", "eta", "total"}, empty when there's none.
var _order: Dictionary = {}


func request(peer: int, tool_ids: PackedStringArray, surgery: Surgery) -> void:
	if tool_ids.is_empty() or tool_ids.size() > BATCH:
		return
	for id in tool_ids:
		var def := Db.tool(id)
		if def == null or not def.orderable:
			return
	if not _order.is_empty():
		surgery.tell(peer, "The nurse is still fetching the %s (%d s)." % [batch_text(_order.ids), ceili(_order.eta)])
		return
	if cooldown_left > 0.0:
		surgery.tell(peer, "The nurse is still busy (%d s)." % ceili(cooldown_left))
		return
	var surgeon: Surgeon = surgery.surgeons.get(peer)
	var delay := 0.0
	for id in tool_ids:
		delay = maxf(delay, delivery_time(Db.tool(id), surgeon, surgery))
	_order = {"ids": tool_ids, "eta": delay, "total": delay}
	surgery.sound("nurse_bell")
	surgery.announce("Nurse: \"%s. Give me %d seconds.\"" % [batch_text(tool_ids), ceili(delay)])


## How long one item takes the nurse to fetch for this surgeon (null for nobody in particular).
static func delivery_time(def: ToolDef, surgeon: Surgeon, surgery: Surgery) -> float:
	var delay := def.delay * (surgeon.mods.mult("nurse_delay_mult") if surgeon else 1.0) * surgery.run_mods.mult("nurse_delay_mult")
	return delay * surgery.run_mods.mult("blood_delay_mult") if def.drug.begins_with("blood") else delay


## "Gauze ×2, Scalpel": names in the order first asked for, repeats counted.
static func batch_text(tool_ids: PackedStringArray) -> String:
	var counts: Dictionary = {}
	for id in tool_ids:
		counts[id] = counts.get(id, 0) + 1
	var parts := PackedStringArray()
	for id: String in counts:
		parts.append(Db.tool(id).name + ("" if counts[id] == 1 else " ×%d" % counts[id]))
	return ", ".join(parts)


## For the board over the bell: [batch text, seconds left, fraction done], empty when nothing is on its way.
func order() -> Array:
	return [] if _order.is_empty() else [batch_text(_order.ids), _order.eta, 1.0 - _order.eta / _order.total]


func tick(delta: float, surgery: Surgery) -> void:
	cooldown_left = maxf(cooldown_left - delta, 0.0)
	if _order.is_empty():
		return
	_order.eta -= delta
	if _order.eta > 0.0:
		return
	var ids: PackedStringArray = _order.ids
	for i in ids.size():
		var spot := surgery.room.delivery_spot(i, ids.size())
		# Bottles come standing, cap up, ready to draw from.
		if Db.tool(ids[i]).tray == "bottles":
			surgery.tools.spawn_standing(ids[i], spot)
		else:
			surgery.tools.spawn(ids[i], spot)
	surgery.sound("nurse_delivery")
	surgery.announce("Nurse leaves the %s on the delivery tray." % batch_text(_order.ids))
	_order = {}
	delivered += 1
	if delivered > FREE_ORDERS:
		cooldown_left = COOLDOWN * surgery.run_mods.mult("nurse_cooldown_mult")
