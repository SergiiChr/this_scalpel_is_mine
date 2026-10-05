extends ToolAnimator
## Times the actual animation calls made by SurgicalTool's normal frame callback.
## Whole-surgery work remains measured by the scenario/driver budgets.

var measuring := false
var ticks := 0
var worst_usec := 0


func animate(active: bool, closed: bool, delta: float) -> void:
	var start := Time.get_ticks_usec()
	super.animate(active, closed, delta)
	if measuring:
		ticks += 1
		worst_usec = maxi(worst_usec, Time.get_ticks_usec() - start)
