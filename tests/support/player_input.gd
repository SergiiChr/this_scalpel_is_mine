extends RefCounted
## A player's keys, buttons and mouse, sent through the engine's input the way the hardware's are. A test never calls
## an input handler itself: the game then sees each press in the same place in the frame as a player's (dispatched
## before the next frame's physics), so a test catches what a player would see. Actions rather than raw mouse buttons:
## the test runner's own panel would catch a click.
## Everything sent reaches the game when the engine next dispatches input, at the start of the next frame: await
## delivered() before counting physics frames for what it did (without --fixed-fps several can come first).


## Presses (with pressed false, lets go of) an input action, as its key or mouse button does.
static func action(name: String, pressed: bool = true) -> void:
	var event := InputEventAction.new()
	event.action = name
	event.pressed = pressed
	Input.parse_input_event(event)


## Presses and lets go of an action, like a click.
static func tap(name: String) -> void:
	action(name)
	action(name, false)


## Presses the keyboard key bound to an action: for what listens to keys themselves (a quick-time event).
static func key(name: String) -> void:
	var events := InputMap.action_get_events(name).filter(func(e: InputEvent) -> bool: return e is InputEventKey)
	var event: InputEventKey = events[0].duplicate()
	event.pressed = true
	Input.parse_input_event(event)
	var up: InputEventKey = event.duplicate()
	up.pressed = false
	Input.parse_input_event(up)


## Moves the mouse `motion` pixels: it looks around, or with a hand's key held moves that hand, or with Aim tool held
## turns its tool (Surgeon._unhandled_input()).
## `motion` is what the game gets: the engine scales a move on the window to the game's own layout (1920 x 1080), so it's
## sent scaled back by as much, whatever the window's size (tiny headless).
static func mouse(motion: Vector2) -> void:
	var event := InputEventMouseMotion.new()
	var root := (Engine.get_main_loop() as SceneTree).root
	event.relative = root.get_final_transform().basis_xform(motion)
	Input.parse_input_event(event)


## The mouse move (pixels) that moves `surgeon`'s active hand by `move` (world, across the floor): Surgeon.steer_hand()
## undone, outside the needle view.
static func hand_motion(surgeon: Surgeon, move: Vector3) -> Vector2:
	var zoomed := tan(deg_to_rad(Surgeon.ZOOM_FOV[surgeon.zoom]) * 0.5) / tan(deg_to_rad(Surgeon.ZOOM_FOV[0]) * 0.5)
	var step := Basis(Vector3.UP, surgeon.rotation.y).inverse() * move
	return Vector2(step.x, step.z) / (Surgeon.HAND_SENSITIVITY * surgeon.status.hand_speed() * zoomed * Settings.mouse_sensitivity)


## The key that makes the mouse move `surgeon`'s active hand.
static func hand_key(surgeon: Surgeon) -> String:
	return "move_left_hand" if surgeon.active == 0 else "move_right_hand"


## Waits until the game has acted on what was sent: the engine dispatches input at the start of a frame, before its
## physics, so the next physics step comes after it wherever in a frame this is called, and the frame's processing
## after that step.
static func delivered() -> void:
	var tree := Engine.get_main_loop() as SceneTree
	await tree.physics_frame
	await tree.process_frame
