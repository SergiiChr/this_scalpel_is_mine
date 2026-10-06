extends GutTest
## Debug objectives update immediately when their state changes and retain their layout while unchanged.

const TAGS = ["smoke"]


func test_objectives_change_without_invalidating_unchanged_label_themes() -> void:
	var debug_was := Settings.debug
	Settings.debug = true
	var surgery := Surgery.new()
	surgery.add_child(surgery._effects)
	var hud := Hud.new()
	hud.set_process(false)
	hud.surgery = surgery
	hud._objectives = VBoxContainer.new()
	hud.add_child(hud._objectives)
	add_child(hud)
	surgery.status = {"objectives": [["Close the incision", false, false, true]], "score": 0}
	hud._update_objectives()
	await get_tree().process_frame
	var objective := hud._objectives.get_child(0) as Label
	assert_eq(objective.text, "▶ Close the incision", "the active objective is shown")
	assert_eq(objective.get_theme_color("font_color"), Ui.PIP, "the active objective is highlighted")
	var changes := {"count": 0}
	objective.theme_changed.connect(func() -> void: changes.count += 1)
	for frame in 5:
		hud._update_objectives()
		await get_tree().process_frame
	assert_eq(changes.count, 0, "unchanged objectives keep their shaped text and layout")
	surgery.status.objectives[0] = ["Close the incision", true, false, false]
	surgery.status.score = 10
	hud._update_objectives()
	await get_tree().process_frame
	assert_eq(objective.text, "☑ Close the incision", "completion updates immediately")
	assert_eq(objective.get_theme_color("font_color"), Ui.DIM, "completed objectives are dimmed")
	assert_string_contains((hud._objectives.get_child(1) as Label).text, "Score 10", "the score updates alongside the objective")
	assert_gt(changes.count, 0, "a changed objective refreshes its theme")
	Settings.debug = debug_was
	hud.queue_free()
	surgery.free()
	await get_tree().process_frame
