class_name InputActions
extends RefCounted
## Every input action the game reads, with its default binding.
## Settings registers these at startup and stores player overrides on top.

## Order here is the order in the settings menu and the on-screen hint.
const DEFAULTS: Array[Dictionary] = [
	{"action": "move_forward", "label": "Move forward", "key": KEY_W},
	{"action": "move_back", "label": "Move back", "key": KEY_S},
	{"action": "move_left", "label": "Move left", "key": KEY_A},
	{"action": "move_right", "label": "Move right", "key": KEY_D},
	{"action": "use_tool", "label": "Use tool", "mouse": MOUSE_BUTTON_LEFT},
	{"action": "look", "label": "Look around (hold)", "mouse": MOUSE_BUTTON_RIGHT},
	{"action": "hand_up", "label": "Raise hand / more pressure", "mouse": MOUSE_BUTTON_WHEEL_UP},
	{"action": "hand_down", "label": "Lower hand / less pressure", "mouse": MOUSE_BUTTON_WHEEL_DOWN},
	{"action": "grab", "label": "Grab / release", "key": KEY_E},
	{"action": "interact", "label": "Interact", "key": KEY_F},
	{"action": "switch_hand", "label": "Switch hand", "key": KEY_TAB},
	{"action": "lift", "label": "Lift hand over (hold)", "key": KEY_SHIFT},
	{"action": "steady", "label": "Hold breath (hold)", "key": KEY_SPACE},
	{"action": "tilt_forward", "label": "Tilt tool forward", "key": KEY_R},
	{"action": "tilt_back", "label": "Tilt tool back", "key": KEY_T},
	{"action": "twist_left", "label": "Twist tool left", "key": KEY_Z},
	{"action": "twist_right", "label": "Twist tool right", "key": KEY_X},
	{"action": "drink", "label": "Drink / wear", "key": KEY_H},
	{"action": "belt_1", "label": "Belt slot 1", "key": KEY_1},
	{"action": "belt_2", "label": "Belt slot 2", "key": KEY_2},
	{"action": "belt_3", "label": "Belt slot 3", "key": KEY_3},
	{"action": "belt_4", "label": "Belt slot 4", "key": KEY_4},
	{"action": "pause", "label": "Pause", "key": KEY_ESCAPE},
]

## Shown in the bottom right corner during surgery.
const HINT_ACTIONS: PackedStringArray = [
	"use_tool", "grab", "interact", "switch_hand", "look", "lift", "steady", "tilt_forward", "twist_left", "drink", "belt_1",
]


## "key:87" / "mouse:1" <-> InputEvent. Strings keep the settings file readable.
static func encode(event: InputEvent) -> String:
	if event is InputEventKey:
		return "key:%d" % (event as InputEventKey).physical_keycode
	if event is InputEventMouseButton:
		return "mouse:%d" % (event as InputEventMouseButton).button_index
	return ""


static func decode(code: String) -> InputEvent:
	var parts := code.split(":")
	if parts.size() != 2:
		return null
	if parts[0] == "key":
		var key := InputEventKey.new()
		key.physical_keycode = int(parts[1]) as Key
		return key
	var mouse := InputEventMouseButton.new()
	mouse.button_index = int(parts[1]) as MouseButton
	return mouse


static func default_code(entry: Dictionary) -> String:
	return "key:%d" % entry.key if entry.has("key") else "mouse:%d" % entry.mouse


static func label_for(action: String) -> String:
	for entry in DEFAULTS:
		if entry.action == action:
			return entry.label
	return action


## Human readable binding, e.g. "E" or "Mouse 1".
static func binding_text(action: String) -> String:
	var events := InputMap.action_get_events(action) if InputMap.has_action(action) else []
	if events.is_empty():
		return "-"
	var event: InputEvent = events[0]
	if event is InputEventKey:
		return OS.get_keycode_string((event as InputEventKey).physical_keycode)
	match (event as InputEventMouseButton).button_index:
		MOUSE_BUTTON_LEFT: return "LMB"
		MOUSE_BUTTON_RIGHT: return "RMB"
		MOUSE_BUTTON_MIDDLE: return "MMB"
		MOUSE_BUTTON_WHEEL_UP: return "Wheel up"
		MOUSE_BUTTON_WHEEL_DOWN: return "Wheel down"
	return "Mouse %d" % (event as InputEventMouseButton).button_index
