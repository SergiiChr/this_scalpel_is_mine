extends GutTest

const TAGS = ["smoke"]


func test_every_main_menu_scenario_has_objectives() -> void:
	assert_gt(Db.scenarios.size(), 0, "at least one scenario is registered")
	for scenario: ScenarioDef in Db.scenarios:
		assert_false(scenario.id.is_empty(), "scenario id is present")
		assert_gt(scenario.steps.size(), 0, "%s has a walkthrough objective" % scenario.id)


func test_tool_and_scenario_ids_are_unique() -> void:
	var tool_ids: Array[String] = []
	for tool: ToolDef in Db.tools.values():
		assert_false(tool_ids.has(tool.id), "tool id %s is unique" % tool.id)
		tool_ids.append(tool.id)
	var scenario_ids: Array[String] = []
	for scenario: ScenarioDef in Db.scenarios:
		assert_false(scenario_ids.has(scenario.id), "scenario id %s is unique" % scenario.id)
		scenario_ids.append(scenario.id)
