extends GutHookScript
## Pre-run hook for every test process run_tests.sh starts.
## GUT_EXACT_CASE selects one exact case for process-isolated suites: GUT's CLI name filter is a substring match, so
## test_hand_stitch would otherwise also run test_hand_stitch_child in the same process.
## GUT_TEST_TIMEOUT (seconds) ends the run when one test takes longer, naming it. GUT itself only limits single waits.


func run() -> void:
	var wanted := OS.get_environment("GUT_EXACT_CASE")
	if not wanted.is_empty():
		var found := 0
		for script in gut.get_test_collector().scripts:
			script.tests = script.tests.filter(func(test) -> bool: return test.name == wanted)
			found += script.tests.size()
		if found != 1:
			gut.logger.error("Expected one exact case %s, found %d" % [wanted, found])
			set_exit_code(1)
			abort()
			return
	var limit := OS.get_environment("GUT_TEST_TIMEOUT").to_float()
	if limit > 0.0:
		var watchdog := Watchdog.new()
		watchdog.limit_msec = int(limit * 1000.0)
		gut.add_child(watchdog)
		gut.start_test.connect(watchdog.started)
		gut.end_test.connect(watchdog.ended)


## Counts real time, not game time: --fixed-fps runs the game faster than the clock, and the limit is about the run.
## A test stuck awaiting can't be stopped on its own, so the whole process quits. A test blocking the main thread
## never lets this run; run_tests.sh's process timeout catches that.
class Watchdog extends Node:
	var limit_msec := 0
	var _test := ""
	var _since := 0

	func _ready() -> void:
		process_mode = Node.PROCESS_MODE_ALWAYS

	func started(test_name: String) -> void:
		_test = test_name
		_since = Time.get_ticks_msec()

	func ended() -> void:
		_test = ""

	func _process(_delta: float) -> void:
		if _test.is_empty() or Time.get_ticks_msec() - _since < limit_msec:
			return
		print("[ERROR] %s took longer than %d s (GUT_TEST_TIMEOUT); stopped." % [_test, limit_msec / 1000])
		_test = ""
		get_tree().quit(1)
