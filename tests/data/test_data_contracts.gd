extends GutTest
## The data files the game reads at startup hold together.

const TAGS = ["smoke"]


func test_every_main_menu_scenario_has_objectives() -> void:
	assert_gt(Db.scenarios.size(), 0, "at least one scenario is registered")
	for scenario: ScenarioDef in Db.scenarios + Db.disabled_scenarios:
		assert_false(scenario.id.is_empty(), "scenario id is present")
		assert_gt(scenario.steps.filter(func(step: Dictionary) -> bool: return not step.get("optional", false)).size(), 0, "%s has a required objective" % scenario.id)


func test_every_tool_is_found_by_its_own_id() -> void:
	for id: String in Db.tools:
		assert_eq(Db.tool(id).id, id, "tool %s knows its id" % id)


func test_scenario_ids_are_unique() -> void:
	var seen: Array[String] = []
	for scenario: ScenarioDef in Db.scenarios + Db.disabled_scenarios:
		assert_false(seen.has(scenario.id), "scenario id %s is used once (file names differ in more than their number)" % scenario.id)
		seen.append(scenario.id)


func test_every_tool_a_scenario_lists_exists() -> void:
	for scenario: ScenarioDef in Db.scenarios + Db.disabled_scenarios:
		for id: String in scenario.starting_tools + scenario.random_tools:
			assert_not_null(Db.tool(id), "%s lists tool %s" % [scenario.id, id])
