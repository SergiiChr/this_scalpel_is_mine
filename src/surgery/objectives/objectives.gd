class_name Objectives
extends Node
## Scenario steps, host only. Required steps complete in order, optional ones any time for bonus points.
## Each step type is a small check in ObjectiveChecks.

var steps: Array[Dictionary] = []
## Per step: {"done": bool, "timer": float}
var states: Array[Dictionary] = []


func setup(scenario: ScenarioDef) -> void:
	for step: Dictionary in scenario.steps:
		steps.append(step)
		states.append({"done": false, "timer": 0.0})


func current_index() -> int:
	for i in steps.size():
		if not states[i].done and not steps[i].get("optional", false):
			return i
	return -1


func all_done() -> bool:
	return current_index() == -1


func tick(delta: float, surgery: Surgery) -> void:
	var current := current_index()
	for i in steps.size():
		var optional: bool = steps[i].get("optional", false)
		if states[i].done or (i != current and not optional):
			continue
		if ObjectiveChecks.check(steps[i], states[i], surgery, delta):
			states[i].done = true
			surgery.scoring.add("optional_done" if optional else "objective_done")
			surgery.announce_debug("Done: %s" % steps[i].label)


## Compact state for the HUD: [[label, done, optional, is_current], ...]
func snapshot() -> Array:
	var current := current_index()
	var out: Array = []
	for i in steps.size():
		out.append([steps[i].label, states[i].done, steps[i].get("optional", false), i == current])
	return out
