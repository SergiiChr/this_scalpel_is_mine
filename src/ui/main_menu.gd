extends Control
## Title screen. Left: sections. Right: the selected section's panel.

var _content: MarginContainer
var _selected_scenario: ScenarioDef


func _ready() -> void:
	theme = Ui.theme()
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	var bg := ColorRect.new()
	bg.color = Color(0.03, 0.04, 0.04)
	bg.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(bg)
	var layout := Ui.hbox(32)
	layout.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT, Control.PRESET_MODE_MINSIZE, 48)
	add_child(layout)

	var nav := Ui.vbox(12)
	nav.custom_minimum_size.x = 340
	nav.add_child(Ui.label("THIS SCALPEL\nIS MINE", 52, Ui.PIP))
	nav.add_child(Ui.label("a co-op surgery thriller", 18, Ui.DIM))
	nav.add_child(Control.new())
	nav.add_child(Ui.button("Scenarios", _show_scenarios))
	nav.add_child(Ui.button("Multiplayer", _show_multiplayer))
	nav.add_child(Ui.button("Quirk codex", _show_codex))
	nav.add_child(Ui.button("Settings", _show_settings))
	nav.add_child(Ui.button("Quit", get_tree().quit))
	layout.add_child(nav)

	_content = MarginContainer.new()
	_content.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	layout.add_child(_content)
	_selected_scenario = Db.scenarios[0] if not Db.scenarios.is_empty() else null
	_show_scenarios()


func _set_content(panel: Control) -> void:
	for child in _content.get_children():
		child.queue_free()
	_content.add_child(Ui.panel(panel))


# --- Scenarios -------------------------------------------------------------------------------------


func _show_scenarios() -> void:
	var split := Ui.hbox(24)
	var list := Ui.vbox(4)
	var details := Ui.vbox(12)
	details.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	for scenario in Db.scenarios:
		var best: int = Progress.best_stars.get(scenario.id, 0)
		var title := "???" if scenario.hidden and best == 0 else scenario.title
		var result := "   best " + "★".repeat(best) if best > 0 else ""
		var b := Ui.button("%02d  %s   %s  %s%s" % [scenario.order, title, scenario.stars_text(), scenario.group, result], func() -> void:
			_selected_scenario = scenario
			_fill_details(details))
		b.alignment = HORIZONTAL_ALIGNMENT_LEFT
		list.add_child(b)
	var scroll := Ui.scroll(list)
	scroll.custom_minimum_size.x = 460
	split.add_child(scroll)
	split.add_child(details)
	_fill_details(details)
	_set_content(split)


func _fill_details(details: VBoxContainer) -> void:
	for child in details.get_children():
		child.queue_free()
	var s := _selected_scenario
	if s == null:
		return
	var secret: bool = s.hidden and Progress.best_stars.get(s.id, 0) == 0
	details.add_child(Ui.label("???" if secret else s.title, 34, Ui.PIP))
	details.add_child(Ui.label("Difficulty %s    Group: %s" % [s.stars_text(), s.group], 18, Ui.DIM))
	details.add_child(Ui.label("Description hidden. Trust us." if secret else s.description, 20, Ui.INK, true))
	var facts := "Anesthesia: %s   Time: %s   Nurse: %s" % [s.anesthesia, "%d min" % (s.time_limit / 60) if s.time_limit > 0 else "none", "yes" if s.nurse else "no"]
	details.add_child(Ui.label(facts, 16, Ui.DIM))
	var similar := Db.scenarios.filter(func(o: ScenarioDef) -> bool: return o != s and o.group.get_slice(" ", 0) == s.group.get_slice(" ", 0) and not o.hidden)
	if not similar.is_empty():
		details.add_child(Ui.label("Similar: " + ", ".join(similar.map(func(o: ScenarioDef) -> String: return o.title)), 16, Ui.DIM, true))
	var buttons := Ui.hbox(12)
	buttons.add_child(Ui.button("Play solo", func() -> void: Net.play_solo(s.id)))
	buttons.add_child(Ui.button("Host co-op", func() -> void: _host(s.id, Net.DEFAULT_PORT)))
	details.add_child(buttons)


# --- Multiplayer -----------------------------------------------------------------------------------


func _show_multiplayer() -> void:
	var box := Ui.vbox(12)
	box.add_child(Ui.label("MULTIPLAYER", 28, Ui.PIP))
	var name_row := Ui.hbox(8)
	name_row.add_child(Ui.label("Your name", 18))
	var name_field := LineEdit.new()
	name_field.text = Progress.player_name
	name_field.custom_minimum_size.x = 280
	name_field.text_changed.connect(func(t: String) -> void:
		Progress.player_name = t
		Progress.save())
	name_row.add_child(name_field)
	box.add_child(name_row)

	box.add_child(Ui.label("Host (uses the scenario picked under Scenarios: %s)" % (_selected_scenario.title if _selected_scenario else "-"), 20, Ui.PIP))
	var host_row := Ui.hbox(8)
	var host_port := _port_field()
	host_row.add_child(Ui.label("Port", 18))
	host_row.add_child(host_port)
	host_row.add_child(Ui.button("Host", func() -> void: _host(_selected_scenario.id, int(host_port.value))))
	box.add_child(host_row)
	box.add_child(Ui.label("Your partner joins your public IP (forward the port) or LAN IP.", 16, Ui.DIM, true))

	box.add_child(Ui.label("Join", 20, Ui.PIP))
	var join_row := Ui.hbox(8)
	var ip := LineEdit.new()
	ip.placeholder_text = "IP address"
	ip.custom_minimum_size.x = 260
	var join_port := _port_field()
	join_row.add_child(ip)
	join_row.add_child(join_port)
	join_row.add_child(Ui.button("Join", func() -> void: _join(ip.text.strip_edges(), int(join_port.value))))
	join_row.add_child(Ui.button("Save host", func() -> void:
		Progress.remember_host(ip.text.strip_edges(), ip.text.strip_edges(), int(join_port.value))
		_show_multiplayer()))
	box.add_child(join_row)

	box.add_child(Ui.label("Known hosts", 20, Ui.PIP))
	for host: Dictionary in Progress.known_hosts:
		var row := Ui.hbox(8)
		row.add_child(Ui.button("%s:%d" % [host.ip, host.port], func() -> void: _join(host.ip, host.port)))
		row.add_child(Ui.button("Forget", func() -> void:
			Progress.forget_host(host.ip, host.port)
			_show_multiplayer()))
		box.add_child(row)
	var status := Ui.label("", 18, Ui.ALERT)
	status.name = "Status"
	box.add_child(status)
	if not Net.connection_failed.is_connected(_on_connection_failed):
		Net.connection_failed.connect(_on_connection_failed)
	_set_content(box)


func _port_field() -> SpinBox:
	var port := SpinBox.new()
	port.min_value = 1024
	port.max_value = 65535
	port.value = Net.DEFAULT_PORT
	return port


func _host(scenario_id: String, port: int) -> void:
	var err := Net.host(scenario_id, port)
	if err != OK:
		_on_connection_failed("Could not host on port %d (%s)." % [port, error_string(err)])


func _join(ip: String, port: int) -> void:
	if ip.is_empty():
		return
	var err := Net.join(ip, port)
	if err != OK:
		_on_connection_failed("Could not connect (%s)." % error_string(err))


func _on_connection_failed(reason: String) -> void:
	var status := find_child("Status", true, false) as Label
	if status:
		status.text = reason


# --- Codex and settings ----------------------------------------------------------------------------


func _show_codex() -> void:
	var box := Ui.vbox(10)
	box.add_child(Ui.label("QUIRK CODEX", 28, Ui.PIP))
	box.add_child(Ui.label("Quirks unlock the first time they happen to you.", 16, Ui.DIM))
	var tabs := TabContainer.new()
	tabs.size_flags_vertical = Control.SIZE_EXPAND_FILL
	for kind: QuirkDef.Kind in [QuirkDef.Kind.SURGEON, QuirkDef.Kind.PATIENT]:
		var list := Ui.vbox(14)
		var table: Dictionary = Db.surgeon_quirks if kind == QuirkDef.Kind.SURGEON else Db.patient_quirks
		for quirk: QuirkDef in table.values():
			if Progress.is_unlocked(quirk):
				for variant in (quirk.variants if not quirk.variants.is_empty() else PackedStringArray([""])):
					list.add_child(Ui.quirk_line(quirk, variant, true))
			else:
				list.add_child(Ui.label("???  (locked)", 18, Ui.DIM))
		var scroll := Ui.scroll(list)
		scroll.name = "Surgeon quirks" if kind == QuirkDef.Kind.SURGEON else "Patient quirks"
		tabs.add_child(scroll)
	box.add_child(tabs)
	_set_content(box)


func _show_settings() -> void:
	_set_content(SettingsPanel.new())
