extends RefCounted
## The game's own work per frame: the wall time from one frame to the next, sampled once per frame while the dynamic
## part runs. Run with --fixed-fps (frames don't wait for the clock) and with rendering off or headless (no waiting for
## the screen), so what's measured is the game, not VSync or the test machine's GPU. Godot's Performance time monitors
## can't stand in: they hold the worst frame of the last whole second.
## After a pause in sampling (loading, a screenshot), resume() starts again without counting the gap.

## Interactive gameplay must stay within this per frame so 60 fps remains possible.
const BUDGET := 0.016

var _frames := 0
var _worst := 0.0
var _last_usec := 0
## What was going on during the slowest frame, to find where to look.
var _worst_during := ""


func resume() -> void:
	_last_usec = 0


## Records the time since the last sample, `during` what (shown in summary() if it's the slowest).
func sample(during: String = "") -> void:
	var now := Time.get_ticks_usec()
	if _last_usec > 0:
		var time := (now - _last_usec) / 1000000.0
		_frames += 1
		if time > _worst:
			_worst = time
			_worst_during = during
	_last_usec = now


func clear() -> void:
	_frames = 0
	_worst = 0.0
	_worst_during = ""
	resume()


## The slowest frame recorded, in seconds.
func worst() -> float:
	return _worst


func within() -> bool:
	return worst() <= BUDGET


func summary() -> String:
	var out := "worst frame %.1f ms of game work over %d frames (budget %.0f ms)" % [worst() * 1000.0, _frames, BUDGET * 1000.0]
	return out + (", during: " + _worst_during if _worst_during else "")
