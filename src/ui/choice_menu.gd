class_name ChoiceMenu
extends RefCounted
## A popup list of buttons. Used for the nurse bell (grouped by category) and the blood panel.


## choices: [[label, value], ...]. on_pick receives value.
static func build(title: String, subtitle: String, choices: Array, on_pick: Callable, on_close: Callable) -> Control:
	var list := Ui.vbox(6)
	list.add_child(Ui.label(title, 28, Ui.PIP))
	list.add_child(Ui.label(subtitle, 16, Ui.DIM, true))
	var grid := GridContainer.new()
	grid.columns = 3
	for choice: Array in choices:
		var value: Variant = choice[1]
		var b := Ui.button(choice[0], func() -> void: on_pick.call(value))
		b.custom_minimum_size = Vector2(320, 0)
		grid.add_child(b)
	list.add_child(Ui.scroll(grid))
	list.add_child(Ui.button("Never mind  [Esc]", on_close))
	var holder := CenterContainer.new()
	var p := Ui.panel(list)
	p.custom_minimum_size = Vector2(1040, 640)
	holder.add_child(p)
	return Ui.fullscreen(holder, Color(0, 0, 0, 0.6))


## Two steps: pick a group, then an item in it. groups: {group name: [[label, value], ...]}, in display order.
static func build_grouped(title: String, subtitle: String, groups: Dictionary, on_pick: Callable, on_close: Callable) -> Control:
	var list := Ui.vbox(6)
	list.add_child(Ui.label(title, 28, Ui.PIP))
	list.add_child(Ui.label(subtitle, 16, Ui.DIM, true))
	var grid := GridContainer.new()
	grid.columns = 3
	list.add_child(Ui.scroll(grid))
	var back := Ui.button("Never mind  [Esc]", on_close)
	list.add_child(back)
	# The two pages open each other, so they live in one dictionary both lambdas can see.
	var pages: Dictionary = {}
	pages.groups = func() -> void:
		_clear(grid)
		for group: String in groups:
			_add_button(grid, "%s  ›" % group, (pages.items as Callable).bind(group))
	pages.items = func(group: String) -> void:
		_clear(grid)
		_add_button(grid, "‹  Back", pages.groups)
		for choice: Array in groups[group]:
			_add_button(grid, choice[0], on_pick.bind(choice[1]))
	(pages.groups as Callable).call()
	var holder := CenterContainer.new()
	var p := Ui.panel(list)
	p.custom_minimum_size = Vector2(1040, 640)
	holder.add_child(p)
	return Ui.fullscreen(holder, Color(0, 0, 0, 0.6))


static func _clear(grid: GridContainer) -> void:
	for child in grid.get_children():
		child.queue_free()


static func _add_button(grid: GridContainer, text: String, on_press: Callable) -> void:
	var b := Ui.button(text, on_press)
	b.custom_minimum_size = Vector2(320, 0)
	grid.add_child(b)
