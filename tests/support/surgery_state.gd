extends RefCounted
## Direct changes to a running surgery, for setting up what a test isn't about: each one writes the state an action
## would leave, without anyone doing the action, and is named for that state (skin_is_cut(), patient_is_asleep()).
## What a player does goes through the player_* steps of surgery_driver.gd instead, so the game itself does the work.
## Keep the two apart: a test about cutting cuts with player_incises(), a test about stitching starts from skin_is_cut().


## As if a right dose of propofol had gone in: asleep within seconds, for the rest of the surgery.
static func patient_is_asleep(patient: Patient) -> void:
	patient.administer("propofol", "direct", Db.drug("propofol").dose * patient.weight_kg)


## As if lidocaine had gone into the site: numb within seconds.
static func patient_is_numb(patient: Patient) -> void:
	patient.administer("lidocaine", "direct", Db.drug("lidocaine").dose * patient.weight_kg)


## A surgeon's cut from `from` to `to` (site uv), `depth` deep (0..1, see Wound.MUSCLE_DEPTH), as one clean stroke.
static func skin_is_cut(patient: Patient, from: Vector2, to: Vector2, depth: float) -> Wound:
	var key := 800000 + patient.wounds.size()
	patient.cut(key, from, to, depth, 1.0, false, 0.0)
	return patient._stroke_wounds[key]


## As if the whole length of `wound` had been sewn shut.
static func wound_is_closed(wound: Wound) -> void:
	wound.bins.fill(1.0)


## As if `wound` had been cauterized as far as a cautery seals.
static func wound_is_cauterized(wound: Wound) -> void:
	wound.cauterized = 0.95


## Tied running threads on stationary skin, for renderer/cache load checks without moving the player's needle.
static func skin_has_finished_threads(patient: Patient, count: int, first_id: int) -> void:
	var tissue := patient.body.tissue
	for id in range(first_id, first_id + count):
		var uv := Vector2(0.3 + (id - first_id) % 4 * 0.1, 0.3 + floorf(float(id - first_id) / 4.0) * 0.1)
		for hole: Vector2 in [uv, uv + Vector2(0.035, 0.035)]:
			tissue.thread_anchor(id, hole, TissueSim.Depth.SKIN, TissueSim.THREAD_LOOSE[TissueSim.Depth.SKIN], 4.0, 1.0)
		tissue.finish_thread(id)


## A tool `id` lying in a free place on the instrument tray (stocked there, as the nurse would have). Returns it.
static func tool_is_on_tray(surgery: Surgery, id: String) -> SurgicalTool:
	surgery.tools.spawn(id, free_tray_spot(surgery))
	return surgery.tools.tools.values()[-1]


## Nothing happens on its own: the scenario's random events (a bleed, the patient waking) are off. Scripted ones stay.
static func random_events_are_off(surgery: Surgery) -> void:
	surgery.director._pool = PackedStringArray()


## A free place on the instrument tray, nothing else lying within 8 cm: the clear strip down its middle first. A spot
## something slid away from (tray_spot_is_bad()) isn't offered again.
static func free_tray_spot(surgery: Surgery) -> Vector3:
	var rest := surgery.room.tray_zone("")
	var strip := rest.end.x + 0.07
	var spots: Array[Vector3] = []
	for x: float in [strip, strip + 0.1, rest.end.x - 0.05, rest.position.x + 0.05]:
		for i in 9:
			var along := 0.0 if i == 8 else (i / 2 + 1) * 0.06 * (1 if i % 2 == 0 else -1)
			spots.append(Vector3(x, rest.position.y + 0.05, rest.get_center().z + along))
	for at in spots:
		if at in surgery.get_meta("bad_tray_spots", []):
			continue
		var crowded := surgery.tools.tools.values().any(func(t: SurgicalTool) -> bool: return t.state == SurgicalTool.State.FREE and (ToolManager.middle(t) - at).slide(Vector3.UP).length() < 0.08)
		if not crowded:
			return at
	return spots[0]


## A tool set down at `spot` (from free_tray_spot()) landed against something there and slid away.
static func tray_spot_is_bad(surgery: Surgery, spot: Vector3) -> void:
	var bad: Array = surgery.get_meta("bad_tray_spots", [])
	bad.append(spot)
	surgery.set_meta("bad_tray_spots", bad)


## Every starter tool is present; this network fixture checks replication rather than missing-tool modifiers.
static func scenario_has_all_starter_tools(scenario_id: String) -> void:
	Db.scenario(scenario_id).missing_tool_chance = 0.0


## Both network surgeons have ordinary reach/steadiness, no run modifiers, and repeatable session rolls.
static func network_session_has_ordinary_surgeons(seed_value: int = 1) -> void:
	for player: Dictionary in Net.roster.values():
		player.quirks = [{"id": "normal_dude", "variant": ""}]
	Net.run_modifiers = []
	Net._rng.seed = seed_value


## An isolated room shell for geometry/visual checks, with the same environment builders as Room.build().
static func room_has_shell(room: Room, environment: String) -> void:
	room.environment_id = environment
	room.layout = Room.LAYOUTS[environment]
	room._build_environment()
	room._build_shell()


## A stationary visual fixture. Real input/physics movement is covered separately by test_surgeon_movement.gd.
static func surgeon_is_pose_fixture(room: Room, peer: int, visible: bool = true) -> Surgeon:
	var surgeon := Surgeon.new()
	room.add_child(surgeon)
	surgeon.setup(peer, "", [], Transform3D.IDENTITY)
	surgeon.set_physics_process(false)
	surgeon._body.visible = visible
	surgeon._face.visible = visible
	for hand in surgeon.hands:
		hand.visible = visible
	return surgeon


## A received pose packet plus one animation tick on each peer, for exact model geometry/replication checks.
static func surgeon_pose_is_received(owner: Surgeon, puppet: Surgeon, crouch: float, pitch: float, speed: float = 0.0) -> void:
	owner.crouch = crouch
	owner.pitch = pitch
	owner._last_position = owner.global_position - Vector3(speed / 60.0, 0, 0)
	puppet._last_position = puppet.global_position - Vector3(speed / 60.0, 0, 0)
	owner._animate_body(1.0 / 60.0)
	puppet._sync_state(owner._pack_state())
	puppet._animate_body(1.0 / 60.0)
	surgeon_hands_are_at_height(puppet, 1.05 - crouch * Surgeon.CROUCH_DROP)


## Empty hands at a known floor-reaching height, to test sleeve clearance over the knees.
static func surgeon_hands_are_at_height(surgeon: Surgeon, height: float) -> void:
	for hand in surgeon.hands:
		hand.target = surgeon.to_global(Vector3(-0.17 if hand.index == 0 else 0.17, height, -0.38))
		surgeon.support_elbow(hand)
		hand.snap_pose(surgeon.visual_shoulder(hand.index))


## A surgeon lies on the chosen side long enough for the normal physics animation to settle.
static func surgeon_is_knocked_out(surgeon: Surgeon, side: float) -> void:
	surgeon.status.knocked_out = 60.0
	surgeon._fall_side = side


## No stress-induced hand jitter during a camera/reach measurement.
static func surgeon_is_steady(surgeon: Surgeon) -> void:
	surgeon.status.stress = 0.0


## A hand attached just inside its reach boundary; the visual walking cycle must not pull it around.
static func surgeon_hand_is_attached(surgeon: Surgeon, hand: int, target: Vector3) -> void:
	surgeon.hands[hand].attached = true
	surgeon.hands[hand].target = target


## A visible, non-colliding remote copy of an owner, for checking how other players see normal physics poses.
static func surgeon_has_remote_copy(owner: Surgeon) -> Surgeon:
	var puppet := Surgeon.new()
	owner.get_parent().add_child(puppet)
	puppet.setup(2, "", [], owner.global_transform)
	puppet.collision_layer = 0
	puppet.collision_mask = 0
	return puppet


## A normal owner packet delivered to the remote fixture before its next engine physics tick.
static func surgeon_copy_has_owner_state(owner: Surgeon, puppet: Surgeon) -> void:
	puppet.global_transform = owner.global_transform
	puppet._sync_state(owner._pack_state())

