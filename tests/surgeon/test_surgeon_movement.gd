extends GutTest
## Real local input and normal surgery physics frames: walking animation must not bob the camera or reach origin.
## Crouching changes only their height, while the visual rig leans forward. A fallen surgeon's face tracks the patient.

const TAGS = ["smoke", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const Driver := preload("res://tests/support/surgery_driver.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")

var _driver: Driver
var _frames: KeyFrames


func before_all() -> void:
	if KeyFrames.wanted():
		var folder := ProjectSettings.globalize_path("res://build/key-frames/surgeon_movement")
		if not DirAccess.dir_exists_absolute(folder):
			return
		for file in DirAccess.get_files_at(folder):
			if file.ends_with(".png"):
				DirAccess.remove_absolute(folder.path_join(file))


func before_each() -> void:
	RenderingServer.render_loop_enabled = false
	_driver = Driver.new()
	add_child(_driver)
	await _driver.start("appendectomy")
	_frames = KeyFrames.new()
	add_child(_frames)
	_frames.begin(_driver.surgery, "res://build/key-frames/surgeon_movement")
	_driver.on_key_frame = _capture
	SurgeryState.surgeon_is_steady(_driver.me)


func after_each() -> void:
	for action in ["move_left", "move_right", "crouch"]:
		Input.action_release(action)
	_frames.end()
	await _driver.stop()
	_frames.queue_free()
	_driver.queue_free()
	RenderingServer.render_loop_enabled = true


func test_walking_and_crouching_keep_camera_and_gameplay_reach_steady() -> void:
	var me := _driver.me
	await _driver.frames(10)
	var initial := me.global_position
	var target := me.shoulder(0) + Vector3.DOWN * (Surgeon.REACH - 0.001)
	SurgeryState.surgeon_hand_is_attached(me, 0, target)
	await _driver.capture("standing")
	var max_drop := 0.0
	_driver.budget.clear()
	Input.action_press("move_right")
	_driver.note("walking sideways with a hand near the reach boundary")
	for frame in 24:
		await _driver.frames(1)
		_check_stable_origins(me, 0.0)
		assert_false(me._strain[0], "a target inside reach is not strained by a walking step")
		max_drop = maxf(max_drop, me._walk_drop)
		if frame == 8:
			await _driver.capture("walking")
	Input.action_release("move_right")
	assert_gt(me.global_position.distance_to(initial), 0.1, "normal movement input actually walked the surgeon")
	assert_gt(max_drop, 0.01, "the visible leg animation moved the pelvis while gameplay origins stayed steady")
	Input.action_press("crouch")
	_driver.note("crouching through normal input")
	for frame in 18:
		await _driver.frames(1)
		_check_stable_origins(me, me.crouch)
	assert_almost_eq(me.crouch, 1.0, 0.001, "normal input reaches the deep squat")
	await _driver.capture("crouched")
	Input.action_release("crouch")
	_driver.note("standing back up through normal input")
	for frame in 18:
		await _driver.frames(1)
		_check_stable_origins(me, me.crouch)
	assert_almost_eq(me.crouch, 0.0, 0.001, "releasing crouch restores standing height")
	await _driver.capture("recovered")
	_driver.budget.check(self, KeyFrames.wanted())


func _check_stable_origins(me: Surgeon, crouch: float) -> void:
	assert_lt(me.to_local(me.camera().global_position).distance_to(Vector3(0, Surgeon.EYE_HEIGHT - crouch * Surgeon.CROUCH_DROP, 0)), 0.0001, "camera only lowers with crouch; no animation bob or sway")
	for index in 2:
		var side := -1.0 if index == 0 else 1.0
		var expected := Vector3(side * Surgeon.SHOULDER.x, Surgeon.SHOULDER.y - crouch * Surgeon.CROUCH_DROP, Surgeon.SHOULDER.z)
		assert_lt(me.to_local(me.shoulder(index)).distance_to(expected), 0.0001, "reach origin is independent of torso/leg animation")
		var sleeve := me.hands[index]._upper
		var start := sleeve.global_transform * Vector3(0, -0.5, 0)
		assert_lt(start.distance_to(me.visual_shoulder(index)), 0.0001, "sleeve still follows its visual shoulder")


func test_downed_visible_head_turns_to_patient_on_both_fall_sides() -> void:
	var me := _driver.me
	var puppet := SurgeryState.surgeon_has_remote_copy(me)
	var camera := Camera3D.new()
	_driver.surgery.add_child(camera)
	camera.fov = 55
	_driver.budget.clear()
	for side: float in [-1.0, 1.0]:
		SurgeryState.surgeon_is_knocked_out(me, side)
		_driver.note("falling onto the floor and looking at the patient")
		for frame in 50:
			SurgeryState.surgeon_copy_has_owner_state(me, puppet)
			await _driver.frames(1)
		assert_almost_eq(me._down, 1.0, 0.001, "normal physics reaches the lying pose")
		var camera_toward := (_driver.patient.global_position - me.camera().global_position).slide(Vector3.UP).normalized()
		assert_gt((-me.camera().global_basis.z).slide(Vector3.UP).normalized().dot(camera_toward), 0.999, "the fallen first-person camera still turns toward the patient")
		var face := puppet._face
		assert_almost_eq((-face.global_basis.z).dot(Vector3.UP), sin(puppet.pitch), 0.001, "visible fallen face keeps the owner's upward look")
		var pivot := face.global_transform * Surgeon.HEAD_PIVOT
		var to_patient := (_driver.patient.global_position - pivot).slide(Vector3.UP).normalized()
		assert_gt((-face.global_basis.z).slide(Vector3.UP).normalized().dot(to_patient), 0.999, "visible face looks toward the patient from either fall side")
		var torso := puppet._joints.Torso as Node3D
		var anchor := torso.global_transform * (Vector3(0, Surgeon.EYE_HEIGHT - Surgeon.HIP_HEIGHT, 0) + Surgeon.HEAD_PIVOT)
		assert_lt(pivot.distance_to(anchor), 0.0001, "patient-facing yaw preserves the neck anchor")
		if KeyFrames.wanted():
			_driver.budget_paused = true
			camera.global_position = pivot + Vector3(side * 0.8, 0.6, -0.7)
			camera.look_at(pivot)
			camera.make_current()
			assert_true(await _frames.capture_view("fallen_left" if side > 0 else "fallen_right"), "saved other player's view of the fallen head")
			me.camera().make_current()
			_driver.budget_paused = false
			_driver.budget.resume()
	_driver.budget.check(self, KeyFrames.wanted())


func _capture(name: String) -> void:
	if not KeyFrames.wanted():
		return
	assert_true(await _frames.capture_view(name), "saved " + name)
