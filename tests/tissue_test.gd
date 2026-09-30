extends Node
## Soft tissue sim checks. Prints "FAIL: ..." for each broken expectation, run_tests.sh fails on those.
## Run: godot --headless --path . res://tests/tissue_test.tscn

const SIZE := Vector2(0.3, 0.25)
const MID := Vector2(0.5, 0.5)


func _ready() -> void:
	_cut_gapes()
	_depth_layers()
	_retraction_and_tears()
	_thin_skin_holds_at_rest()
	_stitches_close()
	_sleeps()
	_elastic()
	_muscle_first()
	_loose_stitch_gapes()
	_exact_snaps()
	_deformed_surface()
	_rests_on_curved_body()
	_stays_on_body()
	print("tissue_test: done")
	get_tree().quit()


func _sim() -> TissueSim:
	var sim := TissueSim.new()
	sim.tearing = true
	sim.build(SIZE, func(_uv: Vector2) -> float: return 0.0)
	return sim


func _settle(sim: TissueSim, steps: int = 60) -> void:
	for i in steps:
		sim._substep()


func _check(ok: bool, what: String) -> void:
	if not ok:
		print("FAIL: ", what)


func _cut_gapes() -> void:
	var sim := _sim()
	_settle(sim)
	_check(not sim.any_severed() and sim.gap_at(MID) == 0.0, "intact skin has no gap")
	sim.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.MUSCLE)
	_settle(sim)
	_check(sim.gap_at(MID) > TissueSim.OPEN_GAP, "a full depth cut gapes on its own (gap %.4f)" % sim.gap_at(MID))
	_check(sim.is_open(MID), "a full depth cut opens into the cavity")
	_check(sim.snapped.is_empty(), "skin tension alone doesn't tear anything")


func _depth_layers() -> void:
	var sim := _sim()
	sim.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.FAT)
	_settle(sim)
	var full := (TissueSim.RES * TissueSim.RES * 2) * 3
	_check(sim.triangles(TissueSim.Depth.SKIN).size() < full, "skin layer has a hole over a fat deep cut")
	_check(sim.triangles(TissueSim.Depth.FAT).size() < full, "fat layer has a hole over a fat deep cut")
	_check(sim.triangles(TissueSim.Depth.MUSCLE).size() == full, "muscle layer stays whole under a fat deep cut")
	_check(not sim.is_open(MID), "a fat deep cut doesn't open into the cavity")


func _retraction_and_tears() -> void:
	var sim := _sim()
	sim.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.MUSCLE)
	_settle(sim)
	var resting := sim.gap_at(MID)
	var edge := Vector2(0.5, 0.46)
	sim.grip(1, edge)
	sim.move_grip(1, sim.rest[sim.nearest(edge)] + Vector3(0, 0.005, -0.02))
	_settle(sim)
	_check(sim.gap_at(MID) > resting + 0.005, "pulling an edge 2 cm widens the gap")
	_check(sim.snapped.is_empty(), "a 2 cm pull doesn't tear")
	sim.move_grip(1, sim.rest[sim.nearest(edge)] + Vector3(0, 0.005, -0.08))
	_settle(sim)
	_check(not sim.snapped.is_empty(), "an 8 cm pull tears")
	sim.release(1)
	var tissue := _sim()
	tissue.tearing = false
	tissue.grip(1, edge)
	tissue.move_grip(1, tissue.rest[tissue.nearest(edge)] + Vector3(0, 0.005, -0.08))
	_settle(tissue)
	_check(tissue.snapped.is_empty(), "clients (tearing off) never snap springs themselves")


func _thin_skin_holds_at_rest() -> void:
	var sim := _sim()
	sim.break_mult = 0.5
	sim.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.MUSCLE)
	_settle(sim, 120)
	_check(sim.snapped.is_empty(), "thin skin (tear threshold x0.5) doesn't tear from its own tension")


## Into the fat: skin can't be closed over open muscle (see _muscle_first()).
func _stitches_close() -> void:
	var sim := _sim()
	sim.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.FAT)
	_settle(sim)
	var u := 0.2
	while u <= 0.8:
		sim.stitch(Vector2(u, 0.51), 0.95, 2.2)
		u += 0.01
	_settle(sim)
	_check(sim.gap_at(MID) == 0.0, "stitching along the whole cut closes it")
	_check(sim.triangles(TissueSim.Depth.SKIN).size() == TissueSim.RES * TissueSim.RES * 6, "a stitched cut shows no hole")
	sim.burst(MID, 0.5)
	_settle(sim)
	_check(sim.gap_at(MID) > TissueSim.OPEN_GAP, "a burst closure gapes again")


func _sleeps() -> void:
	var sim := _sim()
	sim.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.SKIN)
	_settle(sim, 200)
	_check(sim.is_sleeping(), "the sim sleeps once nothing moves")
	var before := sim.steps_done
	sim.step(1.0)
	_check(sim.steps_done == before, "a sleeping sim does no work")
	sim.shake(0.003)
	_check(not sim.is_sleeping(), "a jolt wakes the sim")


## Skin is elastic: a slow 3 cm pull on intact skin drags the skin around it along without tearing,
## and all of the moved skin is shown simulated (inside the region).
func _elastic() -> void:
	var sim := _sim()
	_settle(sim)
	var k := sim.nearest(MID)
	sim.grip(1, MID)
	for i in 30:
		sim.move_grip(1, sim.rest[k] + Vector3(0.001 * (i + 1), 0, 0))
		sim._substep()
	_settle(sim)
	_check(sim.snapped.is_empty(), "a slow 3 cm pull on intact skin doesn't tear it")
	var behind := k - 3
	var moved := sim.pos[behind].distance_to(sim.rest[behind])
	_check(moved > 0.008, "skin 4 cm behind a 3 cm pull follows it by more than 8 mm (%.1f mm)" % (moved * 1000.0))
	var region := sim.region()
	var hidden := 0
	for p in sim.pos.size():
		if sim.pos[p].distance_to(sim.settled[p]) > 0.002 and region[p] == 0:
			hidden += 1
	_check(hidden == 0, "all visibly moved skin is inside the simulated region (%d points outside)" % hidden)


## A cut through the muscle retracts and keeps the cavity open until the muscle is sewn. Sewn muscle closes the
## muscle layer and the cavity; the skin still gapes on its own until it's stitched too.
func _muscle_first() -> void:
	var fat := _sim()
	fat.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.FAT)
	_settle(fat, 120)
	var sim := _sim()
	sim.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.MUSCLE)
	_settle(sim, 120)
	_check(sim.gap_at(MID) > fat.gap_at(MID) + 0.002, "cut muscle retracts: its gap is wider than a cut into the fat (%.1f vs %.1f mm)" % [sim.gap_at(MID) * 1000.0, fat.gap_at(MID) * 1000.0])
	_check(sim.muscle_open_near(MID, 0.03), "a cut through the muscle leaves the muscle open")
	var u := 0.2
	while u <= 0.8:
		sim.muscle_stitch(Vector2(u, 0.51), 0.03)
		u += 0.015
	_settle(sim, 120)
	_check(not sim.muscle_open_near(MID, 0.03), "sewing along the muscle closes it")
	_check(not sim.is_open(MID), "sewn muscle closes the cavity")
	_check(sim.triangles(TissueSim.Depth.MUSCLE).size() == TissueSim.RES * TissueSim.RES * 6, "sewn muscle shows no hole in the muscle layer")
	_check(sim.gap_at(MID) > TissueSim.OPEN_GAP, "the skin over sewn muscle still gapes until it's stitched")


## A stitch closes the cut only where it pulls the edges together: a loose one leaves the rest of the gap open.
func _loose_stitch_gapes() -> void:
	var sims: Array[TissueSim] = []
	for tension: float in [0.95, 1.6]:
		var sim := _sim()
		sim.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.FAT)
		_settle(sim)
		var u := 0.2
		while u <= 0.8:
			sim.stitch(Vector2(u, 0.51), tension, 2.2)
			u += 0.01
		_settle(sim)
		sims.append(sim)
	_check(sims[0].gap_at(MID) == 0.0, "a tight stitch closes the gap")
	_check(sims[1].gap_at(MID) > TissueSim.OPEN_GAP * 0.5, "a loose stitch leaves the gap open (%.1f mm)" % (sims[1].gap_at(MID) * 1000.0))
	_check(sims[1].triangles(TissueSim.Depth.SKIN).size() < TissueSim.RES * TissueSim.RES * 6, "a loosely stitched cut still shows its opening")


## A client mirrors the host's tears spring by spring, diagonals included, and ends with identical topology.
func _exact_snaps() -> void:
	var host := _sim()
	var client := _sim()
	client.tearing = false
	for sim: TissueSim in [host, client]:
		sim.cut(Vector2(0.2, 0.3), Vector2(0.8, 0.7), TissueSim.Depth.FAT)
		sim.stitch(Vector2(0.5, 0.5), 1.0, 1.05)
	var edge := Vector2(0.45, 0.55)
	host.grip(1, edge)
	host.move_grip(1, host.rest[host.nearest(edge)] + Vector3(-0.03, 0.01, 0.06))
	_settle(host)
	_check(not host.snapped.is_empty(), "a hard diagonal pull tears springs")
	for entry: Array in host.snapped:
		client.snap_spring(entry[3])
	_check(client.topology_hash() == host.topology_hash(), "the client's torn springs match the host's exactly")


## Contact follows the skin as it's deformed: lifted by a grip it's higher, over an opening there's no skin.
func _deformed_surface() -> void:
	var sim := _sim()
	var spot := Vector2(0.5, 0.3)
	_check(absf(sim.skin_height(spot)) < 0.0001, "resting skin lies on the body")
	sim.grip(1, spot)
	sim.move_grip(1, sim.rest[sim.nearest(spot)] + Vector3(0, 0.012, 0))
	_settle(sim)
	_check(sim.skin_height(spot) > 0.008, "skin lifted by a grip is higher where it's lifted (%.1f mm)" % (sim.skin_height(spot) * 1000.0))
	_check(absf(sim.skin_height(Vector2(0.1, 0.9))) < 0.001, "skin far from the grip stays put")
	sim.release(1)
	sim.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.MUSCLE)
	_settle(sim, 120)
	_check(is_nan(sim.skin_height(Vector2(0.5, 0.51))), "no skin over an open incision")


## Skin under tension over a round body (a 25 cm radius, like a torso) settles once when it's built and then rests:
## not shown simulated, asleep.
func _rests_on_curved_body() -> void:
	var sim := TissueSim.new()
	sim.build(SIZE, func(uv: Vector2) -> float:
		var x := (uv.x - 0.5) * SIZE.x
		return sqrt(0.25 * 0.25 - x * x) - 0.25)
	_settle(sim)
	var moved := 0.0
	for k in sim.pos.size():
		moved = maxf(moved, sim.pos[k].distance_to(sim.settled[k]))
	_check(moved < TissueSim.REGION_MOVE, "skin over a round body stays where it settled (moved %.1f mm)" % (moved * 1000.0))
	_check(sim.region().count(1) == 0, "untouched skin over a round body isn't shown simulated")
	_check(sim.is_sleeping(), "untouched skin over a round body sleeps")


## Where the site hangs off the body (here past uv.x 0.8), the skin is never drawn and never shown simulated,
## whatever is cut or pulled next to it.
func _stays_on_body() -> void:
	var sim := TissueSim.new()
	sim.tearing = true
	sim.build(SIZE, func(_uv: Vector2) -> float: return 0.0, func(uv: Vector2) -> bool: return uv.x <= 0.8)
	sim.cut(Vector2(0.1, 0.51), Vector2(0.95, 0.51), TissueSim.Depth.MUSCLE)
	var edge := Vector2(0.75, 0.46)
	sim.grip(1, edge)
	sim.move_grip(1, sim.rest[sim.nearest(edge)] + Vector3(0.01, 0.01, -0.02))
	_settle(sim)
	var drawn := sim.triangles(TissueSim.Depth.SKIN)
	var region := sim.region()
	var drawn_off := 0
	for k in drawn:
		drawn_off += sim.off[k]
	var shown_off := 0
	for k in sim.pos.size():
		if sim.off[k] == 1:
			shown_off += region[k]
	_check(sim.off.count(1) > 0, "the test site has skin off the body")
	_check(drawn_off == 0, "no skin is drawn off the body (%d triangle corners)" % drawn_off)
	_check(shown_off == 0, "no skin off the body is shown simulated (%d points)" % shown_off)
