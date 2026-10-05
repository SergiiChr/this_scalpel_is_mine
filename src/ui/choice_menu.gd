class_name ChoiceMenu
extends RefCounted
## A popup list of buttons. Used for the blood panel.


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

