extends Node
## Two-process network test. Start one with --role=host and one with --role=client.
## The driver lives under the tree root so it survives scene changes (lobby -> surgery).
## Process driver for tests/network/test_multiplayer.gd.

const PORT := 24599
const Slicing := preload("res://tests/support/slicing_suite.gd")
const SurgeryState := preload("res://tests/support/surgery_state.gd")
const SQUAT_TIMEOUT := 15.0
var _squat_seen := false


@rpc("authority", "call_remote", "reliable")
func _acknowledge_squat() -> void:
	_squat_seen = true


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
	# This checks synchronization, not missing starter tools or random surgeon handicaps.
	SurgeryState.scenario_has_all_starter_tools("appendectomy")
	if role == "host":
		Net.host("appendectomy", PORT)
		while Net.roster.size() < 2:
			await tree.create_timer(0.2).timeout
		Net.set_ready(true)
		while not Net.all_ready():
			await tree.create_timer(0.2).timeout
		SurgeryState.network_session_has_ordinary_surgeons()
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
		# Deliberately finish the client's startup after the host's former 1.2-second window. The acknowledged pose
		# must still be checked: this exercises the loading-delay race deterministically on every network run.
		await tree.create_timer(2.0).timeout
		# A real input crouch must reach the host as a grounded, articulated squat before continuing surgery.
		Input.action_press("crouch")
		# Keep the pose until the host has actually checked it. Client/host scene loading and sync can take longer
		# than the old fixed one-second hold; a reliable acknowledgement prevents missing a brief crouch window.
		var deadline := Time.get_ticks_msec() + int(SQUAT_TIMEOUT * 1000)
		while not _squat_seen and Time.get_ticks_msec() < deadline:
			await tree.create_timer(0.05).timeout
		Input.action_release("crouch")
		if not _squat_seen:
			print("FAIL: [client] host never acknowledged the grounded squat")
			tree.quit(1)
			return
		await tree.create_timer(0.4).timeout
		var free: Array = []
		var cutters: Array = []
		# Like a player, wait a moment for a blade on the tray: it may still be filling in. Not long: the host only waits
		# so long for the handoff before it ends the session. With all starter tools present, use the scalpel specifically
		# so this replication/cut/handoff test cannot silently switch to the switchblade's different cutting behavior.
		for i in 20:
			free = surgery.tools.tools.values().filter(func(t: SurgicalTool) -> bool: return t.state == SurgicalTool.State.FREE)
			cutters = free.filter(func(t: SurgicalTool) -> bool: return t.def.id == "scalpel")
			if not cutters.is_empty():
				break
			await tree.create_timer(0.1).timeout
		if cutters.is_empty():
			print("FAIL: [client] starter scalpel never arrived")
			tree.quit(1)
			return
		surgery.tools.request_grab(cutters[0], 1)
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
		var partner: Surgeon = surgery.surgeons.values().filter(func(s: Surgeon) -> bool: return not s.is_local)[0]
		var deadline := Time.get_ticks_msec() + int(SQUAT_TIMEOUT * 1000)
		while partner.crouch <= 0.99 and Time.get_ticks_msec() < deadline:
			await tree.create_timer(0.02).timeout
		await tree.physics_frame
		var hip := (partner._joints.LegL as Node3D).global_position
		var knee := (partner._joints.ShinL as Node3D).global_position
		var shoe := partner._joints.ShoeL as Node3D
		if partner.crouch < 0.99 or hip.y >= knee.y or absf(shoe.global_position.y - Surgeon.ANKLE_HEIGHT) > 0.002 or shoe.global_basis.y.dot(Vector3.UP) < 0.999:
			print("FAIL: [host] client squat lost its bent knees or grounded heels")
		else:
			print("[host] client squat has bent knees and grounded heels")
		_acknowledge_squat.rpc_id(partner.peer_id)
		host_me.hands[0].local_target = host_me.to_local(Vector3(0.0, 1.3, 0.06))
		# Until the client has cut and handed its tool across: on a slow machine that takes a while.
		for i in 75:
			if host_me.held_tool(0):
				break
			await tree.create_timer(0.2).timeout
		await tree.create_timer(1.0).timeout
		var got := host_me.held_tool(0)
		print("[host] partner handed me: ", got.def.id if got else "nothing")
	# Sample the persistent network state only after the cut mesh has had time to settle on both peers. Without this,
	# a freshly imported project can catch the same transient raised seam on both host and client.
	await tree.create_timer(2.0).timeout
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
