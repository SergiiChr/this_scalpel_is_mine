extends GutHookScript
## Selects one exact case for process-isolated suites. GUT's CLI name filter is a substring match,
## so test_hand_stitch would otherwise also run test_hand_stitch_child in the same process.


func run() -> void:
	var wanted := OS.get_environment("GUT_EXACT_CASE")
	var found := 0
	for script in gut.get_test_collector().scripts:
		script.tests = script.tests.filter(func(test) -> bool: return test.name == wanted)
		found += script.tests.size()
	if wanted.is_empty() or found != 1:
		gut.logger.error("Expected one exact case %s, found %d" % [wanted, found])
		set_exit_code(1)
		abort()
