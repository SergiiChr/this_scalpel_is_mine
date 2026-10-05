extends Node3D
## Renders every tool model held in a right hand, from both sides and from the holder's eyes, for checking grips.
## Needs a real renderer:
##   xvfb-run godot --path . --rendering-method gl_compatibility res://tests/support/grip_gallery.tscn -- --out=/tmp/grips [--left]
##   [--only=scalpel,needle] renders just those models.

const EYE := Vector3(0.0, 1.62, 0.0)
const HAND_AT := Vector3(0.17, 1.05, -0.42)


func _ready() -> void:
	var out := "user://grips"
	var left := false
	var only := PackedStringArray()
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--out="):
			out = arg.get_slice("=", 1)
		elif arg.begins_with("--only="):
			only = arg.get_slice("=", 1).split(",")
		left = left or arg == "--left"
	DirAccess.make_dir_recursive_absolute(out)
	_light()
	var body := Node3D.new()
	add_child(body)
	var hand := SurgeonHand.new()
	body.add_child(hand)
	hand.build(0 if left else 1, Materials.toon_unique(Color(0.2, 0.36, 0.34)))
	var camera := Camera3D.new()
	camera.fov = 40.0
	add_child(camera)
	camera.current = true
	var seen: Dictionary = {}
	for def: ToolDef in Db.tools.values():
		var model_id := def.model if def.model else def.id
		if seen.has(model_id) or not only.is_empty() and not model_id in only:
			continue
		seen[model_id] = true
		var holder := Node3D.new()
		add_child(holder)
		ToolModel.build(def, holder)
		var animator := ToolAnimator.new()
		animator.setup(holder, def.action)
		animator.animate(false, model_id == "needle", 0.0)
		hand.holding = true
		hand.grip = def.grip
		hand.fit = Db.grip_fit(def, 0 if left else 1)
		hand.tilt = hand.default_tilt()
		hand.turn = hand.default_turn()
		var at := Vector3(-HAND_AT.x if left else HAND_AT.x, HAND_AT.y, HAND_AT.z)
		hand.target = at
		var shoulder := Vector3(-0.19 if left else 0.19, 1.4, -0.08)
		# A syringe half full, its scale turned to the eyes, the way the game holds it.
		hand.press = Surgeon.SYRINGE_PRESS + def.length * Surgeon.SYRINGE_TRAVEL * 0.5 if def.action == "syringe" else NAN
		hand.tilt = Surgeon.SYRINGE_TILT if def.action == "syringe" else SurgeonHand.REST_TILT
		hand.turn = Surgeon.SYRINGE_TURN * (-1.0 if left else 1.0) if def.action == "syringe" else 0.0
		for i in 30:
			if def.action == "syringe":
				hand.twist = hand.twist_facing(EYE - hand.global_position)
			hand.update_pose(shoulder, 1.0 / 30.0)
			holder.global_transform = hand.grip_transform()
			await get_tree().process_frame
		# Aim between the grip and the tip, so both the hand and the working end are in view.
		var middle := hand.global_position + hand.tip_offset(def.length * 0.5)
		var out_side := -1.0 if left else 1.0
		for view: Array in [["side", middle + Vector3(0.45 * out_side, 0.12, 0.2), 38.0], ["thumb", middle + Vector3(-0.45 * out_side, 0.12, 0.2), 38.0], ["eyes", EYE, 20.0]]:
			camera.global_position = view[1]
			camera.fov = view[2]
			camera.look_at(middle)
			await RenderingServer.frame_post_draw
			await RenderingServer.frame_post_draw
			get_viewport().get_texture().get_image().save_png("%s/%s_%s.png" % [out, model_id, view[0]])
		holder.queue_free()
		hand.twist = 0.0
	print("grip_gallery: done")
	get_tree().quit()


func _light() -> void:
	var env := WorldEnvironment.new()
	env.environment = Environment.new()
	env.environment.background_mode = Environment.BG_COLOR
	env.environment.background_color = Color(0.16, 0.17, 0.18)
	env.environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.environment.ambient_light_color = Color(0.5, 0.5, 0.5)
	add_child(env)
	var sun := DirectionalLight3D.new()
	sun.rotation = Vector3(-0.9, 0.5, 0.0)
	add_child(sun)
