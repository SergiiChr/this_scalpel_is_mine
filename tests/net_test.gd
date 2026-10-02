extends Node
## Two-process network test. Start one with --role=host and one with --role=client.
## The driver lives under the tree root so it survives scene changes (lobby -> surgery).
## Run: godot --headless --path . res://tests/net_test.tscn -- --role=host
##      godot --headless --path . res://tests/net_test.tscn -- --role=client

const PORT := 24599
const Slicing := preload("res://tests/slicing_test.gd")


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
		var free: Array = []
		var cutters: Array = []
		# Like a player, wait until a blade lies free on the tray: the tray may still be filling in.
		for i in 150:
			free = surgery.tools.tools.values().filter(func(t: SurgicalTool) -> bool: return t.state == SurgicalTool.State.FREE)
			cutters = free.filter(func(t: SurgicalTool) -> bool: return t.def.action == "cut")
			if not cutters.is_empty():
				break
			await tree.create_timer(0.1).timeout
		surgery.tools.request_grab(cutters[0] if not cutters.is_empty() else free[0], 1)
		await tree.create_timer(0.5).timeout
		print("[client] holding: ", me.held_tool(1).def.id if me.held_tool(1) else "nothing")
		var site := surgery.patient.body.site.global_position
		var hand := me.hands[1]
		# The hand sits behind the tip: start it short of the site so the blade lands in the middle.
		hand.local_target = me.to_local(site + Vector3(0.06, 0.12, -0.08))
		hand.level = 3
		hand.lowered = true
		# Rotated a quarter turn, the blade's edge runs sideways: along the cut.
		hand.twist = PI / 2
		for i in 60:
			hand.local_target += Vector3(0.002, 0, 0.0)
			await tree.physics_frame
		hand.lowered = false
		hand.twist = 0.0
		await tree.create_timer(1.0).timeout
		# Hand the tool across the table: both surgeons reach over the patient.
		# 12 cm apart: close enough to pass (Surgeon.PASS_DISTANCE), not so close the hands bump and drop it.
		hand.level = 0
		hand.local_target = me.to_local(Vector3(0.0, 1.3, -0.06))
		await tree.create_timer(1.5).timeout
		me.active = 1
		me.call("_grab_or_release")
		await tree.create_timer(1.0).timeout
		print("[client] after handoff, holding: ", me.held_tool(1).def.id if me.held_tool(1) else "nothing")
	else:
		var host_me := surgery.local_surgeon
		host_me.hands[0].local_target = host_me.to_local(Vector3(0.0, 1.3, 0.06))
		# Until the client has cut and handed its tool across: on a slow machine that takes a while.
		for i in 75:
			if host_me.held_tool(0):
				break
			await tree.create_timer(0.2).timeout
		await tree.create_timer(1.0).timeout
		var got := host_me.held_tool(0)
		print("[host] partner handed me: ", got.def.id if got else "nothing")
	var surgeon_wounds := surgery.patient.wounds.filter(func(w: Wound) -> bool: return w.made_by_surgeon).size()
	var painted := 0
	var image := surgery.patient.body.wound_map.images[0]
	for y in range(0, image.get_height(), 4):
		for x in range(0, image.get_width(), 4):
			painted += 1 if image.get_pixel(x, y).r > 0.1 else 0
	var body := surgery.patient.body
	var tissue := body.tissue
	print("[%s] surgeon wounds (host state)=%d, painted texels=%d, severed springs=%d, topology=%d, site shape=%d, vitals hr=%d" % [role, surgeon_wounds, painted, tissue.c_active.count(0), tissue.topology_hash(), body.shape_hash(), surgery.patient.vitals.heart_rate])
	# Each player's simulated skin meets the body model where the cut opened it, without a step.
	var seam := Slicing.seam(body)
	if seam >= Slicing.SEAM_MAX:
		print("FAIL: [%s] the simulated skin doesn't meet the body model: %.2f mm off at its edge" % [role, seam * 1000.0])
	await tree.create_timer(1.0).timeout
	tree.quit()
