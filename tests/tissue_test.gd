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


func _stitches_close() -> void:
	var sim := _sim()
	sim.cut(Vector2(0.2, 0.51), Vector2(0.8, 0.51), TissueSim.Depth.MUSCLE)
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
