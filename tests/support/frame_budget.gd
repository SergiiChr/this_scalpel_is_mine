extends RefCounted
## The game's own work per frame: the wall time from one frame to the next, sampled once per frame while the dynamic
## part runs. Run with --fixed-fps (frames don't wait for the clock) and with rendering off or headless (no waiting for
## the screen), so what's measured is the game, not VSync or the test machine's GPU. Godot's Performance time monitors
## can't stand in: they hold the worst frame of the last whole second.
## After a pause in sampling (loading, a screenshot), resume() starts again without counting the gap.

## Interactive gameplay must stay within this per frame so 60 fps remains possible.
const BUDGET := 0.016
## Set to 1 in a CI run (run_tests.sh --ci-run): shared CI machines are slower than the ones the budget is for, so the
## frame times are only reported there.
const CI_RUN := "CI_RUN"

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


## Whether this run fails on frames over the budget: everywhere but on CI.
static func enforced() -> bool:
	return OS.get_environment(CI_RUN) != "1"


## The one frame budget check for every test: fails `test` when a frame went over the budget, in a run that renders
## key frames (`with_key_frames`) and isn't on CI, and otherwise only reports the worst frame.
## Headless frame times depend on the machine and on scripts running alongside, so they're never checked.
func check(test: GutTest, with_key_frames: bool, label: String = "") -> void:
	var report := (label + ": " if label else "") + summary()
	if with_key_frames and enforced():
		test.assert_true(within(), report)
	else:
		test.gut.p(report)


func summary() -> String:
	var out := "worst frame %.1f ms of game work over %d frames (budget %.0f ms)" % [worst() * 1000.0, _frames, BUDGET * 1000.0]
	return out + (", during: " + _worst_during if _worst_during else "")
