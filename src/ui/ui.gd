class_name Ui
extends RefCounted
## Small builders so every screen looks the same. Colors and sizes live here.

const INK := Color(0.82, 0.9, 0.84)
const DIM := Color(0.55, 0.62, 0.58)
const HINT := Color(0.7, 0.7, 0.7, 0.45)
const ALERT := Color(0.95, 0.35, 0.3)
const GOOD := Color(0.45, 0.95, 0.55)
const PIP := Color(0.35, 1.0, 0.5)
const PANEL := Color(0.05, 0.07, 0.07, 0.88)
const PAPER := Color(0.86, 0.82, 0.72)
const PAPER_INK := Color(0.12, 0.1, 0.09)

static var _theme: Theme


static func theme() -> Theme:
	if _theme == null:
		_theme = Theme.new()
		_theme.default_font_size = 20
		_theme.set_color("font_color", "Label", INK)
		_theme.set_color("font_color", "Button", INK)
		_theme.set_color("font_hover_color", "Button", PIP)
		_theme.set_color("font_pressed_color", "Button", PIP)
		_theme.set_color("font_disabled_color", "Button", DIM)
		for state: String in ["normal", "hover", "pressed", "disabled", "focus"]:
			var box := StyleBoxFlat.new()
			box.bg_color = Color(0.1, 0.13, 0.12, 0.9) if state != "hover" else Color(0.15, 0.22, 0.18, 0.95)
			box.border_color = PIP if state in ["hover", "focus"] else Color(0.25, 0.32, 0.28)
			box.set_border_width_all(1)
			box.set_content_margin_all(8)
			if state == "focus":
				box.draw_center = false
			_theme.set_stylebox(state, "Button", box)
		var panel := StyleBoxFlat.new()
		panel.bg_color = PANEL
		panel.border_color = Color(0.2, 0.28, 0.24)
		panel.set_border_width_all(1)
		panel.set_content_margin_all(16)
		_theme.set_stylebox("panel", "PanelContainer", panel)
	return _theme


## wrap: break long text over lines. Only for labels whose container sets the width (VBox, not HBox).
static func label(text: String, size: int = 20, color: Color = INK, wrap: bool = false) -> Label:
	var l := Label.new()
	l.text = text
	l.add_theme_font_size_override("font_size", size)
	l.add_theme_color_override("font_color", color)
	if wrap:
		l.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
		l.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	return l


static func button(text: String, callback: Callable) -> Button:
	var b := Button.new()
	b.text = text
	b.pressed.connect(callback)
	return b


static func vbox(separation: int = 8) -> VBoxContainer:
	var box := VBoxContainer.new()
	box.add_theme_constant_override("separation", separation)
	return box


static func hbox(separation: int = 8) -> HBoxContainer:
	var box := HBoxContainer.new()
	box.add_theme_constant_override("separation", separation)
	return box


static func panel(child: Control) -> PanelContainer:
	var p := PanelContainer.new()
	p.add_child(child)
	return p


static func bar(color: Color) -> ProgressBar:
	var b := ProgressBar.new()
	b.max_value = 1.0
	b.show_percentage = false
	b.custom_minimum_size = Vector2(160, 10)
	var fill := StyleBoxFlat.new()
	fill.bg_color = color
	var back := StyleBoxFlat.new()
	back.bg_color = Color(0.1, 0.1, 0.1, 0.6)
	b.add_theme_stylebox_override("fill", fill)
	b.add_theme_stylebox_override("background", back)
	return b


static func icon(texture: Texture2D, size: int = 48) -> TextureRect:
	var rect := TextureRect.new()
	rect.texture = texture
	rect.custom_minimum_size = Vector2(size, size)
	rect.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	rect.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
	return rect


static func scroll(child: Control) -> ScrollContainer:
	var s := ScrollContainer.new()
	s.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	s.size_flags_vertical = Control.SIZE_EXPAND_FILL
	child.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	s.add_child(child)
	return s


## Full screen dark backdrop with a centered child.
static func fullscreen(child: Control, backdrop: Color = Color(0, 0, 0, 0.85)) -> Control:
	var root := Control.new()
	root.set_anchors_preset(Control.PRESET_FULL_RECT)
	root.theme = theme()
	var bg := ColorRect.new()
	bg.color = backdrop
	bg.set_anchors_preset(Control.PRESET_FULL_RECT)
	root.add_child(bg)
	var margin := MarginContainer.new()
	margin.set_anchors_preset(Control.PRESET_FULL_RECT)
	for side: String in ["left", "right", "top", "bottom"]:
		margin.add_theme_constant_override("margin_" + side, 48)
	margin.add_child(child)
	root.add_child(margin)
	return root


static func quirk_line(quirk: QuirkDef, variant: String, detailed: bool) -> Control:
	var row := hbox(12)
	row.add_child(icon(quirk.icon(), 56 if detailed else 36))
	var text := vbox(2)
	var polarity := quirk.polarity(variant)
	var color: Color = {"positive": GOOD, "negative": ALERT, "mixed": Color(0.95, 0.8, 0.35)}.get(polarity, INK)
	text.add_child(label("%s  [%s]" % [quirk.display_name(variant), polarity], 20, color))
	if detailed:
		text.add_child(label(quirk.text("lore", variant), 16, DIM, true))
		text.add_child(label("+ " + quirk.text("pros", variant), 16, GOOD, true))
		text.add_child(label("- " + quirk.text("cons", variant), 16, ALERT, true))
		text.add_child(label(quirk.text("specifics", variant), 16, INK, true))
	text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(text)
	return row
