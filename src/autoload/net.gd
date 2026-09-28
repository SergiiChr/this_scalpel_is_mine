extends Node
## Host-client session. The host owns the simulation and the save; clients own only their surgeon.
## Solo play uses the same code path with an offline peer, so there is no separate single player mode.
##
## Flow: host()/join() -> lobby (quirks rolled by host, everyone readies up) -> start_session() -> surgery scene.

signal roster_changed
signal scenario_changed
signal connection_failed(reason: String)
signal session_started
signal disconnected

const DEFAULT_PORT := 24565
const MAX_CLIENTS := 1
const LOBBY_SCENE := "res://scenes/ui/lobby.tscn"
const SURGERY_SCENE := "res://scenes/surgery.tscn"
const MENU_SCENE := "res://scenes/ui/main_menu.tscn"

## peer id -> {"name": String, "quirks": Array[Dictionary], "ready": bool}
var roster: Dictionary = {}
var scenario_id := ""
var session_seed := 0
var patient_quirks: Array = []
## Run modifier ids (data/run_modifiers.cfg), rolled in the lobby so players see them before starting.
var run_modifiers: Array = []
## True from the start of a surgery until everyone is back in the lobby. Nobody can join in the middle.
var in_session := false
var _rng := RandomNumberGenerator.new()


func _ready() -> void:
	multiplayer.peer_connected.connect(_on_peer_connected)
	multiplayer.peer_disconnected.connect(_on_peer_disconnected)
	multiplayer.connected_to_server.connect(_on_connected)
	multiplayer.connection_failed.connect(func() -> void: connection_failed.emit("Could not reach the host."))
	multiplayer.server_disconnected.connect(_on_server_gone)


func is_host() -> bool:
	return multiplayer.is_server()


func local_id() -> int:
	return multiplayer.get_unique_id()


func is_online() -> bool:
	return not multiplayer.multiplayer_peer is OfflineMultiplayerPeer


func play_solo(scenario: String) -> void:
	leave()
	_start_hosting(scenario)


func host(scenario: String, port: int = DEFAULT_PORT) -> Error:
	leave()
	var peer := ENetMultiplayerPeer.new()
	var err := peer.create_server(port, MAX_CLIENTS)
	if err != OK:
		return err
	multiplayer.multiplayer_peer = peer
	_start_hosting(scenario)
	return OK


func join(ip: String, port: int = DEFAULT_PORT) -> Error:
	leave()
	var peer := ENetMultiplayerPeer.new()
	var err := peer.create_client(ip, port)
	if err == OK:
		multiplayer.multiplayer_peer = peer
	return err


func leave() -> void:
	if multiplayer.multiplayer_peer and not multiplayer.multiplayer_peer is OfflineMultiplayerPeer:
		multiplayer.multiplayer_peer.close()
	multiplayer.multiplayer_peer = OfflineMultiplayerPeer.new()
	roster.clear()
	in_session = false


func back_to_menu() -> void:
	leave()
	get_tree().change_scene_to_file(MENU_SCENE)


func scenario() -> ScenarioDef:
	return Db.scenario(scenario_id)


func local_quirks() -> Array:
	return roster.get(local_id(), {}).get("quirks", [])


func all_ready() -> bool:
	return not roster.is_empty() and roster.values().all(func(p: Dictionary) -> bool: return p.ready)


# --- Host side -------------------------------------------------------------------------------------


func _start_hosting(scenario: String) -> void:
	_rng.randomize()
	scenario_id = scenario
	run_modifiers = _roll_run_modifiers()
	roster = {1: _new_player(Progress.player_name)}
	get_tree().change_scene_to_file(LOBBY_SCENE)


func _new_player(player_name: String) -> Dictionary:
	return {"name": player_name, "quirks": QuirkRoller.roll_surgeon(_rng), "ready": false}


## Host only. Picks a new scenario and rerolls everyone (it's a new run).
func change_scenario(id: String) -> void:
	if not is_host():
		return
	scenario_id = id
	run_modifiers = _roll_run_modifiers()
	for peer_id: int in roster:
		roster[peer_id].quirks = QuirkRoller.roll_surgeon(_rng)
		roster[peer_id].ready = false
	_sync_lobby.rpc(roster, scenario_id, run_modifiers)


## One or two per run, never the same twice.
func _roll_run_modifiers() -> Array:
	var ids := Array(Db.run_modifiers.get_sections())
	ids.shuffle()
	return ids.slice(0, _rng.randi_range(1, 2))


## Host only, after everyone readied up.
func start_session() -> void:
	if not is_host() or not all_ready():
		return
	session_seed = _rng.randi()
	patient_quirks = QuirkRoller.roll_patient(scenario(), _rng)
	_begin.rpc(session_seed, patient_quirks, roster, scenario_id, run_modifiers)


## Host only. Back to the lobby after a report, with fresh quirks.
func return_to_lobby() -> void:
	if is_host():
		change_scenario(scenario_id)
		_go_to_lobby.rpc()


func set_ready(value: bool) -> void:
	_request_ready.rpc_id(1, value)


@rpc("any_peer", "call_local", "reliable")
func _request_ready(value: bool) -> void:
	var sender := _sender()
	if is_host() and roster.has(sender):
		roster[sender].ready = value
		_sync_lobby.rpc(roster, scenario_id, run_modifiers)


@rpc("any_peer", "call_remote", "reliable")
func _register(player_name: String) -> void:
	if not is_host():
		return
	if in_session:
		multiplayer.multiplayer_peer.disconnect_peer(_sender())
		return
	roster[_sender()] = _new_player(player_name)
	_sync_lobby.rpc(roster, scenario_id, run_modifiers)


# --- Everyone --------------------------------------------------------------------------------------


@rpc("authority", "call_local", "reliable")
func _sync_lobby(new_roster: Dictionary, new_scenario: String, modifiers: Array) -> void:
	roster = new_roster
	run_modifiers = modifiers
	if new_scenario != scenario_id:
		scenario_id = new_scenario
		scenario_changed.emit()
	roster_changed.emit()


@rpc("authority", "call_local", "reliable")
func _begin(seed_value: int, patient: Array, final_roster: Dictionary, scenario: String, modifiers: Array) -> void:
	session_seed = seed_value
	run_modifiers = modifiers
	patient_quirks = patient
	roster = final_roster
	scenario_id = scenario
	in_session = true
	for roll: Dictionary in local_quirks():
		Progress.unlock(Db.quirk(QuirkDef.Kind.SURGEON, roll.id), roll.variant)
	session_started.emit()
	get_tree().change_scene_to_file(SURGERY_SCENE)


@rpc("authority", "call_local", "reliable")
func _go_to_lobby() -> void:
	in_session = false
	get_tree().change_scene_to_file(LOBBY_SCENE)


func _on_connected() -> void:
	_register.rpc_id(1, Progress.player_name)
	get_tree().change_scene_to_file(LOBBY_SCENE)


func _on_peer_connected(_id: int) -> void:
	pass


func _on_peer_disconnected(id: int) -> void:
	roster.erase(id)
	if is_host():
		_sync_lobby.rpc(roster, scenario_id, run_modifiers)


func _on_server_gone() -> void:
	disconnected.emit()
	back_to_menu()


## Remote sender id, or our own id for local calls.
func _sender() -> int:
	var id := multiplayer.get_remote_sender_id()
	return id if id != 0 else multiplayer.get_unique_id()
