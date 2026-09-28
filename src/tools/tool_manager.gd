class_name ToolManager
extends Node3D
## Owns every tool in the room. Peers send requests (grab, release, belt), the host decides and broadcasts.
## Initial tools spawn deterministically on every peer, later ones (nurse deliveries) come from the host.

const SYNC_INTERVAL := 0.1
const GRAB_RADIUS := 0.09

var tools: Dictionary = {}
var _next_uid := 1
var _sync_acc := 0.0


func spawn_initial(tray_ids: PackedStringArray, tray_spots: Array[Vector3], personal: Dictionary) -> void:
	var spot := 0
	for id in tray_ids:
		_create(_next_uid, id, Transform3D(Basis.IDENTITY, tray_spots[spot % tray_spots.size()]))
		spot += 1
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
		if tool.state in [SurgicalTool.State.FREE, SurgicalTool.State.STANDING, SurgicalTool.State.INSIDE]:
			var dist := minf(tool.global_position.distance_to(at), tool.tip_position().distance_to(at))
			if dist < best_dist:
				best_dist = dist
				best = tool
	return best


# --- Requests (any peer) ---------------------------------------------------------------------------


func request_grab(tool: SurgicalTool, hand: int) -> void:
	_req_grab.rpc_id(1, tool.uid, hand)


func request_release(hand: int, velocity: Vector3) -> void:
	_req_release.rpc_id(1, hand, velocity)


func request_belt(hand: int, belt_slot: int) -> void:
	_req_belt.rpc_id(1, hand, belt_slot)


func request_sterilize(hand: int) -> void:
	_req_sterilize.rpc_id(1, hand)


@rpc("any_peer", "call_local", "reliable")
func _req_grab(uid: int, hand: int) -> void:
	var peer := Net._sender()
	var tool: SurgicalTool = tools.get(uid)
	var surgeon: Surgeon = Surgery.current.surgeons.get(peer)
	if tool == null or surgeon == null or tool_in_hand(peer, hand):
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
func _req_sterilize(hand: int) -> void:
	var tool := tool_in_hand(Net._sender(), hand)
	if tool:
		_set_sterile.rpc(tool.uid, true)
		tool.reported.erase("dirty")


# --- Host ------------------------------------------------------------------------------------------


func spawn(id: String, at: Vector3) -> void:
	if multiplayer.is_server():
		_spawn.rpc(_next_uid, id, at)


func leave_standing(tool: SurgicalTool) -> void:
	Surgery.current.set_attached(tool.holder, tool.slot, false)
	_set_state.rpc(tool.uid, SurgicalTool.State.STANDING, tool.holder, -1, tool.global_transform)


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


func retained_count() -> int:
	return tools.values().filter(func(t: SurgicalTool) -> bool: return t.state == SurgicalTool.State.INSIDE).size()


func _physics_process(delta: float) -> void:
	var surgery := Surgery.current
	if surgery == null:
		return
	for tool: SurgicalTool in tools.values():
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
		match tool.state:
			SurgicalTool.State.HELD:
				var surgeon: Surgeon = surgery.surgeons.get(tool.holder)
				if surgeon:
					ToolActions.update(tool, surgeon.hand_state(tool.slot), surgery.patient, delta)
			SurgicalTool.State.STANDING:
				ToolActions.update_standing(tool, surgery.patient, delta)
			SurgicalTool.State.FREE:
				_check_drop(tool)
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
		Sfx.play("tool_drop_flesh", tool.global_position)
		if tool.def.action in ["cut", "saw"]:
			patient.cut_cavity(probe.uv, probe.depth, 1.0, not tool.sterile, 2.0)
		if not tool.sterile:
			patient.contaminate_site("")
		return
	for body in tool.get_colliding_bodies():
		if body.has_meta("floor"):
			tool.remove_meta("falling")
			_set_sterile.rpc(tool.uid, false)
			Surgery.current.scoring.add("dropped_tool")
			Sfx.play("tool_drop_metal", tool.global_position)
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
	if Surgery.current and Surgery.current.scenario and Surgery.current.scenario.dirty_start and not def.id.begins_with("syringe"):
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


@rpc("authority", "call_remote", "unreliable_ordered")
func _sync_free(moving: Array) -> void:
	for entry: Array in moving:
		var tool: SurgicalTool = tools.get(entry[0])
		if tool and tool.state == SurgicalTool.State.FREE:
			tool.global_transform = entry[1]
