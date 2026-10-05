extends GutTest
## Ceiling fixture geometry and two oblique views in each indoor environment, including the shorter ambulance.

const TAGS = ["smoke", "visual_confirmation"]
const GODOT_ARGS = ["--fixed-fps", "60"]
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const KeyFrames := preload("res://tests/support/key_frames.gd")


func test_ceiling_panel_clears_the_slab_in_each_indoor_room() -> void:
	RenderingServer.render_loop_enabled = false
	for environment in ["or", "ambulance"]:
		var room := Room.new()
		add_child(room)
		SurgeryState.room_has_shell(room, environment)
		var ceiling := room.get_node("Ceiling") as MeshInstance3D
		var panel := room.get_node("CeilingPanel") as MeshInstance3D
		var underside := ceiling.position.y + ceiling.mesh.get_aabb().position.y
		var panel_top := panel.position.y + panel.mesh.get_aabb().end.y
		assert_gt(underside - panel_top, 0.01, "%s: entire panel clears the slab, eliminating coincident faces" % environment)
		if KeyFrames.wanted():
			var camera := Camera3D.new()
			camera.fov = 55
			room.add_child(camera)
			camera.make_current()
			var height := float(room.layout.size.y)
			await _capture(camera, environment + "_ceiling_oblique", Vector3(1.6, height - 1.1, 0.9), Vector3(0, height - 0.05, 0))
			await _capture(camera, environment + "_ceiling_reverse", Vector3(-1.6, height - 1.1, -0.9), Vector3(0, height - 0.05, 0))
		room.queue_free()
		await get_tree().process_frame
	RenderingServer.render_loop_enabled = true


func _capture(camera: Camera3D, name: String, from: Vector3, at: Vector3) -> void:
	var folder := ProjectSettings.globalize_path("res://build/key-frames/room")
	DirAccess.make_dir_recursive_absolute(folder)
	camera.position = from
	camera.look_at(at)
	var overlay := get_tree().root.get_node_or_null("GutRunner/GutLayer/GutScene") as CanvasItem
	if overlay:
		overlay.visible = false
	RenderingServer.render_loop_enabled = true
	for i in 3:
		await get_tree().process_frame
	var path := folder.path_join(name + ".png")
	assert_eq(get_viewport().get_texture().get_image().save_png(path), OK, "saved " + path)
	RenderingServer.render_loop_enabled = false
	if overlay:
		overlay.visible = true
