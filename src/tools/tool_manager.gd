class_name ToolManager
extends Node3D
## Owns every tool in the room. Peers send requests (grab, release, belt), the host decides and broadcasts.
## Initial tools spawn deterministically on every peer, later ones (nurse deliveries) come from the host.

const SYNC_INTERVAL := 0.1
const GRAB_RADIUS := 0.09
## Everyone but the host sees iodine levels in steps this fine (syringes, vials and the kidney dish are exact).
const FILL_STEPS := 50.0
## How close a syringe's needle has to be to a vial's middle to be in it, or to a dish's or hung bag's (a share of
## its length). A hung bag is out of a hand's reach from below: the needle goes in resting on top of it.
const VIAL_REACH := 0.05
const DISH_REACH := 0.4
const DRIP_REACH := 0.75

var tools: Dictionary = {}
var _next_uid := 1
var _sync_acc := 0.0


## station_tools: [[id, Transform3D], ...] that sit on their own station (see Room.station_tools()).
## A tool the station provides is never also put on the tray.
func spawn_initial(tray_ids: PackedStringArray, tray_spots: Array[Vector3], personal: Dictionary, station_tools: Array = []) -> void:
	for entry: Array in station_tools:
		var at_station: String = entry[0]
		while tray_ids.has(at_station):
			tray_ids.remove_at(tray_ids.find(at_station))
		var made := _create(_next_uid, at_station, entry[1])
		if made.def.fixed:
			made.set_state(SurgicalTool.State.STANDING, 0, -1)
	var spot := 0
	var groups: Dictionary = {}
	for id in tray_ids:
		var tool := _create(_next_uid, id, Transform3D(Basis.IDENTITY, tray_spots[spot % tray_spots.size()]))
		(groups.get_or_add(tool.def.tray, []) as Array).append(tool)
		spot += 1
	if Surgery.current and Surgery.current.room:
		for group: String in groups:
			_lay_out(groups[group], Surgery.current.room.tray_zone(group))
	var peers := personal.keys()
	peers.sort()
	for peer: int in peers:
		var surgeon: Surgeon = Surgery.current.surgeons[peer]
		var belt_slot := 0
		for id: String in personal[peer]:
			var tool := _create(_next_uid, id, Transform3D(Basis.IDENTITY, tray_spots[spot % tray_spots.size()]))
			if belt_slot < surgeon.belt_capacity():
				tool.set_state(SurgicalTool.State.BELT, peer, belt_slot)
				belt_slot += 1
			else:
				spot += 1


## Lays a group of tools out side by side in its spot on the tray (Room.tray_zone()), in columns, each resting right on
## it: nothing overlaps, so nothing gets shoved into the tray or its neighbour when the physics starts.
## Longest first; what doesn't fit lies on top of the ones already there.
func _lay_out(group: Array, zone: AABB) -> void:
	const GAP := 0.012
	var sorted := group.duplicate()
	sorted.sort_custom(func(a: SurgicalTool, b: SurgicalTool) -> bool: return a.bounds.size.z > b.bounds.size.z)
	var x := zone.position.x
	var z := zone.position.z
	var column := 0.0
	var layer := 0.0
	var layer_height := 0.0
	for tool: SurgicalTool in sorted:
		var size := tool.bounds.size
		if z + size.z > zone.end.z:
			x += column + GAP
			z = zone.position.z
			column = 0.0
		if x + size.x > zone.end.x:
			x = zone.position.x
			layer += layer_height + 0.001
			layer_height = 0.0
		var at := Vector3(x - tool.bounds.position.x, zone.position.y + layer - tool.bounds.position.y + 0.001, z - tool.bounds.position.z)
		tool.global_transform = Transform3D(Basis.IDENTITY, at)
		z += size.z + GAP
		column = maxf(column, size.x)
		layer_height = maxf(layer_height, size.y)


func tool_in_hand(peer: int, hand: int) -> SurgicalTool:
	for tool: SurgicalTool in tools.values():
		if tool.state == SurgicalTool.State.HELD and tool.holder == peer and tool.slot == hand:
			return tool
	return null


func tool_on_belt(peer: int, belt_slot: int) -> SurgicalTool:
	for tool: SurgicalTool in tools.values():
		if tool.state == SurgicalTool.State.BELT and tool.holder == peer and tool.slot == belt_slot:
			return tool
	return null


func nearest_grabbable(at: Vector3) -> SurgicalTool:
	var best: SurgicalTool = null
	var best_dist := GRAB_RADIUS
	for tool: SurgicalTool in tools.values():
		if tool.state in [SurgicalTool.State.FREE, SurgicalTool.State.STANDING, SurgicalTool.State.INSIDE] and not tool.def.fixed:
			var dist := minf(tool.global_position.distance_to(at), tool.tip_position().distance_to(at))
			if dist < best_dist:
				best_dist = dist
				best = tool
	return best


## The closest tool of this kind within reach of a point (measured to its middle), wherever it is but on a belt or used up.
func nearest_of(id: String, at: Vector3, reach: float) -> SurgicalTool:
	return _nearest(at, reach, func(tool: SurgicalTool) -> bool: return tool.def.id == id)


func _nearest(at: Vector3, reach: float, wanted: Callable) -> SurgicalTool:
	var best: SurgicalTool = null
	var best_dist := reach
	for tool: SurgicalTool in tools.values():
		if wanted.call(tool) and not tool.state in [SurgicalTool.State.BELT, SurgicalTool.State.CONSUMED]:
			var dist := middle(tool).distance_to(at)
			if dist < best_dist:
				best_dist = dist
				best = tool
	return best


## The vial or dish a syringe's needle at `at` is in, or null: anything else that holds liquid, closest first.
func nearest_container(at: Vector3) -> SurgicalTool:
	var best: SurgicalTool = null
	var best_dist := INF
	for tool: SurgicalTool in tools.values():
		if tool.def.volume <= 0.0 or tool.def.action == "syringe" or tool.state in [SurgicalTool.State.BELT, SurgicalTool.State.CONSUMED]:
			continue
		var dist := middle(tool).distance_to(at)
		var reach := VIAL_REACH if tool.def.action == "vial" else tool.def.length * (DRIP_REACH if tool.def.action == "drip" else DISH_REACH)
		if dist < reach and dist < best_dist:
			best_dist = dist
			best = tool
	return best


## True while a held syringe's needle is in this vial, dish or bag.
func needle_in(container: SurgicalTool) -> bool:
	for tool: SurgicalTool in tools.values():
		if tool.state == SurgicalTool.State.HELD and tool.def.action == "syringe" and nearest_container(tool.tip_position()) == container:
			return true
	return false


## The bag hanging on the IV stand, null where there's none.
func drip_bag() -> SurgicalTool:
	for tool: SurgicalTool in tools.values():
		if tool.def.action == "drip":
			return tool
	return null


static func middle(tool: SurgicalTool) -> Vector3:
	return tool.global_transform * Vector3(0, 0, -tool.def.length * 0.5)


## What this tool holds at its tip (a cotton pad in forceps), or null.
func carried_by(tool: SurgicalTool) -> SurgicalTool:
	for other: SurgicalTool in tools.values():
		if other.state == SurgicalTool.State.CARRIED and other.holder == tool.uid:
			return other
	return null


# --- Requests (any peer) ---------------------------------------------------------------------------


func request_grab(tool: SurgicalTool, hand: int) -> void:
	_req_grab.rpc_id(1, tool.uid, hand)


func request_release(hand: int, velocity: Vector3) -> void:
	_req_release.rpc_id(1, hand, velocity)


func request_pass(hand: int) -> void:
	_req_pass.rpc_id(1, hand)


func request_belt(hand: int, belt_slot: int) -> void:
	_req_belt.rpc_id(1, hand, belt_slot)


## One wheel notch on the syringe in this hand: notches > 0 pull the plunger out, < 0 push it in.
func request_plunger(hand: int, notches: int) -> void:
	_req_plunger.rpc_id(1, hand, notches)


func request_sterilize(hand: int) -> void:
	_req_sterilize.rpc_id(1, hand)


func request_wash(hand: int) -> void:
	_req_wash.rpc_id(1, hand)


@rpc("any_peer", "call_local", "reliable")
func _req_grab(uid: int, hand: int) -> void:
	var peer := Net._sender()
	var tool: SurgicalTool = tools.get(uid)
	var surgeon: Surgeon = Surgery.current.surgeons.get(peer)
	if tool == null or surgeon == null or tool.def.fixed or tool_in_hand(peer, hand):
		return
	var owned_belt := tool.state == SurgicalTool.State.BELT and tool.holder == peer
	if not (tool.state in [SurgicalTool.State.FREE, SurgicalTool.State.STANDING, SurgicalTool.State.INSIDE] or owned_belt):
		return
	var blocked := surgeon.blocked_reason(tool.def)
	if blocked:
		Surgery.current.tell(peer, blocked)
		return
	var was_standing := tool.state == SurgicalTool.State.STANDING
	_set_state.rpc(uid, SurgicalTool.State.HELD, peer, hand, tool.global_transform)
	if was_standing and not tool.grip_info.is_empty():
		Surgery.current.set_attached(peer, hand, true)
	if was_standing and tool.def.action == "tourniquet":
		Surgery.current.patient.remove_tourniquet()


@rpc("any_peer", "call_local", "reliable")
func _req_release(hand: int, velocity: Vector3) -> void:
	var peer := Net._sender()
	var tool := tool_in_hand(peer, hand)
	if tool == null:
		return
	Surgery.current.set_attached(peer, hand, false)
	if not tool.grip_info.is_empty():
		if tool.def.self_retaining:
			leave_standing(tool)
			return
		Surgery.current.patient.release_grip(tool.uid, tool.grip_info, false)
		tool.grip_info = {}
	_set_state.rpc(tool.uid, SurgicalTool.State.FREE, peer, -1, tool.global_transform)
	tool.linear_velocity = velocity
	tool.set_meta("falling", true)


## Hand-to-hand handoff. Moving hands fumble it and the tool falls.
@rpc("any_peer", "call_local", "reliable")
func _req_pass(hand: int) -> void:
	var giver: Surgeon = Surgery.current.surgeons.get(Net._sender())
	var tool := tool_in_hand(Net._sender(), hand)
	if giver == null or tool == null or not tool.grip_info.is_empty():
		return
	var target := giver.pass_target(hand)
	if target.is_empty():
		return
	var receiver: Surgeon = target[0]
	var receiving_hand: int = target[1]
	var blocked := receiver.blocked_reason(tool.def)
	if blocked:
		Surgery.current.tell(giver.peer_id, "%s can't take it: %s" % [receiver.display_name, blocked])
		return
	if giver.hands[hand].speed > Surgeon.FUMBLE_SPEED or receiver.hands[receiving_hand].speed > Surgeon.FUMBLE_SPEED:
		_set_state.rpc(tool.uid, SurgicalTool.State.FREE, giver.peer_id, -1, tool.global_transform)
		tool.set_meta("falling", true)
		Surgery.current.announce("Fumbled the handoff!")
		return
	_set_state.rpc(tool.uid, SurgicalTool.State.HELD, receiver.peer_id, receiving_hand, tool.global_transform)
	Surgery.current.tell(receiver.peer_id, "%s hands you the %s." % [giver.display_name, tool.def.name])


@rpc("any_peer", "call_local", "reliable")
func _req_belt(hand: int, belt_slot: int) -> void:
	var peer := Net._sender()
	var surgeon: Surgeon = Surgery.current.surgeons.get(peer)
	if surgeon == null or belt_slot >= surgeon.belt_capacity():
		return
	var held := tool_in_hand(peer, hand)
	var stored := tool_on_belt(peer, belt_slot)
	if held and stored == null and held.grip_info.is_empty():
		_set_state.rpc(held.uid, SurgicalTool.State.BELT, peer, belt_slot, held.global_transform)
	elif held == null and stored:
		_set_state.rpc(stored.uid, SurgicalTool.State.HELD, peer, hand, stored.global_transform)


@rpc("any_peer", "call_local", "reliable")
func _req_plunger(hand: int, notches: int) -> void:
	var tool := tool_in_hand(Net._sender(), hand)
	if tool and tool.def.action == "syringe" and Surgery.current.running:
		ToolActions.plunge(tool, notches * ToolActions.PLUNGER_STEP, Surgery.current.patient)


@rpc("any_peer", "call_local", "reliable")
func _req_sterilize(hand: int) -> void:
	var peer := Net._sender()
	var tool := tool_in_hand(peer, hand)
	if tool == null:
		Surgery.current.tell(peer, "Nothing in your hand to dip.")
	elif tool.soiled:
		Surgery.current.tell(peer, "Alcohol won't cut through that much dirt. Wash it first.")
	else:
		_set_sterile.rpc(tool.uid, true)
		tool.reported.erase("dirty")
		Surgery.current.tell(peer, "Dipped in alcohol.")


## The sink takes the dirt off, it doesn't make anything sterile.
@rpc("any_peer", "call_local", "reliable")
func _req_wash(hand: int) -> void:
	var peer := Net._sender()
	var tool := tool_in_hand(peer, hand)
	if tool == null:
		var surgeon: Surgeon = Surgery.current.surgeons.get(peer)
		if surgeon:
			surgeon.clean_gloves.rpc()
		Surgery.current.tell(peer, "You wash your gloves. They're still gloves.")
		return
	_set_soiled.rpc(tool.uid, false)
	tool.blood_exposure = 0.0
	_set_blood.rpc(tool.uid, 0.0)
	Surgery.current.tell(peer, "Scrubbed clean. Still not sterile.")


# --- Host ------------------------------------------------------------------------------------------


func spawn(id: String, at: Vector3) -> void:
	if multiplayer.is_server():
		_spawn.rpc(_next_uid, id, at)


func leave_standing(tool: SurgicalTool) -> void:
	Surgery.current.set_attached(tool.holder, tool.slot, false)
	_set_state.rpc(tool.uid, SurgicalTool.State.STANDING, tool.holder, -1, tool.global_transform)


## Host: the tool leaves the hand and wraps around a limb (a tourniquet), see PatientBody.limb_ring().
func wrap(tool: SurgicalTool, ring: Dictionary) -> void:
	leave_standing(tool)
	_wrap.rpc(tool.uid, ring.center, ring.axis, ring.radius)


@rpc("authority", "call_local", "reliable")
func _wrap(uid: int, center: Vector3, axis: Vector3, radius: float) -> void:
	var tool: SurgicalTool = tools.get(uid)
	if tool:
		tool.wrap_around(center, axis, radius)


func carry(item: SurgicalTool, by: SurgicalTool) -> void:
	_set_state.rpc(item.uid, SurgicalTool.State.CARRIED, by.uid, -1, item.global_transform)


func drop_carried(by: SurgicalTool) -> void:
	var item := carried_by(by)
	if item:
		_let_fall(item)


func _let_fall(tool: SurgicalTool) -> void:
	_set_state.rpc(tool.uid, SurgicalTool.State.FREE, 0, -1, tool.global_transform)
	tool.set_meta("falling", true)


## Host: exact amount here, everyone else sees it change in FILL_STEPS (and the moment it runs dry).
func set_fill(tool: SurgicalTool, amount: float) -> void:
	var step := ceili(tool.fill * FILL_STEPS)
	tool.fill = clampf(amount, 0.0, 1.0)
	if ceili(tool.fill * FILL_STEPS) != step:
		_show_fill.rpc(tool.uid, tool.fill)


## Host: moves up to `amount` ml of liquid out of a syringe, vial or dish, into another one or (to == null) out of it.
## Drugs go along in proportion, so a mix stays mixed. Returns what moved: drug id -> amount in its unit.
func transfer(from: SurgicalTool, to: SurgicalTool, amount: float) -> Dictionary:
	var moved: Dictionary = {}
	amount = minf(amount, from.ml)
	if amount <= 0.0:
		return moved
	var share := amount / from.ml
	for drug: String in from.contents:
		moved[drug] = from.contents[drug] * share
		from.contents[drug] -= moved[drug]
	add_liquid(from, -amount)
	if to:
		add_liquid(to, amount, moved)
	return moved


## Host: adds ml of liquid holding `drugs` (drug id -> amount, "blood" in ml) and ml of air to a syringe, vial or dish.
## Negative takes away (the contents are taken out by the caller). Everyone sees the exact result.
func add_liquid(tool: SurgicalTool, ml: float, drugs: Dictionary = {}, air: float = 0.0) -> void:
	for drug: String in drugs:
		tool.contents[drug] = tool.contents.get(drug, 0.0) + drugs[drug]
	tool.ml += ml
	tool.air = maxf(tool.air + air, 0.0)
	if tool.ml <= 0.0001:
		tool.ml = 0.0
		tool.contents.clear()
	tool.fill = tool.ml / tool.def.volume
	_show_liquid.rpc(tool.uid, tool.ml, tool.air, tool.contents.get("blood", 0.0) / tool.ml if tool.ml > 0.0 else 0.0)


func consume(tool: SurgicalTool) -> void:
	if tool.state == SurgicalTool.State.HELD:
		Surgery.current.set_attached(tool.holder, tool.slot, false)
	_set_state.rpc(tool.uid, SurgicalTool.State.CONSUMED, tool.holder, -1, tool.global_transform)


func drink(peer: int, hand: int) -> String:
	var tool := tool_in_hand(peer, hand)
	if tool == null or not tool.def.drinkable or tool.charges == 0:
		return ""
	tool.charges -= 1
	if tool.charges == 0:
		consume(tool)
	return tool.def.id


## Host: a surgeon left, so everything in their hands and on their belt drops. Standing clamps keep holding.
func drop_all(peer: int) -> void:
	for tool: SurgicalTool in tools.values():
		if tool.holder != peer or not tool.state in [SurgicalTool.State.HELD, SurgicalTool.State.BELT]:
			continue
		if not tool.grip_info.is_empty():
			Surgery.current.patient.release_grip(tool.uid, tool.grip_info, false)
			tool.grip_info = {}
		_set_state.rpc(tool.uid, SurgicalTool.State.FREE, 0, -1, tool.global_transform)
		tool.set_meta("falling", true)


func retained_count() -> int:
	return tools.values().filter(func(t: SurgicalTool) -> bool: return t.state == SurgicalTool.State.INSIDE).size()


func _physics_process(delta: float) -> void:
	var surgery := Surgery.current
	if surgery == null:
		return
	for tool: SurgicalTool in tools.values():
		if tool.state == SurgicalTool.State.CARRIED and tools.has(tool.holder):
			# Centered on the carrier's tip.
			var by: SurgicalTool = tools[tool.holder]
			tool.global_transform = Transform3D(by.global_basis, by.tip_position() + by.global_basis.z * tool.def.length * 0.5)
			continue
		var surgeon: Surgeon = surgery.surgeons.get(tool.holder)
		if surgeon == null:
			continue
		if tool.state == SurgicalTool.State.HELD:
			tool.global_transform = surgeon.hands[tool.slot].grip_transform()
		elif tool.state == SurgicalTool.State.BELT:
			tool.global_transform = surgeon.belt_transform(tool.slot)
	if not multiplayer.is_server() or not surgery.running:
		return
	for tool: SurgicalTool in tools.values():
		if tool.state != SurgicalTool.State.HELD:
			ToolActions.finish_injection(tool, surgery.patient)
		match tool.state:
			SurgicalTool.State.HELD:
				var surgeon: Surgeon = surgery.surgeons.get(tool.holder)
				# Paused, not released: a lag spike must not fire a charged defibrillator or drop a clamp's grip.
				if surgeon and not surgeon.is_stalled():
					ToolActions.update(tool, surgeon.hand_state(tool.slot), surgery.patient, delta)
			SurgicalTool.State.STANDING:
				ToolActions.update_standing(tool, surgery.patient, delta)
			SurgicalTool.State.FREE:
				_check_drop(tool)
			SurgicalTool.State.CARRIED:
				var by: SurgicalTool = tools.get(tool.holder)
				if by == null or by.state != SurgicalTool.State.HELD:
					_let_fall(tool)
	_sync_acc += delta
	if _sync_acc >= SYNC_INTERVAL:
		_sync_acc = 0.0
		var moving: Array = []
		for tool: SurgicalTool in tools.values():
			if tool.state == SurgicalTool.State.FREE and not tool.sleeping:
				moving.append([tool.uid, tool.global_transform])
		if not moving.is_empty():
			_sync_free.rpc(moving)


## Where a dropped tool ended up decides what it costs you.
func _check_drop(tool: SurgicalTool) -> void:
	if not tool.has_meta("falling"):
		return
	var patient := Surgery.current.patient
	var probe := patient.body.probe(tool.global_position)
	if probe.zone == "cavity":
		tool.remove_meta("falling")
		_set_state.rpc(tool.uid, SurgicalTool.State.INSIDE, 0, -1, tool.global_transform)
		Surgery.current.scoring.add("dropped_in_cavity")
		Surgery.current.sound("tool_drop_flesh", tool.global_position)
		if tool.def.action in ["cut", "saw"]:
			patient.cut_cavity(probe.uv, probe.depth, 1.0, not tool.sterile, 2.0)
		if not tool.sterile:
			patient.contaminate_site("")
		return
	for body in tool.get_colliding_bodies():
		if body.has_meta("floor") and tool.def.fragile:
			tool.remove_meta("falling")
			consume(tool)
			Surgery.current.scoring.add("broken_syringe")
			Surgery.current.sound("glass_break", tool.global_position)
			Surgery.current.announce("The %s shatters on the floor." % tool.def.name)
			return
		if body.has_meta("floor"):
			tool.remove_meta("falling")
			_set_sterile.rpc(tool.uid, false)
			_set_soiled.rpc(tool.uid, true)
			Surgery.current.scoring.add("dropped_tool")
			Surgery.current.sound("tool_drop_metal", tool.global_position)
			return
		if body.has_meta("part"):
			tool.remove_meta("falling")
			if tool.def.size == "heavy":
				patient.heavy_drop(tool.global_position)
			return
	if tool.sleeping:
		tool.remove_meta("falling")


# --- Everyone --------------------------------------------------------------------------------------


func _create(uid: int, id: String, xform: Transform3D) -> SurgicalTool:
	var def := Db.tool(id)
	if def == null:
		push_warning("Unknown tool id '%s'" % id)
		def = Db.tool("gauze")
	var tool := SurgicalTool.new()
	add_child(tool)
	tool.setup(uid, def)
	tool.global_transform = xform
	tools[uid] = tool
	_next_uid = maxi(_next_uid, uid + 1)
	if Surgery.current and Surgery.current.scenario and Surgery.current.scenario.dirty_start and not def.action in ["syringe", "vial"]:
		tool.sterile = false
	return tool


@rpc("authority", "call_local", "reliable")
func _spawn(uid: int, id: String, at: Vector3) -> void:
	_create(uid, id, Transform3D(Basis.IDENTITY, at))


@rpc("authority", "call_local", "reliable")
func _set_state(uid: int, state: int, holder: int, slot: int, xform: Transform3D) -> void:
	var tool: SurgicalTool = tools.get(uid)
	if tool:
		tool.global_transform = xform
		tool.set_state(state as SurgicalTool.State, holder, slot)
		if state == SurgicalTool.State.HELD:
			Sfx.play("tool_pickup", xform.origin)


@rpc("authority", "call_local", "reliable")
func _set_sterile(uid: int, value: bool) -> void:
	var tool: SurgicalTool = tools.get(uid)
	if tool:
		tool.sterile = value
		var local := Surgery.current.local_surgeon
		tool.show_contamination(local != null and local.mods.flag("contamination_vision"))


## Host: builds up blood on a tool working in blood; everyone sees it in steps of a quarter.
func add_blood(tool: SurgicalTool, amount: float) -> void:
	tool.blood_exposure = minf(tool.blood_exposure + amount, 1.0)
	var shown := snappedf(tool.blood_exposure, 0.25)
	if shown > tool.blood:
		_set_blood.rpc(tool.uid, shown)


@rpc("authority", "call_local", "reliable")
func _set_blood(uid: int, amount: float) -> void:
	var tool: SurgicalTool = tools.get(uid)
	if tool:
		tool.set_blood(amount)


@rpc("authority", "call_local", "reliable")
func _show_fill(uid: int, amount: float) -> void:
	var tool: SurgicalTool = tools.get(uid)
	if tool:
		if not multiplayer.is_server():
			tool.fill = amount
		tool.show_fill(amount)


@rpc("authority", "call_local", "reliable")
func _show_liquid(uid: int, ml: float, air: float, red: float) -> void:
	var tool: SurgicalTool = tools.get(uid)
	if tool:
		tool.ml = ml
		tool.air = air
		tool.red = red
		tool.fill = ml / tool.def.volume
		tool.show_liquid()


@rpc("authority", "call_local", "reliable")
func _set_soiled(uid: int, value: bool) -> void:
	var tool: SurgicalTool = tools.get(uid)
	if tool:
		tool.set_soiled(value)


@rpc("authority", "call_remote", "unreliable_ordered")
func _sync_free(moving: Array) -> void:
	for entry: Array in moving:
		var tool: SurgicalTool = tools.get(entry[0])
		if tool and tool.state == SurgicalTool.State.FREE:
			tool.global_transform = entry[1]
