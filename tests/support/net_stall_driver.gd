extends Node
## Spotty connection driver: the GUT network test freezes the client for 10 seconds, then resumes it.
## Both sides must still be in the same surgery afterwards, and the host must have paused the silent player's input.
## Run through tests/network/test_multiplayer.gd.

const PORT := 24598


func _ready() -> void:
	if get_parent() != get_tree().root or name != "StallDriver":
		var driver := Node.new()
		driver.name = "StallDriver"
		driver.set_script(get_script())
		get_tree().root.add_child.call_deferred(driver)
		return
	var role := "host"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--role="):
			role = arg.get_slice("=", 1)
	_drive(role)


func _drive(role: String) -> void:
	var tree := get_tree()
	if role == "host":
		Net.host("hand_stitch", PORT)
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
	if role == "client":
		# Holding the tool down when the freeze hits: the host must not keep using it.
		surgery.local_surgeon.hands[1].lowered = true
		await tree.create_timer(1.0).timeout
		print("[client] running")
		# The shell freezes this process now. A long gap between two short waits means it happened.
		var last := Time.get_ticks_msec()
		var frozen := 0.0
		for i in 240:
			await tree.create_timer(0.25).timeout
			var now := Time.get_ticks_msec()
			frozen = maxf(frozen, (now - last) * 0.001 - 0.25)
			last = now
			if frozen > 5.0:
				break
		await tree.create_timer(3.0).timeout
		# Still hearing the host means the host kept us; a dropped client only notices much later.
		var connected := Surgery.current == surgery and Net.is_online() and Net.silence(1) < 1.0
		print("[client] frozen for %.0f s, still in surgery: %s" % [frozen, connected])
	else:
		var partner: Surgeon = surgery.surgeons.values().filter(func(s: Surgeon) -> bool: return not s.is_local)[0]
		var longest := 0.0
		var paused := false
		var engaged_before := false
		for i in 240:
			await tree.create_timer(0.25).timeout
			if not is_instance_valid(partner):
				break
			var silent := Net.silence(partner.peer_id)
			engaged_before = engaged_before or partner.hands[1].lowered and silent < 0.5
			longest = maxf(longest, silent)
			paused = paused or partner.is_stalled()
			if longest > 5.0 and silent < 0.5:
				break
		await tree.create_timer(2.0).timeout
		var still_here := is_instance_valid(partner) and surgery.surgeons.has(partner.peer_id) and Net.roster.size() == 2
		print("[host] partner silent for %.0f s, input paused: %s, partner still here: %s" % [longest, paused and engaged_before, still_here])
		# Let the client report before the host goes away.
		await tree.create_timer(3.0).timeout
	tree.quit()
