extends Node
## Player settings: display, audio, mouse and key bindings. Saved to user://settings.cfg.

const PATH := "user://settings.cfg"
const RESOLUTIONS: Array[Vector2i] = [
	Vector2i(1280, 720), Vector2i(1600, 900), Vector2i(1920, 1080), Vector2i(2560, 1440), Vector2i(3840, 2160),
]
const BUSES: PackedStringArray = ["Master", "SFX", "Voice", "Music"]

var resolution := Vector2i(1920, 1080)
var fullscreen := false
var mouse_sensitivity := 1.0
var volumes: Dictionary = {"Master": 0.8, "SFX": 1.0, "Voice": 1.0, "Music": 0.6}
## action -> encoded binding, only for actions the player changed.
var bindings: Dictionary = {}


func _ready() -> void:
	_create_buses()
	load_settings()
	apply()


func load_settings() -> void:
	var cfg := ConfigFile.new()
	if cfg.load(PATH) != OK:
		return
	resolution = cfg.get_value("display", "resolution", resolution)
	fullscreen = cfg.get_value("display", "fullscreen", fullscreen)
	mouse_sensitivity = cfg.get_value("input", "mouse_sensitivity", mouse_sensitivity)
	bindings = cfg.get_value("input", "bindings", {})
	for bus in BUSES:
		volumes[bus] = cfg.get_value("audio", bus, volumes[bus])


func save_settings() -> void:
	var cfg := ConfigFile.new()
	cfg.set_value("display", "resolution", resolution)
	cfg.set_value("display", "fullscreen", fullscreen)
	cfg.set_value("input", "mouse_sensitivity", mouse_sensitivity)
	cfg.set_value("input", "bindings", bindings)
	for bus in BUSES:
		cfg.set_value("audio", bus, volumes[bus])
	cfg.save(PATH)


func apply() -> void:
	_apply_bindings()
	for bus in BUSES:
		AudioServer.set_bus_volume_db(AudioServer.get_bus_index(bus), linear_to_db(maxf(volumes[bus], 0.0001)))
	if DisplayServer.get_name() == "headless":
		return
	DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_FULLSCREEN if fullscreen else DisplayServer.WINDOW_MODE_WINDOWED)
	if not fullscreen:
		DisplayServer.window_set_size(resolution)


func rebind(action: String, event: InputEvent) -> void:
	bindings[action] = InputActions.encode(event)
	_apply_bindings()


func reset_bindings() -> void:
	bindings.clear()
	_apply_bindings()


func _apply_bindings() -> void:
	for entry in InputActions.DEFAULTS:
		var action: String = entry.action
		if not InputMap.has_action(action):
			InputMap.add_action(action)
		InputMap.action_erase_events(action)
		var event := InputActions.decode(bindings.get(action, InputActions.default_code(entry)))
		if event:
			InputMap.action_add_event(action, event)


func _create_buses() -> void:
	for bus in BUSES:
		if AudioServer.get_bus_index(bus) == -1:
			AudioServer.add_bus()
			var index := AudioServer.bus_count - 1
			AudioServer.set_bus_name(index, bus)
			AudioServer.set_bus_send(index, "Master")
