extends Surgeon
## Measures only the surgeon's game work, in the engine's normal physics callback with real input and networking.
## Whole-surgery frame time (patient tissue, tools, director, UI) remains covered by the scenario/driver budgets.

var measuring := false
var ticks := 0
var worst_usec := 0


func _physics_process(delta: float) -> void:
	var start := Time.get_ticks_usec()
	super._physics_process(delta)
	if measuring:
		ticks += 1
		worst_usec = maxi(worst_usec, Time.get_ticks_usec() - start)


func begin_measurement() -> void:
	ticks = 0
	worst_usec = 0
	measuring = true
