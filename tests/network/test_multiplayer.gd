extends GutTest
## Real two-process ENet coverage. The shell helper is only process orchestration; GUT owns the cases and verdict.

const TAGS = ["smoke", "network"]
const DRIVER := "res://tests/support/network_runner.sh"


func test_multiplayer_cut_topology_and_tool_handoff_stay_in_sync() -> void:
	assert_eq(_run("sync"), 0, "host/client wounds, painted map, topology and handoff agree")


func test_ten_second_client_stall_keeps_session_and_pauses_input() -> void:
	assert_eq(_run("stall"), 0, "a stalled client stays connected and its held input is paused")


func _run(mode: String) -> int:
	var output: Array = []
	var executable := OS.get_executable_path()
	var root := ProjectSettings.globalize_path("res://")
	var code := OS.execute("/bin/bash", [ProjectSettings.globalize_path(DRIVER), executable, root, mode], output, true)
	if code != 0:
		gut.p("network %s driver failed:\n%s" % [mode, "\n".join(output)], 0)
	return code
