class_name Hud
extends CanvasLayer
## Everything drawn on screen during surgery, plus the full screen overlays (manual, card, menus, report).
## While an overlay is open the surgeon's input is locked and the mouse is free.

const POST_FX := preload("res://assets/shaders/post_grime.gdshader")
const TOAST_TIME := 5.0
const SUBTITLE_TIME := 6.0

var surgery: Surgery
var _vitals: Label
var _clock: Label
var _objectives: VBoxContainer
var _hands: Label
var _belt: HBoxContainer
var _prompt: Label
var _net_warning: Label
var _toasts: VBoxContainer
var _subtitle: Label
var _subtitle_timer := 0.0
var _gauges: Dictionary = {}
var _overlay: Control
var _post: ShaderMaterial
var _root: Control


func setup(owner_surgery: Surgery) -> void:
	surgery = owner_surgery
	_build_post_fx()
	_root = Control.new()
	_root.set_anchors_preset(Control.PRESET_FULL_RECT)
	_root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_root.theme = Ui.theme()
	add_child(_root)
	_vitals = _corner_label(Control.PRESET_TOP_LEFT, 18, Ui.PIP)
	_clock = _corner_label(Control.PRESET_CENTER_TOP, 26, Ui.INK)
	_clock.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_objectives = Ui.vbox(4)
	_objectives.set_anchors_and_offsets_preset(Control.PRESET_TOP_RIGHT)
	_objectives.position = Vector2(-460, 20)
	_objectives.custom_minimum_size.x = 440
	_root.add_child(_objectives)
	_toasts = Ui.vbox(4)
	_toasts.set_anchors_and_offsets_preset(Control.PRESET_CENTER_TOP)
	_toasts.position = Vector2(-400, 70)
	_toasts.custom_minimum_size.x = 800
	_root.add_child(_toasts)
	_prompt = _corner_label(Control.PRESET_CENTER, 22, Ui.INK)
	_prompt.position += Vector2(-200, 40)
	_prompt.custom_minimum_size.x = 400
	_prompt.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_subtitle = _corner_label(Control.PRESET_CENTER_BOTTOM, 24, Color(1, 1, 0.9))
	_subtitle.position = Vector2(-500, -190)
	_subtitle.custom_minimum_size.x = 1000
	_subtitle.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_net_warning = _corner_label(Control.PRESET_CENTER_TOP, 20, Ui.ALERT)
	_net_warning.position = Vector2(-400, 44)
	_net_warning.custom_minimum_size.x = 800
	_net_warning.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_build_bottom_bar()
	_build_gauges()
	_build_controls_hint()


func begin() -> void:
	_capture_mouse()
	toast("%s. %s" % [surgery.scenario.title, surgery.scenario.description])


func _process(delta: float) -> void:
	if surgery == null or surgery.local_surgeon == null:
		return
	var me := surgery.local_surgeon
	var v := surgery.patient.vitals
	_vitals.text = "HR %d  %s\nSpO2 %d%%   BP %d\nTemp %.1f   Blood %d%%\nBleeding %.1f ml/s%s" % [
		v.heart_rate, v.rhythm_name(), v.spo2, v.systolic, v.temperature, v.blood_ratio() * 100.0, v.bleed_rate,
		"\nPatient is AWAKE" if v.is_awake() else "",
	]
	_vitals.add_theme_color_override("font_color", Ui.ALERT if v.is_arrested() or v.spo2 < 88.0 else Ui.PIP)
	var left := surgery.time_left()
	_clock.text = "%s   %s" % [surgery.scenario.title, "%d:%02d" % [int(left) / 60, int(left) % 60] if left >= 0.0 else "no time limit"]
	_update_objectives()
	_update_net_warning()
	_update_hands(me)
	_update_gauges(me)
	_prompt.text = "[%s] %s" % [InputActions.binding_text("interact"), me.focused.prompt] if me.focused and _overlay == null else ""
	if _prompt.text.is_empty() and me.held_tool(me.active):
		var partner := me.pass_target(me.active)
		if not partner.is_empty():
			_prompt.text = "[%s] Pass to %s" % [InputActions.binding_text("grab"), (partner[0] as Surgeon).display_name]
	_subtitle_timer -= delta
	if _subtitle_timer <= 0.0:
		_subtitle.text = ""
	_post.set_shader_parameter("blackout", 1.0 if me.status.is_out() else 0.0)
	_post.set_shader_parameter("wobble", me.status.sickness)
	_post.set_shader_parameter("blur", me.status.sickness * 1.5)


## A lag spike shows up after a moment; the game carries on and the connection only drops after Net.TIMEOUT_MAX_MSEC.
func _update_net_warning() -> void:
	var worst := Net.worst_silence()
	var seconds: float = worst[1]
	if seconds < 1.5:
		_net_warning.text = ""
		return
	var who: String = "the host" if worst[0] == 1 else str(Net.roster.get(worst[0], {}).get("name", "your partner"))
	_net_warning.text = "Connection to %s is unstable (%d s). Waiting..." % [who, int(seconds)]


func _unhandled_input(event: InputEvent) -> void:
	if not event.is_action_pressed("pause"):
		return
	get_viewport().set_input_as_handled()
	if _overlay and _overlay.has_meta("locked"):
		return
	if _overlay:
		close_overlay()
	else:
		_open(_pause_menu())


func toast(text: String) -> void:
	if text.is_empty():
		return
	var l := Ui.label(text, 20, Ui.INK, true)
	l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_toasts.add_child(l)
	if _toasts.get_child_count() > 4:
		_toasts.get_child(0).queue_free()
	var tween := l.create_tween()
	tween.tween_interval(TOAST_TIME)
	tween.tween_property(l, "modulate:a", 0.0, 0.8)
	tween.tween_callback(l.queue_free)


func subtitle(text: String) -> void:
	_subtitle.text = "\"%s\"" % text
	_subtitle_timer = SUBTITLE_TIME


# --- Overlays --------------------------------------------------------------------------------------


func open_manual() -> void:
	var keys := PackedStringArray()
	if surgery.local_surgeon.mods.flag("manual_highlight"):
		keys.append_array(surgery.patient.mods.values.keys())
		keys.append_array(surgery.patient.mods.list("allergen"))
		for roll: Dictionary in surgery.patient.rolls:
			keys.append(roll.id)
	_open(ManualView.build(keys, close_overlay))


func open_card() -> void:
	_open(PatientCardView.build(surgery.patient, Net.session_seed, close_overlay))


func open_nurse() -> void:
	var choices: Array = []
	for def: ToolDef in Db.tools.values():
		if def.orderable:
			choices.append(["%s  (%d s)" % [def.name, def.delay], def.id])
	var cooldown: float = surgery.status.get("nurse", 0.0)
	var subtitle_text := "Nurse is busy for %d s." % ceili(cooldown) if cooldown > 0.0 else "One request at a time. Pick carefully."
	_open(ChoiceMenu.build("Ring for the nurse", subtitle_text, choices, _on_nurse_pick, close_overlay))


func open_lab() -> void:
	var choices: Array = []
	for kind: String in Lab.PANELS:
		choices.append(["%s  (%d s)" % [Lab.PANELS[kind].label, Lab.PANELS[kind].time], kind])
	var cooldown: float = surgery.status.get("lab", 0.0)
	var subtitle_text := "Lab is busy for %d s." % ceili(cooldown) if cooldown > 0.0 else "Narrow panels come back faster."
	_open(ChoiceMenu.build("Blood work", subtitle_text, choices, _on_lab_pick, close_overlay))


func _on_nurse_pick(tool_id: String) -> void:
	surgery.order_tool(tool_id)
	close_overlay()


func _on_lab_pick(kind: String) -> void:
	surgery.order_lab(kind)
	close_overlay()


func open_xray(cart: XrayCart) -> void:
	var box := Ui.vbox(12)
	var photo := XrayPhoto.new(cart)
	box.add_child(photo)
	var close := Ui.button("Put it down  [Esc]", close_overlay)
	close.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	box.add_child(close)
	var center := CenterContainer.new()
	center.add_child(box)
	_open(Ui.fullscreen(center, Color(0, 0, 0, 0.8)))


func run_qte(sequence: PackedStringArray, window: float, done: Callable) -> void:
	var qte := QteView.new()
	_root.add_child(qte)
	qte.start(sequence, window, done)


func show_report(report: Dictionary) -> void:
	var view := ReportView.build(report, surgery.scenario)
	view.set_meta("locked", true)
	_open(view)


func close_overlay() -> void:
	if _overlay:
		_overlay.queue_free()
		_overlay = null
	if surgery.local_surgeon:
		surgery.local_surgeon.input_locked = false
	_capture_mouse()


func _open(overlay: Control) -> void:
	if _overlay:
		_overlay.queue_free()
	_overlay = overlay
	add_child(overlay)
	if surgery.local_surgeon:
		surgery.local_surgeon.input_locked = true
		surgery.local_surgeon.hands[surgery.local_surgeon.active].engaged = false
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE


func _pause_menu() -> Control:
	var box := Ui.vbox(12)
	box.add_child(Ui.label("PAUSED (the patient isn't)", 28, Ui.PIP))
	box.add_child(Ui.button("Back to the table", close_overlay))
	box.add_child(Ui.button("Leave to main menu", Net.back_to_menu))
	var center := CenterContainer.new()
	center.add_child(Ui.panel(box))
	return Ui.fullscreen(center, Color(0, 0, 0, 0.6))


func _capture_mouse() -> void:
	if surgery.running and DisplayServer.get_name() != "headless":
		Input.mouse_mode = Input.MOUSE_MODE_CAPTURED


# --- Building --------------------------------------------------------------------------------------


func _build_post_fx() -> void:
	var layer := CanvasLayer.new()
	layer.layer = 0
	add_child(layer)
	var rect := ColorRect.new()
	rect.set_anchors_preset(Control.PRESET_FULL_RECT)
	rect.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_post = ShaderMaterial.new()
	_post.shader = POST_FX
	rect.material = _post
	layer.add_child(rect)


func _corner_label(preset: Control.LayoutPreset, size: int, color: Color) -> Label:
	var l := Ui.label("", size, color)
	l.autowrap_mode = TextServer.AUTOWRAP_OFF
	l.set_anchors_and_offsets_preset(preset, Control.PRESET_MODE_MINSIZE, 20)
	_root.add_child(l)
	return l


func _build_bottom_bar() -> void:
	var bar := Ui.vbox(4)
	bar.set_anchors_and_offsets_preset(Control.PRESET_CENTER_BOTTOM)
	bar.position = Vector2(-300, -110)
	bar.custom_minimum_size.x = 600
	_hands = Ui.label("", 20, Ui.INK)
	_hands.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	bar.add_child(_hands)
	_belt = Ui.hbox(6)
	_belt.alignment = BoxContainer.ALIGNMENT_CENTER
	bar.add_child(_belt)
	_root.add_child(bar)


func _build_gauges() -> void:
	var box := Ui.vbox(4)
	box.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_LEFT)
	box.position = Vector2(20, -180)
	for key: String in ["stress", "sickness", "breath", "sweat"]:
		var row := Ui.hbox(8)
		var name_label := Ui.label(key.capitalize(), 16, Ui.DIM)
		name_label.custom_minimum_size.x = 80
		row.add_child(name_label)
		var b := Ui.bar({"stress": Ui.ALERT, "sickness": Color(0.6, 0.75, 0.2), "breath": Color(0.5, 0.75, 1.0), "sweat": Color(0.8, 0.8, 0.6)}[key])
		row.add_child(b)
		box.add_child(row)
		_gauges[key] = row
	_root.add_child(box)


func _build_controls_hint() -> void:
	var lines := PackedStringArray()
	for action in InputActions.HINT_ACTIONS:
		lines.append("%s  %s" % [InputActions.binding_text(action), InputActions.label_for(action)])
	lines.append("%s  Move" % "/".join(["move_forward", "move_left", "move_back", "move_right"].map(InputActions.binding_text)))
	lines.append("Wheel  Hand height / pressure")
	var hint := Ui.label("\n".join(lines), 14, Ui.HINT)
	hint.autowrap_mode = TextServer.AUTOWRAP_OFF
	hint.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	hint.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_RIGHT, Control.PRESET_MODE_MINSIZE, 16)
	hint.grow_horizontal = Control.GROW_DIRECTION_BEGIN
	hint.grow_vertical = Control.GROW_DIRECTION_BEGIN
	_root.add_child(hint)


# --- Updating --------------------------------------------------------------------------------------


func _update_objectives() -> void:
	var data: Array = surgery.status.get("objectives", [])
	while _objectives.get_child_count() < data.size():
		_objectives.add_child(Ui.label("", 18, Ui.INK, true))
	for i in data.size():
		var entry: Array = data[i]
		var l := _objectives.get_child(i) as Label
		l.text = "%s %s%s" % ["☑" if entry[1] else "▶" if entry[3] else "☐", entry[0], "  (bonus)" if entry[2] else ""]
		l.add_theme_color_override("font_color", Ui.DIM if entry[1] else Ui.PIP if entry[3] else Ui.INK)


func _update_hands(me: Surgeon) -> void:
	var parts := PackedStringArray()
	for i in 2:
		var tool := me.held_tool(i)
		var text := "%s: %s" % ["L" if i == 0 else "R", tool.def.name if tool else "empty"]
		if tool and not tool.sterile and me.mods.flag("contamination_vision"):
			text += " (dirty)"
		if me.hands[i].attached:
			text += " [holding]"
		parts.append(("▶ " + text + " ◀") if i == me.active else text)
	var active_tool := me.held_tool(me.active)
	var tension := active_tool != null and active_tool.def.id in Patient.TENSIONED_CLOSURES
	var level: String = (["", "loose", "right", "TIGHT"] if tension else ["", "light", "normal", "DEEP"])[me.hands[me.active].pressure]
	_hands.text = "   ".join(parts) + ("   tension: " if tension else "   pressure: ") + level
	var capacity := me.belt_capacity()
	while _belt.get_child_count() < capacity:
		var slot := Ui.label("", 16, Ui.DIM)
		slot.autowrap_mode = TextServer.AUTOWRAP_OFF
		_belt.add_child(slot)
	for i in capacity:
		var tool := surgery.tools.tool_on_belt(me.peer_id, i)
		(_belt.get_child(i) as Label).text = "[%d] %s" % [i + 1, tool.def.name if tool else "—"]


func _update_gauges(me: Surgeon) -> void:
	var values := {"stress": me.status.stress, "sickness": me.status.sickness, "breath": me.status.breath, "sweat": me.status.sweat}
	for key: String in values:
		var row: HBoxContainer = _gauges[key]
		(row.get_child(1) as ProgressBar).value = values[key]
		row.visible = key == "stress" or values[key] > 0.01 and (key != "breath" or values[key] < 0.99)
