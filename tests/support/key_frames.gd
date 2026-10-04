extends Node
## Deliberate key frames of the surgical site for visual tests: capture(name) right after a named action saves a view
## from straight above the site and an oblique close-up that shows depth and intersections.
## Rendering is on only while a key frame is saved: whoever runs the game keeps it off in between.

## Key frames are taken only in a run with this environment variable set to 1 (run_tests.sh --with-key-frames sets it
## and gives the tests a display to render to). Without it the same cases run headless and every capture is skipped.
## GUT refuses command line arguments it doesn't know, so it can't be one.
const WITH_KEY_FRAMES := "WITH_KEY_FRAMES"

## Views of the site: from above, and 45° off toward the surgeon's side of the table.
const DISTANCE := 0.45
const FOV := 35.0

var out_dir := ""
var saved: PackedStringArray = []
## Key frames are numbered in the order they're taken, so the folder reads as the story of the surgery.
var _taken := 0
var _surgery: Surgery
var _camera: Camera3D


static func wanted() -> bool:
	return OS.get_environment(WITH_KEY_FRAMES) == "1"


## Starts a set of key frames for `surgery` into `dir` (res:// or absolute).
func begin(surgery: Surgery, dir: String) -> void:
	_surgery = surgery
	out_dir = ProjectSettings.globalize_path(dir)
	DirAccess.make_dir_recursive_absolute(out_dir)
	_camera = Camera3D.new()
	_camera.fov = FOV
	_camera.near = 0.01
	surgery.add_child(_camera)


func end() -> void:
	if is_instance_valid(_camera):
		_camera.queue_free()


## Saves both views as NN_<name>_top.png and NN_<name>_oblique.png. Returns false when one couldn't be saved.
## The oblique view comes from the surgeon's side, or from `side` (world, across the floor) when given: along a cut,
## say, to see what lies across it.
func capture(key_frame: String, side: Vector3 = Vector3.ZERO) -> bool:
	var body := _surgery.patient.body
	var middle := body.site.global_position
	var up := body.site.global_basis.y.normalized()
	var toward := (side if side != Vector3.ZERO else _surgery.local_surgeon.global_position - middle).slide(up).normalized()
	return await _views(key_frame, middle, up, toward, DISTANCE, false)


## The same two views of something off the site (a bottle on a tray), from `distance` away: from above, and 45° off
## toward the surgeon. The hands are left out of both: one just let go of it would hide it.
func capture_at(key_frame: String, at: Vector3, distance: float) -> bool:
	var toward := (_surgery.local_surgeon.global_position - at).slide(Vector3.UP).normalized()
	return await _views(key_frame, at, Vector3.UP, toward, distance, true)


func _views(key_frame: String, middle: Vector3, up: Vector3, toward: Vector3, distance: float, no_hands: bool) -> bool:
	key_frame = "%02d_%s" % [_taken, key_frame]
	_taken += 1
	var hud_was := _surgery.hud.visible
	_surgery.hud.visible = false
	_hide_test_overlay(true)
	_camera.current = true
	RenderingServer.render_loop_enabled = true
	var ok := true
	for view: Array in [["top", up, toward], ["oblique", (up + toward).normalized(), up]]:
		_camera.global_position = middle + (view[1] as Vector3) * distance
		_camera.look_at(middle, view[2])
		# From straight above the hands would hide the site; the oblique view shows them, with the tools on it.
		for hand in _surgery.local_surgeon.hands:
			hand.visible = view[0] != "top" and not no_hands
		# A few drawn frames: the first ones after the camera moves can still show the last view.
		for i in 3:
			await get_tree().process_frame
		var path := out_dir.path_join("%s_%s.png" % [key_frame, view[0]])
		ok = ok and get_viewport().get_texture().get_image().save_png(path) == OK
		saved.append(path)
	RenderingServer.render_loop_enabled = false
	for hand in _surgery.local_surgeon.hands:
		hand.visible = true
	_camera.current = false
	_surgery.local_surgeon.camera().current = true
	_surgery.hud.visible = hud_was
	_hide_test_overlay(false)
	return ok


## Saves what the surgeon sees, the HUD's aim included, as NN_<name>_view.png. Returns false when it couldn't be saved.
func capture_view(key_frame: String) -> bool:
	key_frame = "%02d_%s" % [_taken, key_frame]
	_taken += 1
	_hide_test_overlay(true)
	RenderingServer.render_loop_enabled = true
	for i in 3:
		await get_tree().process_frame
	var path := out_dir.path_join("%s_view.png" % key_frame)
	var ok := get_viewport().get_texture().get_image().save_png(path) == OK
	saved.append(path)
	RenderingServer.render_loop_enabled = false
	_hide_test_overlay(false)
	return ok


## GUT draws its own panel over the game window; it isn't part of what's being looked at.
func _hide_test_overlay(hide: bool) -> void:
	var runner := get_tree().root.get_node_or_null("GutRunner")
	if runner and runner.has_node("GutLayer/GutScene"):
		runner.get_node("GutLayer/GutScene").visible = not hide
