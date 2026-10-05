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
