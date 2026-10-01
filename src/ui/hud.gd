class_name Hud
extends CanvasLayer
## Everything drawn on screen during surgery, plus the full screen overlays (manual, card, menus, report).
## While an overlay is open the surgeon's input is locked and the mouse is free.

const POST_FX := preload("res://assets/shaders/post_grime.gdshader")
const TOAST_TIME := 5.0
const SUBTITLE_TIME := 6.0

## What each effort level (0..3) does, for tools that take one (see level_kind()).
const LEVEL_STEPS: Dictionary = {
	"cut": ["Resting on skin", "Skin", "Fat", "Muscle, into cavity"],
	"tension": ["Off", "Loose", "Correct", "Tight"],
	"inject": ["Not pushed", "A third in", "Two thirds in", "All in"],
	"effort": ["Off", "Low", "Medium", "High"],
}
## Length of the blade edge line drawn on the skin (m).
const BLADE_LINE := 0.04

var surgery: Surgery
var _clock: Label
var _objectives: VBoxContainer
var _hands: Label
var _belt: HBoxContainer
var _prompt: Label
var _net_warning: Label
## Aim at the active tool tip: a dot, or for blades a line along the edge where it will cut.
## Beside it, the name of the tool the hand would pick up.
var _dot: Panel
var _blade: Line2D
## Controls for what the player is doing right now, bottom right. Changes while a hand key or a tool is held.
var _hint: Label
var _dot_label: Label
## Effort levels of the active tool beside the aim, when the tool has any (see LEVEL_STEPS).
var _levels: RichTextLabel
var _toasts: VBoxContainer
var _subtitle: Label
var _subtitle_timer := 0.0
var _gauges: Dictionary = {}
var _overlay: Control
var _post: ShaderMaterial
## Blood on the view (0..1), how long since it was clean, and where its drops sit (a new pattern per clean start).
var _lens_blood := 0.0
var _lens_age := 0.0
## Blood on the view clears in about this many seconds.
const LENS_CLEAR_SECONDS := 8.0
## A sedative blurs the view by this much (the screen's mip level) at the right dose. Past it the view darkens, up to
## OVERDOSE_DAZE just short of a knockout, and lying knocked out it's KNOCKED_OUT_DAZE dark: still enough to see by.
const SEDATED_BLUR := 1.0
const OVERDOSE_DAZE := 0.6
const KNOCKED_OUT_DAZE := 0.8
var _root: Control


func setup(owner_surgery: Surgery) -> void:
	surgery = owner_surgery
	_build_post_fx()
	surgery.patient.body.blood.splashed.connect(_on_blood_splashed)
	_root = Control.new()
	_root.set_anchors_preset(Control.PRESET_FULL_RECT)
	_root.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_root.theme = Ui.theme()
	add_child(_root)
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
	_build_dot()
	_build_bottom_bar()
	_build_gauges()
	_build_controls_hint()


func begin() -> void:
	_capture_mouse()
	toast(surgery.scenario.title)


func _process(delta: float) -> void:
	if surgery == null or surgery.local_surgeon == null:
		return
	var me := surgery.local_surgeon
	var left := surgery.time_left()
	_clock.text = "%s   %s" % [surgery.scenario.title, "%d:%02d" % [int(left) / 60, int(left) % 60] if left >= 0.0 else "no time limit"]
	_update_objectives()
	_update_net_warning()
	_update_dot(me)
	_update_hands(me)
	_update_gauges(me)
	var hint := "\n".join(control_lines(me))
	if _hint.text != hint:
		_hint.text = hint
	_prompt.text = "[%s] %s" % [InputActions.binding_text("interact"), me.focused.prompt] if me.focused and _overlay == null else ""
	if _prompt.text.is_empty() and me.held_tool(me.active):
		var partner := me.pass_target(me.active)
		if not partner.is_empty():
			_prompt.text = "[%s] Pass to %s" % [InputActions.binding_text("grab"), (partner[0] as Surgeon).display_name]
	_subtitle_timer -= delta
	if _subtitle_timer <= 0.0:
		_subtitle.text = ""
	_post.set_shader_parameter("blackout", 1.0 if me.status.passed_out > 0.0 else 0.0)
	_post.set_shader_parameter("daze", KNOCKED_OUT_DAZE if me.status.is_knocked_out() else me.status.overdose * OVERDOSE_DAZE)
	_post.set_shader_parameter("wobble", me.status.sickness)
	_post.set_shader_parameter("blur", maxf(me.status.sickness * 1.5, me.status.calm * SEDATED_BLUR))
	if _lens_blood > 0.0:
		_lens_blood = maxf(_lens_blood - delta / LENS_CLEAR_SECONDS, 0.0)
		_lens_age += delta
		_post.set_shader_parameter("lens_blood", _lens_blood)
		_post.set_shader_parameter("lens_age", _lens_age)


func _on_blood_splashed(amount: float) -> void:
	if _lens_blood <= 0.0:
		_lens_age = 0.0
		_post.set_shader_parameter("lens_seed", randf() * 100.0)
	_lens_blood = minf(_lens_blood + amount, 1.0)


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
	var groups: Dictionary = {}
	for def: ToolDef in Db.tools.values():
		if def.orderable:
			if not groups.has(def.category):
				groups[def.category] = []
			(groups[def.category] as Array).append(["%s  (%d s)" % [def.name, def.delay], def.id])
	_open(ChoiceMenu.build_grouped("Ring for the nurse", Room.nurse_board_text(surgery.status), groups, _on_nurse_pick, close_overlay))


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
		surgery.local_surgeon.hands[surgery.local_surgeon.active].lowered = false
		surgery.local_surgeon.hands[surgery.local_surgeon.active].trigger = false
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


func _build_dot() -> void:
	_dot = Panel.new()
	var style := StyleBoxFlat.new()
	style.bg_color = Color(1.0, 1.0, 0.9, 0.85)
	style.border_color = Color(0.0, 0.0, 0.0, 0.6)
	style.set_border_width_all(1)
	style.set_corner_radius_all(4)
	_dot.add_theme_stylebox_override("panel", style)
	_dot.size = Vector2(8, 8)
	_dot.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_root.add_child(_dot)
	# A dark line with a light one on top, so it shows on pale skin and in blood alike.
	_blade = Line2D.new()
	_blade.width = 5.0
	_blade.default_color = Color(0.0, 0.0, 0.0, 0.6)
	var edge := Line2D.new()
	edge.width = 2.5
	for line: Line2D in [_blade, edge]:
		line.begin_cap_mode = Line2D.LINE_CAP_ROUND
		line.end_cap_mode = Line2D.LINE_CAP_ROUND
	_blade.add_child(edge)
	_root.add_child(_blade)
	_dot_label = Ui.label("", 16, Ui.INK)
	_dot_label.autowrap_mode = TextServer.AUTOWRAP_OFF
	_root.add_child(_dot_label)
	_levels = RichTextLabel.new()
	_levels.bbcode_enabled = true
	_levels.fit_content = true
	_levels.autowrap_mode = TextServer.AUTOWRAP_OFF
	_levels.scroll_active = false
	_levels.custom_minimum_size.x = 260
	_levels.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_levels.add_theme_font_size_override("normal_font_size", 15)
	_levels.add_theme_constant_override("outline_size", 4)
	_levels.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.8))
	_root.add_child(_levels)


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
	_hint = Ui.label("", 14, Ui.HINT)
	_hint.autowrap_mode = TextServer.AUTOWRAP_OFF
	_hint.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	_hint.set_anchors_and_offsets_preset(Control.PRESET_BOTTOM_RIGHT, Control.PRESET_MODE_MINSIZE, 16)
	_hint.grow_horizontal = Control.GROW_DIRECTION_BEGIN
	_hint.grow_vertical = Control.GROW_DIRECTION_BEGIN
	_root.add_child(_hint)


## The controls that do something right now. Holding a hand key swaps looking for moving that hand.
static func control_lines(me: Surgeon) -> PackedStringArray:
	var key := InputActions.binding_text
	var moving := me.moving_hand()
	var hand := me.hands[me.active]
	var tool := me.held_tool(me.active)
	var side := "left" if me.active == 0 else "right"
	var lines := PackedStringArray()
	if moving < 0:
		lines.append("Mouse  Look around")
		lines.append("%s / %s (hold)  Move left / right hand" % [key.call("move_left_hand"), key.call("move_right_hand")])
	else:
		lines.append("Mouse  Move %s hand" % side)
	if tool:
		var action := tool.def.action
		lines.append("%s (hold)  %s %s" % [key.call("use_tool"), ToolActions.TRIGGER_NAMES.get(action, "Press in" if action == "syringe" else "Use"), tool.label()])
		if action == "syringe":
			lines.append("%s  Pull plunger 1 ml" % key.call("level_down"))
			lines.append("%s  Push plunger 1 ml" % key.call("level_up"))
		elif me.uses_level(me.active):
			lines.append("Wheel  %s" % ToolActions.LEVEL_NAMES[action])
		lines.append("%s / %s  Tilt   %s / %s  Rotate" % [key.call("tilt_forward"), key.call("tilt_back"), key.call("twist_left"), key.call("twist_right")])
		lines.append("%s (hold)  Look at it" % key.call("inspect"))
		lines.append("%s  %s" % [key.call("grab"), "Pass" if not me.pass_target(me.active).is_empty() and not hand.attached else "Put down"])
	else:
		lines.append("%s  Pick up%s" % [key.call("grab"), " " + me.hovered.label() if is_instance_valid(me.hovered) else ""])
	var needle_view := tool and tool.def.action in Surgeon.NEEDLE_ACTIONS and me.zoom == Surgeon.ZOOM_FOV.size() - 1
	lines.append("%s  Zoom %d/%d%s" % [key.call("zoom"), me.zoom + 1, Surgeon.ZOOM_FOV.size(), ", needle view" if needle_view else ""])
	lines.append("%s (hold)  %s" % [key.call("lift"), "Pull up" if hand.attached else "Lift hand over"])
	lines.append("%s (hold)  Hold breath" % key.call("steady"))
	if moving < 0:
		lines.append("%s  Move" % "/".join(["move_forward", "move_left", "move_back", "move_right"].map(key)))
		lines.append("%s (hold)  Crouch" % key.call("crouch"))
		lines.append("%s  Interact" % key.call("interact"))
		lines.append("%s  Drink / wear" % key.call("drink"))
		lines.append("%s-%s  Belt slots" % [key.call("belt_1"), key.call("belt_4")])
	return lines


# --- Updating --------------------------------------------------------------------------------------


## Debug mode only: the steps the game checks and the latest scored actions. Normal play shows neither.
func _update_objectives() -> void:
	_objectives.visible = Settings.debug
	if not Settings.debug:
		return
	var data: Array = surgery.status.get("objectives", [])
	var recent: Array = surgery.status.get("log", [])
	var score_lines := PackedStringArray(["", "Score %d" % surgery.status.get("score", 0)])
	for entry: Array in recent:
		score_lines.append("%+d  %s" % [entry[1], entry[0]])
	data = data + [["\n".join(score_lines), false, false, false]]
	while _objectives.get_child_count() < data.size():
		_objectives.add_child(Ui.label("", 18, Ui.INK, true))
	for i in data.size():
		var entry: Array = data[i]
		var l := _objectives.get_child(i) as Label
		l.text = "%s %s%s" % ["☑" if entry[1] else "▶" if entry[3] else "☐", entry[0], "  (bonus)" if entry[2] else ""]
		l.add_theme_color_override("font_color", Ui.DIM if entry[1] else Ui.PIP if entry[3] else Ui.INK)


func _update_dot(me: Surgeon) -> void:
	var camera := me.camera()
	var aim := me.aim_point()
	var tool := me.held_tool(me.active)
	var shown := _overlay == null and not camera.is_position_behind(aim)
	var blade := shown and tool != null and tool.def.action == "cut"
	_dot.visible = shown and not blade
	_blade.visible = blade
	if not shown:
		_dot_label.text = ""
		_levels.visible = false
		return
	var at := camera.unproject_position(aim)
	_dot.position = at - _dot.size * 0.5
	if blade:
		var edge := ToolActions.blade_direction(tool) * BLADE_LINE * 0.5
		_blade.points = PackedVector2Array([camera.unproject_position(aim - edge), camera.unproject_position(aim + edge)])
		var cutting := me.hands[me.active].lowered and me.hands[me.active].level > 0
		var light := _blade.get_child(0) as Line2D
		light.points = _blade.points
		light.default_color = Color(1.0, 0.42, 0.35) if cutting else Color(1.0, 1.0, 0.9)
	_dot_label.text = me.hovered.label() if is_instance_valid(me.hovered) else ""
	_dot_label.position = at + Vector2(10, -10)
	_levels.text = _level_text(me)
	_levels.visible = not _levels.text.is_empty()
	_levels.position = at + Vector2(14, 12)


## Which LEVEL_STEPS names a tool's effort levels, "" when it takes none.
static func level_kind(tool: SurgicalTool) -> String:
	if tool == null or not ToolActions.LEVEL_NAMES.has(tool.def.action):
		return ""
	if tool.def.id in Patient.TENSIONED_CLOSURES:
		return "tension"
	return tool.def.action if LEVEL_STEPS.has(tool.def.action) else "effort"


## The levels, the current one marked; red while the tool is working at it.
func _level_text(me: Surgeon) -> String:
	var tool := me.held_tool(me.active)
	var kind := level_kind(tool)
	if kind.is_empty():
		return ""
	var hand := me.hands[me.active]
	var working := ToolActions.in_use(tool.def.action, hand.lowered, hand.trigger, hand.level)
	var lines := PackedStringArray([ToolActions.LEVEL_NAMES[tool.def.action]])
	for level in 4:
		var text := "%d  %s" % [level, LEVEL_STEPS[kind][level]]
		if level == hand.level:
			lines.append("[color=%s][b]▶ %s[/b][/color]" % ["#ff6a5a" if working else "#fff4c8", text])
		else:
			lines.append("[color=#ffffff80]   %s[/color]" % text)
	return "\n".join(lines)


func _update_hands(me: Surgeon) -> void:
	var parts := PackedStringArray()
	for i in 2:
		var tool := me.held_tool(i)
		var text := "%s: %s" % ["L" if i == 0 else "R", tool.label() if tool else "empty"]
		if tool and not tool.sterile and me.mods.flag("contamination_vision"):
			text += " (dirty)"
		if me.hands[i].attached:
			text += " [holding]"
		parts.append(("▶ " + text + " ◀") if i == me.active else text)
	var active_tool := me.held_tool(me.active)
	var kind := level_kind(active_tool)
	if not kind.is_empty():
		var level := me.hands[me.active].level
		parts.append("%s: %d %s" % [ToolActions.LEVEL_NAMES[active_tool.def.action].to_lower(), level, LEVEL_STEPS[kind][level]])
	_hands.text = "   ".join(parts)
	var capacity := me.belt_capacity()
	while _belt.get_child_count() < capacity:
		var slot := Ui.label("", 16, Ui.DIM)
		slot.autowrap_mode = TextServer.AUTOWRAP_OFF
		_belt.add_child(slot)
	for i in capacity:
		var tool := surgery.tools.tool_on_belt(me.peer_id, i)
		(_belt.get_child(i) as Label).text = "[%d] %s" % [i + 1, tool.label() if tool else "—"]


func _update_gauges(me: Surgeon) -> void:
	var values := {"stress": me.status.stress, "sickness": me.status.sickness, "breath": me.status.breath, "sweat": me.status.sweat}
	for key: String in values:
		var row: HBoxContainer = _gauges[key]
		(row.get_child(1) as ProgressBar).value = values[key]
		row.visible = key == "stress" or values[key] > 0.01 and (key != "breath" or values[key] < 0.99)
