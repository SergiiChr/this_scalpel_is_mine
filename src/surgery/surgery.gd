class_name Surgery
extends Node3D
## Root of an operation. Builds the room, patient, surgeons and tools from the session,
## runs the host-side systems and is the one place where gameplay RPCs go through.
##
## Launch surgery.tscn directly (F6 in the editor) for a solo test run.
## Command line: `-- --scenario=appendectomy` picks the scenario.

static var current: Surgery

const STATUS_INTERVAL := 0.5
const QTE_KEYS: PackedStringArray = ["move_forward", "move_back", "move_left", "move_right"]
const QTE_TIMEOUT := 10.0

@onready var room: Room = $Room
@onready var patient: Patient = $Patient
@onready var tools: ToolManager = $Tools
@onready var surgeons_root: Node3D = $Surgeons
@onready var objectives: Objectives = $Systems/Objectives
@onready var director: EventDirector = $Systems/Director
@onready var scoring: Scoring = $Systems/Scoring
@onready var nurse: Nurse = $Systems/Nurse
@onready var lab: Lab = $Systems/Lab
@onready var hud: Hud = $Hud

var scenario: ScenarioDef
var surgeons: Dictionary = {}
var local_surgeon: Surgeon
var running := false
var finished := false
var elapsed := 0.0
var rng := RandomNumberGenerator.new()
## Last status from the host, for the HUD: objectives, score, cooldowns.
var status: Dictionary = {}

var _loaded: Dictionary = {}
var _announced: Dictionary = {}
var _status_acc := 0.0
var _qte: Dictionary = {}


func _enter_tree() -> void:
	current = self


func _exit_tree() -> void:
	if current == self:
		current = null


func _ready() -> void:
	if Net.roster.is_empty():
		_start_test_session()
	scenario = Net.scenario()
	rng.seed = Net.session_seed
	room.build(scenario.environment, self)
	patient.position = Vector3(0, Room.TABLE_HEIGHT, 0)
	patient.setup(scenario, Net.patient_quirks, Net.session_seed)
	patient.died.connect(_on_patient_died)
	var peers := Net.roster.keys()
	peers.sort()
	var personal: Dictionary = {}
	for i in peers.size():
		var surgeon := _spawn_surgeon(peers[i], i)
		personal[peers[i]] = surgeon.mods.list("items")
	tools.spawn_initial(scenario.roll_tools(rng), room.tray_spots(), personal)
	objectives.setup(scenario)
	director.setup(scenario, Net.session_seed)
	hud.setup(self)
	_peer_ready.rpc_id(1)


func _start_test_session() -> void:
	var wanted := ""
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--scenario="):
			wanted = arg.get_slice("=", 1)
	var def := Db.scenario(wanted) if wanted else Db.scenarios[0]
	var test_rng := RandomNumberGenerator.new()
	test_rng.randomize()
	Net.scenario_id = def.id
	Net.session_seed = test_rng.randi()
	Net.roster = {1: {"name": Progress.player_name, "quirks": QuirkRoller.roll_surgeon(test_rng), "ready": true}}
	Net.patient_quirks = QuirkRoller.roll_patient(def, test_rng)


func _spawn_surgeon(peer: int, index: int) -> Surgeon:
	var surgeon := Surgeon.new()
	surgeons_root.add_child(surgeon)
	var info: Dictionary = Net.roster[peer]
	surgeon.setup(peer, info.name, info.quirks, room.spawn_transform(index))
	surgeons[peer] = surgeon
	if surgeon.is_local:
		local_surgeon = surgeon
	return surgeon


func _physics_process(delta: float) -> void:
	if not running or finished:
		return
	elapsed += delta
	if not multiplayer.is_server():
		return
	objectives.tick(delta, self)
	director.tick(delta, self)
	nurse.tick(delta, self)
	lab.tick(delta, self)
	_tick_qte(delta)
	if scenario.time_limit > 0 and elapsed >= scenario.time_limit:
		_finish(false, "Out of time.")
	elif objectives.all_done() and (patient.alive or patient.flags.has("euthanized")):
		_finish(true, "")
	_status_acc += delta
	if _status_acc >= STATUS_INTERVAL:
		_status_acc = 0.0
		_sync_status.rpc({"objectives": objectives.snapshot(), "score": scoring.points, "nurse": nurse.cooldown_left, "lab": lab.cooldown_left, "elapsed": elapsed})


func time_left() -> float:
	return maxf(scenario.time_limit - elapsed, 0.0) if scenario.time_limit > 0 else -1.0


func _on_patient_died(reason: String) -> void:
	if patient.flags.has("euthanized"):
		announce(reason)
		return
	_finish(false, reason)


func _finish(success: bool, reason: String) -> void:
	if finished:
		return
	finished = true
	_show_report.rpc(Report.build(self, success, reason))


# --- Host -> peers ---------------------------------------------------------------------------------


## Host: toast for everyone. throttled drops repeats of the same text for a few seconds.
func announce(text: String, throttled: bool = false) -> void:
	if not multiplayer.is_server() or text.is_empty():
		return
	var now := Time.get_ticks_msec() * 0.001
	if throttled and now - float(_announced.get(text, -INF)) < 5.0:
		return
	_announced[text] = now
	_toast.rpc(text)


## Host: toast for one peer.
func tell(peer: int, text: String) -> void:
	_toast.rpc_id(peer, text)


func say(text: String, voice_id: String) -> void:
	_say.rpc(text, voice_id)


func jolt_all(strength: float, text: String) -> void:
	_jolt.rpc(strength)
	announce(text, true)


func jolt_peer(peer: int, strength: float) -> void:
	_jolt.rpc_id(peer, strength)


func broadcast_stress(amount: float) -> void:
	_stress.rpc(amount)


func add_sickness(peer: int, amount: float) -> void:
	_sick.rpc_id(peer, amount)


func set_attached(peer: int, hand: int, value: bool) -> void:
	var surgeon: Surgeon = surgeons.get(peer)
	if surgeon and hand >= 0:
		surgeon.set_hand_attached.rpc_id(peer, hand, value)


func flicker_lights() -> void:
	_flicker.rpc(randf_range(1.0, 3.0))


func publish_lab(text: String) -> void:
	_lab_result.rpc(text)


## Defibrillator discharge: anyone else touching the patient gets zapped.
func shock_bystanders(source_peer: int) -> void:
	for peer: int in surgeons:
		if peer == source_peer:
			continue
		for hand in surgeons[peer].hands:
			if hand.engaged and patient.body.probe(hand.global_position).zone != "none":
				_zapped.rpc_id(peer)
				break


## A surgeon walked away while holding onto tissue.
func overstretched(peer: int, hand: int) -> void:
	var tool := tools.tool_in_hand(peer, hand)
	if tool == null or tool.grip_info.is_empty():
		return
	var anchor: Vector2 = tool.grip_info.get("anchor", patient.body.world_to_uv(tool.tip_position()))
	patient.tear(anchor, Vector2(randf_range(-1, 1), randf_range(-1, 1)), 0.05)
	patient.release_grip(tool.uid, tool.grip_info, false)
	tool.grip_info = {}
	set_attached(peer, hand, false)
	tell(peer, "You pulled away while holding on. The tissue tore.")


@rpc("authority", "call_local", "reliable")
func _toast(text: String) -> void:
	hud.toast(text)


@rpc("authority", "call_local", "reliable")
func _say(text: String, voice_id: String) -> void:
	hud.subtitle(text)
	Sfx.play_voice(voice_id, patient.global_position + Vector3(0.7, 0.2, 0))


@rpc("authority", "call_local", "reliable")
func _jolt(strength: float) -> void:
	if local_surgeon:
		local_surgeon.jolt(strength)


@rpc("authority", "call_local", "reliable")
func _stress(amount: float) -> void:
	if local_surgeon:
		local_surgeon.status.add_stress(amount)


@rpc("authority", "call_local", "reliable")
func _sick(amount: float) -> void:
	if local_surgeon:
		local_surgeon.status.add_sickness(amount)


@rpc("authority", "call_local", "reliable")
func _zapped() -> void:
	hud.toast("ZAP! You were touching the patient during the shock.")
	local_surgeon.status.add_stress(0.4)
	local_surgeon.jolt(1.0)
	local_surgeon.drop_everything()


@rpc("authority", "call_local", "reliable")
func _flicker(duration: float) -> void:
	room.flicker(duration)


@rpc("authority", "call_local", "reliable")
func _lab_result(text: String) -> void:
	room.monitor.show_lab(text)
	hud.toast("Lab results are on the monitor.")


@rpc("authority", "call_local", "unreliable_ordered")
func _sync_status(data: Dictionary) -> void:
	status = data
	elapsed = data.elapsed


@rpc("authority", "call_local", "reliable")
func _start() -> void:
	running = true
	hud.begin()


@rpc("authority", "call_local", "reliable")
func _show_report(report: Dictionary) -> void:
	running = false
	finished = true
	for roll: Dictionary in report.patient_quirks:
		Progress.unlock(Db.quirk(QuirkDef.Kind.PATIENT, roll.id), roll.variant)
	if multiplayer.is_server():
		Progress.record_result(report.scenario, report.stars)
	hud.show_report(report)


# --- Peer -> host requests -------------------------------------------------------------------------


@rpc("any_peer", "call_local", "reliable")
func _peer_ready() -> void:
	_loaded[Net._sender()] = true
	if Net.roster.keys().all(func(peer: int) -> bool: return _loaded.has(peer)):
		_start.rpc()


func request_drink(hand: int) -> void:
	_req_drink.rpc_id(1, hand)


@rpc("any_peer", "call_local", "reliable")
func _req_drink(hand: int) -> void:
	var peer := Net._sender()
	var id := tools.drink(peer, hand)
	if id:
		_drank.rpc_id(peer, id)


@rpc("authority", "call_local", "reliable")
func _drank(tool_id: String) -> void:
	local_surgeon.status.drink(tool_id)
	Sfx.play("sip", local_surgeon.global_position)
	hud.toast({"whiskey_flask": "Warm. Steady.", "coffee_thermos": "Bitter. Awake.", "surgical_cap": "Cap on. No more drips."}.get(tool_id, ""))


func report_incident(kind: String) -> void:
	_incident.rpc_id(1, kind)


@rpc("any_peer", "call_local", "reliable")
func _incident(kind: String) -> void:
	var surgeon: Surgeon = surgeons.get(Net._sender())
	if surgeon == null:
		return
	match kind:
		"vomit":
			scoring.add("vomit")
			var at := surgeon.hands[surgeon.active].global_position
			if patient.body.probe(at + Vector3.DOWN * 0.1).zone in ["site", "cavity"]:
				var uv := patient.body.world_to_uv(at)
				patient.paint(WoundMap.Layer.FLUIDS, WoundMap.GRIME, uv, uv, 0.15, 0.9, WoundMap.Mode.MAX)
				patient.contaminate_site("...right into the surgical field.")
			for other: int in surgeons:
				if other != surgeon.peer_id:
					add_sickness(other, 0.3)
		"passed_out":
			scoring.add("passed_out")
		"sweat_drip":
			patient.contaminate_site("")


@rpc("any_peer", "call_local", "reliable")
func _req_iv(hand: int) -> void:
	var peer := Net._sender()
	var tool := tools.tool_in_hand(peer, hand)
	if tool == null or tool.def.action != "inject" or tool.charges == 0:
		tell(peer, "You need a syringe, bag or drug in hand.")
		return
	patient.administer(tool.def.drug, "iv")
	if tool.charges > 0:
		tool.charges -= 1
		if tool.charges == 0:
			tools.consume(tool)


@rpc("any_peer", "call_local", "reliable")
func _req_comfort() -> void:
	patient.reassure()


@rpc("any_peer", "call_local", "reliable")
func _req_order(tool_id: String) -> void:
	nurse.request(Net._sender(), tool_id, self)


@rpc("any_peer", "call_local", "reliable")
func _req_lab(kind: String) -> void:
	lab.request(kind, self)


# --- Stations (called on the interacting peer) -----------------------------------------------------


func open_manual(_surgeon: Surgeon) -> void:
	hud.open_manual()


func open_card(_surgeon: Surgeon) -> void:
	hud.open_card()


func open_nurse(_surgeon: Surgeon) -> void:
	hud.open_nurse()


func open_lab(_surgeon: Surgeon) -> void:
	hud.open_lab()


func order_tool(tool_id: String) -> void:
	_req_order.rpc_id(1, tool_id)


func order_lab(kind: String) -> void:
	_req_lab.rpc_id(1, kind)


func change_gloves(surgeon: Surgeon) -> void:
	surgeon.status.sweat = 0.0
	hud.toast("Fresh gloves.")


func sanitize_tool(surgeon: Surgeon) -> void:
	tools.request_sterilize(surgeon.active)
	hud.toast("Dipped in alcohol.")


func use_iv(surgeon: Surgeon) -> void:
	_req_iv.rpc_id(1, surgeon.active)


func comfort_patient(_surgeon: Surgeon) -> void:
	_req_comfort.rpc_id(1)


func turn_patient(_surgeon: Surgeon) -> void:
	_req_turn.rpc_id(1)


# --- Turning the patient (quick time event) ---------------------------------------------------------
# Everyone near the table takes part. All hits: the patient turns one step.
# A few misses: they slide back, try again. Too many misses or a wrong key: they hit the floor.


@rpc("any_peer", "call_local", "reliable")
func _req_turn() -> void:
	var peer := Net._sender()
	if not _qte.is_empty():
		return
	var center := patient.global_position
	var near := surgeons.keys().filter(func(p: int) -> bool: return (surgeons[p] as Surgeon).global_position.distance_to(center) < 1.5)
	var sides := near.map(func(p: int) -> float: return signf((surgeons[p] as Surgeon).global_position.z))
	if sides.any(func(s: float) -> bool: return s != sides[0]):
		tell(peer, "Get on the same side of the table to turn them.")
		return
	if near.any(func(p: int) -> bool: return (surgeons[p] as Surgeon).hands.any(func(h: SurgeonHand) -> bool: return h.attached)):
		tell(peer, "Let go of everything first.")
		return
	var sequence := PackedStringArray()
	for i in 4:
		sequence.append(QTE_KEYS[rng.randi_range(0, QTE_KEYS.size() - 1)])
	_qte = {"peers": near, "results": {}, "timer": QTE_TIMEOUT, "target": _next_orientation()}
	for p: int in near:
		_qte_begin.rpc_id(p, sequence, 1.1)


func _next_orientation() -> int:
	var now := patient.body.orientation
	if now != PatientBody.Orientation.SIDE:
		return PatientBody.Orientation.SIDE
	for step: Dictionary in scenario.steps:
		if step.type == "flip":
			return step.get("orientation", PatientBody.Orientation.FACE_DOWN)
	return PatientBody.Orientation.FACE_UP


@rpc("authority", "call_local", "reliable")
func _qte_begin(sequence: PackedStringArray, window: float) -> void:
	hud.run_qte(sequence, window * local_surgeon.mods.mult("qte_window_mult"), func(hits: int, critical: bool) -> void: _qte_result.rpc_id(1, hits, critical))


@rpc("any_peer", "call_local", "reliable")
func _qte_result(hits: int, critical: bool) -> void:
	if _qte.is_empty():
		return
	_qte.results[Net._sender()] = [hits, critical]
	if _qte.results.size() >= _qte.peers.size():
		_resolve_qte()


func _tick_qte(delta: float) -> void:
	if not _qte.is_empty():
		_qte.timer -= delta
		if _qte.timer <= 0.0:
			_resolve_qte()


func _resolve_qte() -> void:
	var needed: int = 4 * _qte.peers.size()
	var hits := 0
	var critical := false
	for result: Array in _qte.results.values():
		hits += result[0]
		critical = critical or result[1]
	var misses := needed - hits
	if critical or misses > _qte.peers.size():
		scoring.add("patient_fell")
		announce("The patient slides off the table and hits the floor!")
		patient.turn_over(patient.body.orientation, true)
		Sfx.play("body_fall", patient.global_position)
	elif misses > 0:
		announce("They slide back. Try again, together this time.")
	else:
		scoring.add("flip_success")
		patient.turn_over(_qte.target, false)
		announce("Patient turned.")
	_qte = {}
