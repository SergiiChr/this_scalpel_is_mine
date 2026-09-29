class_name SettingsPanel
extends VBoxContainer
## Resolution, audio, mouse and key rebinding. Changes apply immediately, Save writes them to disk.

var _waiting_for: String = ""
var _binding_buttons: Dictionary = {}


func _ready() -> void:
	add_theme_constant_override("separation", 10)
	add_child(Ui.label("SETTINGS", 28, Ui.PIP))

	var display := Ui.hbox(12)
	var resolution := OptionButton.new()
	for res in Settings.RESOLUTIONS:
		resolution.add_item("%d x %d" % [res.x, res.y])
	resolution.select(maxi(Settings.RESOLUTIONS.find(Settings.resolution), 0))
	resolution.item_selected.connect(func(i: int) -> void:
		Settings.resolution = Settings.RESOLUTIONS[i]
		Settings.apply())
	display.add_child(Ui.label("Resolution", 18))
	display.add_child(resolution)
	var fullscreen := CheckBox.new()
	fullscreen.text = "Fullscreen"
	fullscreen.button_pressed = Settings.fullscreen
	fullscreen.toggled.connect(func(on: bool) -> void:
		Settings.fullscreen = on
		Settings.apply())
	display.add_child(fullscreen)
	add_child(display)

	for bus in Settings.BUSES:
		add_child(_slider_row("%s volume" % bus, Settings.volumes[bus], 0.0, 1.0, func(value: float) -> void:
			Settings.volumes[bus] = value
			Settings.apply()))
	add_child(_slider_row("Mouse sensitivity", Settings.mouse_sensitivity, 0.2, 3.0, func(value: float) -> void:
		Settings.mouse_sensitivity = value))

	var debug := CheckBox.new()
	debug.text = "Debug mode (show objectives and every scored action)"
	debug.button_pressed = Settings.debug
	debug.toggled.connect(func(on: bool) -> void: Settings.debug = on)
	add_child(debug)

	add_child(Ui.label("Controls (click, then press a key or mouse button)", 18, Ui.PIP))
	var grid := GridContainer.new()
	grid.columns = 4
	for entry in InputActions.DEFAULTS:
		var action: String = entry.action
		grid.add_child(Ui.label(entry.label, 16))
		var b := Ui.button(InputActions.binding_text(action), func() -> void: _start_rebind(action))
		b.custom_minimum_size.x = 140
		_binding_buttons[action] = b
		grid.add_child(b)
	add_child(Ui.scroll(grid))

	var buttons := Ui.hbox(12)
	buttons.add_child(Ui.button("Reset controls", func() -> void:
		Settings.reset_bindings()
		_refresh()))
	buttons.add_child(Ui.button("Save", Settings.save_settings))
	add_child(buttons)


func _slider_row(text: String, value: float, min_value: float, max_value: float, on_change: Callable) -> HBoxContainer:
	var row := Ui.hbox(12)
	var l := Ui.label(text, 18)
	l.custom_minimum_size.x = 220
	row.add_child(l)
	var slider := HSlider.new()
	slider.min_value = min_value
	slider.max_value = max_value
	slider.step = 0.01
	slider.value = value
	slider.custom_minimum_size.x = 300
	slider.value_changed.connect(on_change)
	row.add_child(slider)
	return row


func _start_rebind(action: String) -> void:
	_waiting_for = action
	(_binding_buttons[action] as Button).text = "press..."


func _input(event: InputEvent) -> void:
	if _waiting_for.is_empty():
		return
	var usable := event is InputEventKey and event.is_pressed() or event is InputEventMouseButton and event.is_pressed()
	if not usable:
		return
	get_viewport().set_input_as_handled()
	if not (event is InputEventKey and (event as InputEventKey).physical_keycode == KEY_ESCAPE and _waiting_for != "pause"):
		Settings.rebind(_waiting_for, event)
	_waiting_for = ""
	_refresh()


func _refresh() -> void:
	for action: String in _binding_buttons:
		(_binding_buttons[action] as Button).text = InputActions.binding_text(action)
