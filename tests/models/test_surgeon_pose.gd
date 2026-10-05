extends GutTest
## Grounded deep squat, torso-anchored neck and separated ceiling panel. The puppet receives the owner's packed
## state; hard geometry checks cover the transition and rendered front/side/back views expose intersections.

const TAGS = ["smoke", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const KeyFrames := preload("res://tests/support/key_frames.gd")
const FrameBudget := preload("res://tests/support/frame_budget.gd")

var _room: Room
var _owner: Surgeon
var _puppet: Surgeon
var _camera: Camera3D
var _shot := 0


func before_all() -> void:
	if KeyFrames.wanted():
		var folder := ProjectSettings.globalize_path("res://build/key-frames/surgeon_pose")
		for file in DirAccess.get_files_at(folder):
			if file.ends_with(".png"):
				DirAccess.remove_absolute(folder.path_join(file))


func before_each() -> void:
	RenderingServer.render_loop_enabled = false
	_room = Room.new()
	add_child_autofree(_room)
	_room.layout = Room.LAYOUTS.or
	_room._build_environment()
	_room._build_shell()
	_owner = _surgeon(2)
	_puppet = _surgeon(3)
	_owner._body.visible = false
	_owner._face.visible = false
	for hand in _owner.hands:
		hand.visible = false
	_camera = Camera3D.new()
	_camera.fov = 55
	_room.add_child(_camera)
	_camera.make_current()


func after_each() -> void:
	RenderingServer.render_loop_enabled = true


func _surgeon(peer: int) -> Surgeon:
	var surgeon := Surgeon.new()
	_room.add_child(surgeon)
	surgeon.setup(peer, "", [], Transform3D.IDENTITY)
	surgeon.set_physics_process(false)
	return surgeon


func _receive_pose(crouch: float, pitch: float, speed: float = 0.0) -> void:
	_owner.crouch = crouch
	_owner.pitch = pitch
	_owner._last_position = _owner.global_position - Vector3(speed / 60.0, 0, 0)
	_puppet._last_position = _puppet.global_position - Vector3(speed / 60.0, 0, 0)
	_owner._animate_body(1.0 / 60.0)
	_puppet._sync_state(_owner._pack_state())
	_puppet._animate_body(1.0 / 60.0)
	for hand in _puppet.hands:
		hand.target = _puppet.to_global(Vector3(-0.17 if hand.index == 0 else 0.17, 1.05 - crouch * Surgeon.CROUCH_DROP, -0.38))
		_puppet.support_elbow(hand)
		hand.snap_pose(_puppet.shoulder(hand.index))


func test_synced_deep_squat_keeps_heels_down_neck_in_collar_and_recovers() -> void:
	_receive_pose(0, 0)
	_check_neck_inside_collar()
	await _capture("standing", Vector3(1.4, 1.25, -1.85), Vector3(0, 0.85, 0))
	var budget := FrameBudget.new()
	for step in 61:
		_receive_pose(step / 60.0, -0.55)
		_check_leg_geometry("lowering %d" % step)
		if _puppet.crouch > 0.55:
			_check_sleeves_clear_knees()
		budget.sample("lowering into a deep squat")
		await get_tree().process_frame
		if step == 30:
			await _capture("squat_transition", Vector3(1.5, 1.15, -1.6), Vector3(0, 0.7, 0))
			budget.resume()
	await _capture("squat_front", Vector3(0.8, 0.85, -1.75), Vector3(0, 0.48, 0))
	await _capture("squat_side", Vector3(2.2, 0.75, 0.1), Vector3(0, 0.48, 0))
	var hip := (_puppet._joints.LegL as Node3D).global_position
	var knee := (_puppet._joints.ShinL as Node3D).global_position
	assert_lt(hip.y, 0.24, "hips drop below knee height in a deep squat")
	assert_gt(knee.y, hip.y + 0.08, "deeply flexed knee sits above the hips")
	assert_lt(knee.z, hip.z - 0.2, "knees point forward, rather than rigid legs pointing behind")
	assert_gt(absf(knee.x), 0.2, "knees open out to make room for the torso")
	var squatting_torso := _puppet._joints.Torso as Node3D
	assert_gt(squatting_torso.global_basis.y.dot(Vector3.FORWARD), 0.4, "torso leans forward at the hips in the deep squat")
	assert_lt(_puppet._face.global_position.z, hip.z - 0.2, "head stays forward of the hips rather than leaning back")
	_check_sleeves_clear_knees()
	# The floor-reaching hand pose must also clear the knee, with the same controlled hand position.
	for height in [0.04, 0.2, 0.3, 0.55]:
		for hand in _puppet.hands:
			hand.target = _puppet.to_global(Vector3(-0.17 if hand.index == 0 else 0.17, height, -0.38))
			hand.snap_pose(_puppet.shoulder(hand.index))
		_check_sleeves_clear_knees()
	assert_almost_eq(_puppet.camera().global_position.y, Surgeon.EYE_HEIGHT - Surgeon.CROUCH_DROP, 0.001, "view height follows crouch gameplay height")
	budget.resume()
	for step in 61:
		_receive_pose(1.0 - step / 60.0, -0.55)
		_check_leg_geometry("rising %d" % step)
		if _puppet.crouch > 0.55:
			_check_sleeves_clear_knees()
		budget.sample("standing back up")
		await get_tree().process_frame
	await _capture("recovered", Vector3(1.4, 1.25, -1.85), Vector3(0, 0.85, 0))
	gut.p(budget.summary())
	if FrameBudget.enforced():
		assert_true(budget.within(), budget.summary())
	for crouch in [0.0, 0.5, 1.0]:
		for pitch in [Surgeon.LOOK_PITCH.x, 0.0, Surgeon.LOOK_PITCH.y]:
			_receive_pose(crouch, pitch)
			var torso := _puppet._joints.Torso as Node3D
			var neck := _puppet._body.find_child("Neck", true, false) as Node3D
			var anchor := torso.global_transform * Vector3(0, 1.54 - Surgeon.HIP_HEIGHT, 0.02)
			assert_lt((_puppet._face.global_transform * Surgeon.HEAD_PIVOT).distance_to(anchor), 0.001, "head rotates about the top of the neck at every crouch/look angle")
			assert_almost_eq(torso.to_local(neck.global_position).z, 0.015, 0.001, "neck stays anchored inside the collar")
			assert_true(_puppet._face.global_transform.is_equal_approx(_owner._face.global_transform), "owner and puppet head transforms agree")
			for joint in _owner._joints:
				assert_true((_owner._joints[joint] as Node3D).global_transform.is_equal_approx((_puppet._joints[joint] as Node3D).global_transform), "%s agrees after pose synchronization" % joint)
	_receive_pose(0, Surgeon.LOOK_PITCH.x)
	await _capture("looking_down_back", Vector3(1.25, 1.7, 1.6), Vector3(0, 1.46, 0))
	_receive_pose(0, Surgeon.LOOK_PITCH.y)
	await _capture("looking_up_side", Vector3(1.6, 1.7, -0.3), Vector3(0, 1.48, 0))
	# The new knee/ankle rig also has to walk without stretching a leg or lifting both feet off the floor.
	budget.clear()
	for step in 60:
		_receive_pose(0, 0, Surgeon.WALK_SPEED)
		_check_leg_geometry("walking %d" % step, true)
		budget.sample("walking with articulated legs")
		await get_tree().process_frame
		if step == 8:
			await _capture("walking_stride", Vector3(1.5, 1.15, -1.6), Vector3(0, 0.8, 0))
			budget.resume()
	gut.p(budget.summary())
	if FrameBudget.enforced():
		assert_true(budget.within(), budget.summary())


func _check_neck_inside_collar() -> void:
	var torso := _puppet._joints.Torso as MeshInstance3D
	var neck := _puppet._body.find_child("Neck", true, false) as MeshInstance3D
	var shell := torso.mesh.get_faces()
	var vertices: PackedVector3Array = neck.mesh.surface_get_arrays(0)[Mesh.ARRAY_VERTEX]
	var exposed := 0
	for vertex in vertices:
		var at := torso.to_local(neck.to_global(vertex))
		if at.y > 1.48 - Surgeon.HIP_HEIGHT:
			continue
		var axis := Vector3(0, at.y, 0)
		var out := (at - axis).normalized()
		var covered := false
		for t in range(0, shell.size(), 3):
			var hit: Variant = Geometry3D.ray_intersects_triangle(axis, out, shell[t], shell[t + 1], shell[t + 2])
			if hit != null and axis.distance_to(hit) >= axis.distance_to(at) - 0.001:
				covered = true
				break
		if not covered:
			exposed += 1
	assert_eq(exposed, 0, "every neck vertex below the collar is inside the actual torso mesh")


func _check_leg_geometry(what: String, walking: bool = false) -> void:
	var lowest := INF
	for suffix in ["L", "R"]:
		var thigh := _puppet._joints["Leg" + suffix] as Node3D
		var shin := _puppet._joints["Shin" + suffix] as Node3D
		var shoe := _puppet._joints["Shoe" + suffix] as Node3D
		assert_almost_eq(thigh.global_position.distance_to(shin.global_position), Surgeon.THIGH_LENGTH, 0.0001, "%s: thigh length" % what)
		assert_almost_eq(shin.global_position.distance_to(shoe.global_position), Surgeon.SHIN_LENGTH, 0.0001, "%s: shin length" % what)
		assert_almost_eq(shoe.global_basis.y.dot(Vector3.UP), 1.0, 0.0001, "%s: sole stays horizontal" % what)
		var sole := _puppet._body.find_child("Sole" + suffix, true, false) as MeshInstance3D
		var bottom := INF
		for vertex in sole.mesh.get_faces():
			bottom = minf(bottom, (sole.global_transform * vertex).y)
		assert_between(bottom, -0.001, 0.065 if walking else 0.005, "%s: sole clears the floor" % what)
		lowest = minf(lowest, bottom)
	assert_lt(lowest, 0.005, "%s: at least one foot stays planted" % what)


func _check_sleeves_clear_knees() -> void:
	for hand in _puppet.hands:
		var elbow := hand._upper.global_transform * Vector3(0, 0.5, 0)
		var shoulder := _puppet.shoulder(hand.index)
		assert_almost_eq(shoulder.distance_to(elbow), SurgeonHand.UPPER_ARM, 0.001, "supported elbow preserves upper arm length")
		for joint in ["ShinL", "ShinR"]:
			var knee := (_puppet._joints[joint] as Node3D).global_position
			for segment: Array in [[hand._upper, 0.064], [hand._fore, 0.05]]:
				var sleeve := segment[0] as Node3D
				var start := sleeve.global_transform * Vector3(0, -0.5, 0)
				var end := sleeve.global_transform * Vector3(0, 0.5, 0)
				var nearest := Geometry3D.get_closest_point_to_segment(knee, start, end)
				assert_gte(nearest.distance_to(knee), 0.065 + float(segment[1]), "actual sleeve segment and knee volumes clear each other for the controlled hand pose")


func test_ceiling_panel_has_clearance_in_both_indoor_rooms() -> void:
	for env in ["or", "ambulance"]:
		var room := _room
		if env == "ambulance":
			room = Room.new()
			add_child_autofree(room)
			room.environment_id = env
			room.position.x = 10
			room.layout = Room.LAYOUTS[env]
			room._build_environment()
			room._build_shell()
		var ceiling := room.get_node("Ceiling") as MeshInstance3D
		var panel := room.get_node("CeilingPanel") as MeshInstance3D
		var underside := ceiling.position.y + ceiling.mesh.get_aabb().position.y
		var panel_top := panel.position.y + panel.mesh.get_aabb().end.y
		assert_gt(underside - panel_top, 0.01, "%s: entire light fixture clears the ceiling, eliminating coincident faces" % env)
	await _capture("ceiling_oblique", Vector3(1.8, 1.7, 1.3), Vector3(0, 2.75, 0))
	await _capture("ceiling_reverse", Vector3(-1.8, 1.7, -1.3), Vector3(0, 2.75, 0))


func _capture(label: String, from: Vector3, at: Vector3) -> void:
	if not KeyFrames.wanted():
		return
	var folder := ProjectSettings.globalize_path("res://build/key-frames/surgeon_pose")
	DirAccess.make_dir_recursive_absolute(folder)
	_camera.position = from
	_camera.look_at(at)
	var overlay := get_tree().root.get_node_or_null("GutRunner/GutLayer/GutScene") as CanvasItem
	if overlay:
		overlay.visible = false
	RenderingServer.render_loop_enabled = true
	for i in 3:
		await get_tree().process_frame
	var path := folder.path_join("%02d_%s.png" % [_shot, label])
	_shot += 1
	assert_eq(get_viewport().get_texture().get_image().save_png(path), OK, "saved " + path)
	RenderingServer.render_loop_enabled = false
	if overlay:
		overlay.visible = true
