extends Node
## Two-process network test. Start one with --role=host and one with --role=client.
## The driver lives under the tree root so it survives scene changes (lobby -> surgery).
## Run: godot --headless --path . res://tests/net_test.tscn -- --role=host
##      godot --headless --path . res://tests/net_test.tscn -- --role=client

const PORT := 24599


func _ready() -> void:
	if get_parent() != get_tree().root or name != "NetDriver":
		var driver := Node.new()
		driver.name = "NetDriver"
		driver.set_script(get_script())
		get_tree().root.add_child.call_deferred(driver)
		return
	var role := "host"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--role="):
			role = arg.get_slice("=", 1)
	_drive(role, self)


func _drive(role: String, driver: Node) -> void:
	var tree := driver.get_tree()
	if role == "host":
		Net.host("appendectomy", PORT)
		while Net.roster.size() < 2:
			await tree.create_timer(0.2).timeout
		Net.set_ready(true)
		while not Net.all_ready():
			await tree.create_timer(0.2).timeout
		Net.start_session()
	else:
		await tree.create_timer(0.5).timeout
		Net.join("127.0.0.1", PORT)
		while Net.roster.size() < 2:
			await tree.create_timer(0.2).timeout
		Net.set_ready(true)
	while Surgery.current == null or not Surgery.current.running:
		await tree.create_timer(0.2).timeout
	var surgery := Surgery.current
	print("[%s] surgery running, surgeons=%d tools=%d" % [role, surgery.surgeons.size(), surgery.tools.tools.size()])
	if role == "client":
		var me := surgery.local_surgeon
		var free: Array = surgery.tools.tools.values().filter(func(t: SurgicalTool) -> bool: return t.state == SurgicalTool.State.FREE)
		var cutters := free.filter(func(t: SurgicalTool) -> bool: return t.def.action == "cut")
		surgery.tools.request_grab(cutters[0] if not cutters.is_empty() else free[0], 1)
		await tree.create_timer(0.5).timeout
		print("[client] holding: ", me.held_tool(1).def.id if me.held_tool(1) else "nothing")
		var site := surgery.patient.body.site.global_position
		var hand := me.hands[1]
		hand.local_target = me.to_local(site + Vector3(0, 0.12, 0))
		hand.pressure = 3
		hand.engaged = true
		for i in 60:
			hand.local_target += Vector3(0.002, 0, 0.0)
			await tree.physics_frame
		hand.engaged = false
		await tree.create_timer(1.0).timeout
	else:
		await tree.create_timer(4.0).timeout
	var surgeon_wounds := surgery.patient.wounds.filter(func(w: Wound) -> bool: return w.made_by_surgeon).size()
	var painted := 0
	var image := surgery.patient.body.wound_map.images[0]
	for y in range(0, WoundMap.SIZE, 4):
		for x in range(0, WoundMap.SIZE, 4):
			painted += 1 if image.get_pixel(x, y).r > 0.1 else 0
	print("[%s] surgeon wounds (host state)=%d, painted texels=%d, vitals hr=%d" % [role, surgeon_wounds, painted, surgery.patient.vitals.heart_rate])
	await tree.create_timer(1.0).timeout
	tree.quit()
