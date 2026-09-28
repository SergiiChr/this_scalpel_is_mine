extends Control
## Pre-op briefing. Shows the scenario and everyone's rolled quirks. Everyone readies up, the host starts.

var _roster_box: VBoxContainer
var _header: VBoxContainer
var _ready_button: Button
var _start_button: Button
var _scenario_picker: OptionButton


func _ready() -> void:
	theme = Ui.theme()
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	var bg := ColorRect.new()
	bg.color = Color(0.03, 0.04, 0.04)
	bg.set_anchors_preset(Control.PRESET_FULL_RECT)
	add_child(bg)
	var box := Ui.vbox(16)
	box.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT, Control.PRESET_MODE_MINSIZE, 48)
	add_child(box)
	_header = Ui.vbox(6)
	box.add_child(_header)
	if Net.is_host():
		var row := Ui.hbox(8)
		row.add_child(Ui.label("Scenario", 18))
		_scenario_picker = OptionButton.new()
		for s in Db.scenarios:
			_scenario_picker.add_item("%02d  %s" % [s.order, s.title if not s.hidden else "???"])
		_scenario_picker.item_selected.connect(func(i: int) -> void: Net.change_scenario(Db.scenarios[i].id))
		row.add_child(_scenario_picker)
		box.add_child(row)
	_roster_box = Ui.vbox(20)
	box.add_child(Ui.scroll(_roster_box))
	var buttons := Ui.hbox(12)
	_ready_button = Ui.button("Ready", _toggle_ready)
	buttons.add_child(_ready_button)
	if Net.is_host():
		_start_button = Ui.button("Start surgery", Net.start_session)
		buttons.add_child(_start_button)
		if Net.is_online():
			buttons.add_child(Ui.label("Hosting on port %d. Waiting for your partner is optional." % Net.DEFAULT_PORT, 16, Ui.DIM))
	buttons.add_child(Ui.button("Leave", Net.back_to_menu))
	box.add_child(buttons)
	Net.roster_changed.connect(_refresh)
	Net.scenario_changed.connect(_refresh)
	_refresh()


func _refresh() -> void:
	var scenario := Net.scenario()
	for child in _header.get_children():
		child.queue_free()
	if scenario:
		_header.add_child(Ui.label("PRE-OP BRIEFING: %s" % (scenario.title if not scenario.hidden else "???"), 32, Ui.PIP))
		_header.add_child(Ui.label("%s   %s" % [scenario.stars_text(), scenario.description if not scenario.hidden else "One injection. Then you listen."], 18, Ui.INK, true))
		if _scenario_picker:
			_scenario_picker.select(Db.scenarios.find(scenario))
	for child in _roster_box.get_children():
		child.queue_free()
	var peers := Net.roster.keys()
	peers.sort()
	for peer: int in peers:
		var info: Dictionary = Net.roster[peer]
		var mine := peer == Net.local_id()
		var card := Ui.vbox(8)
		card.add_child(Ui.label("%s%s   %s" % [info.name, " (you)" if mine else "", "READY" if info.ready else "not ready"], 24, Ui.GOOD if info.ready else Ui.INK))
		for roll: Dictionary in info.quirks:
			card.add_child(Ui.quirk_line(Db.quirk(QuirkDef.Kind.SURGEON, roll.id), roll.variant, mine))
		_roster_box.add_child(Ui.panel(card))
	var me: Dictionary = Net.roster.get(Net.local_id(), {})
	_ready_button.text = "Not ready" if me.get("ready", false) else "Ready"
	if _start_button:
		_start_button.disabled = not Net.all_ready()


func _toggle_ready() -> void:
	var me: Dictionary = Net.roster.get(Net.local_id(), {})
	Net.set_ready(not me.get("ready", false))
