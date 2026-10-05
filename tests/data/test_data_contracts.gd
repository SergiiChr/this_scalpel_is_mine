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


func test_every_chronic_condition_has_a_manual_entry_quoting_its_chart_line() -> void:
	var entries := _conditions_page().children
	assert_gt(entries.size(), 0, "the manual has a chronic conditions section")
	for quirk: QuirkDef in Db.patient_quirks.values():
		for variant: String in quirk.variants if not quirk.variants.is_empty() else PackedStringArray([""]):
			var card := quirk.text("card", variant)
			# A strong heart ("Marathon runner") is on the chart but isn't a condition to manage.
			if card.is_empty() or quirk.is_red_herring() or quirk.polarity(variant) == "positive":
				continue
			var key := "%s.%s" % [quirk.id, variant] if variant else quirk.id
			var entry: ManualPage = null
			for page: ManualPage in entries:
				if key in page.tags or quirk.id in page.tags:
					entry = page
			assert_not_null(entry, "%s has a chronic conditions entry tagged %s" % [card, key])
			if entry:
				assert_string_contains(entry.body, "“%s”" % card, "the %s entry quotes the chart line" % entry.title)


func test_manual_pointers_name_existing_condition_entries() -> void:
	var titles := _conditions_page().children.map(func(p: ManualPage) -> String: return p.title)
	var pointer := RegEx.create_from_string("See Chronic conditions: ([^.\\n]+)\\.")
	for page: ManualPage in Db.manual:
		for found in pointer.search_all(page.body):
			for title: String in found.get_string(1).split(", "):
				assert_has(titles, title, "%s points to an existing condition entry" % page.title)


func test_site_bound_quirks_roll_only_on_their_sites() -> void:
	var rng := RandomNumberGenerator.new()
	rng.seed = 3
	var rolled_somewhere := false
	for scenario: ScenarioDef in Db.scenarios + Db.disabled_scenarios:
		for i in 60:
			for roll: Dictionary in QuirkRoller.roll_patient(scenario, rng):
				var fits := (Db.patient_quirks[roll.id] as QuirkDef).fits_site(scenario.site)
				if not fits:
					fail_test("%s rolls only on its sites, not on %s (%s)" % [roll.id, scenario.site, scenario.id])
				rolled_somewhere = rolled_somewhere or roll.id == "aneurysm"
	assert_true(rolled_somewhere, "an aneurysm rolls on a great-vessel site")


func _conditions_page() -> ManualPage:
	for page: ManualPage in Db.manual:
		if not page.children.is_empty():
			return page
	return ManualPage.new()


func test_manual_numbers_come_from_the_game() -> void:
	var filled := ManualPage.fill_numbers("{Patient.HIGH_PRESSURE} mmHg for {drug.diazepam.duration / 60} min, {quirk.heart_weak.arrest_mult}x", "test")
	var expected := "%d mmHg for %d min, %sx" % [Patient.HIGH_PRESSURE, Db.drug("diazepam").duration / 60.0, String.num((Db.patient_quirks.heart as QuirkDef).effects("weak").arrest_mult, 2)]
	assert_eq(filled, expected, "manual numbers are worked out from constants, drugs and quirk effects")
	# A number with a unit written straight into a page would go stale when the game changes: it has to be "{...}".
	var unit := "(mmHg|mmol/l|°C|mg|units|ml/s|ml|seconds?|minutes?|kg|points|times)\\b|%"
	var raw := RegEx.create_from_string("(?i)(\\b\\d+(\\.\\d+)?|\\b(one|two|three|four|five|six|ten|fifteen|twenty|thirty|forty|fifty|sixty)) ?(" + unit + ")")
	for page: ManualPage in _all_pages():
		assert_false(page.body.contains("{"), "%s has no number left to work out" % page.title)
		var text := RegEx.create_from_string(ManualPage.NUMBER).sub(page.source, "", true)
		text = text.get_slice("[font_size=16][b]References", 0)
		for found in raw.search_all(text):
			fail_test("%s writes \"%s\" by hand: use a {...} game number" % [page.title, found.get_string()])


func _all_pages() -> Array[ManualPage]:
	var pages: Array[ManualPage] = []
	for page: ManualPage in Db.manual:
		pages.append(page)
		pages.append_array(page.children)
	return pages
