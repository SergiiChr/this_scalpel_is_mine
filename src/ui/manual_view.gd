class_name ManualView
extends RefCounted
## The in-game manual. Takes the whole screen on purpose: reading it means not operating.
## Divine knowledge points a hand at the pages that match the patient's hidden conditions.
## Styled as a Victorian surgical guide: parchment sheets in ruled frames, red and black ink, hand written notes.
## Page markup on top of BBCode: a line starting with "## " is a section heading, one starting with "> " a note.

const INK := Color("231c16")
const RED := Color("b3241c")
const GLOW := Color("9a6a00")
const BODY_FONT := "res://assets/fonts/IMFellEnglish-Regular.ttf"
const ITALIC_FONT := "res://assets/fonts/IMFellEnglish-Italic.ttf"
const CAPS_FONT := "res://assets/fonts/IMFellEnglishSC-Regular.ttf"
const SCRIPT_FONT := "res://assets/fonts/LaBelleAurore-Regular.ttf"
const PAPER := preload("res://assets/manual/paper.jpg")
const FRAME := preload("res://assets/manual/frame.svg")
## Width of the frame texture's border, matches FRAME_MARGIN in tools/assetgen/manual.py.
const FRAME_MARGIN := 44


static func build(highlight_keys: PackedStringArray, on_close: Callable) -> Control:
	var book := Ui.hbox(28)
	book.theme = _theme()
	var title := _label("", 40, RED, CAPS_FONT)
	title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	var page := RichTextLabel.new()
	page.bbcode_enabled = true
	page.size_flags_vertical = Control.SIZE_EXPAND_FILL
	var content := Ui.vbox(16)
	content.add_child(_title_box(title))
	content.add_child(page)

	var toc := Ui.vbox(0)
	var heading := _label("Operating Theatre\nProcedures", 30, RED, CAPS_FONT)
	heading.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	toc.add_child(heading)
	var revision := _label("~ Revision the Seventh ~", 18, INK, ITALIC_FONT)
	revision.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	toc.add_child(revision)
	toc.add_child(Control.new())
	var entries: Array[Button] = []
	for p in Db.manual:
		var glowing := not highlight_keys.is_empty() and p.matches(highlight_keys)
		var entry := Ui.button(("☞ " if glowing else "") + p.title, func() -> void: _show(title, page, p, entries))
		entry.alignment = HORIZONTAL_ALIGNMENT_LEFT
		entry.set_meta("page", p)
		entry.set_meta("glowing", glowing)
		entries.append(entry)
		toc.add_child(entry)
	toc.add_child(Control.new())
	var close := Ui.button("Close  [Esc]", on_close)
	close.add_theme_font_override("font", load(CAPS_FONT))
	close.add_theme_color_override("font_color", RED)
	toc.add_child(_title_box(close))

	var toc_sheet := _sheet(Ui.scroll(toc))
	toc_sheet.custom_minimum_size.x = 440
	book.add_child(toc_sheet)
	var page_sheet := _sheet(content)
	page_sheet.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	book.add_child(page_sheet)
	if not Db.manual.is_empty():
		_show(title, page, Db.manual[0], entries)
	return Ui.fullscreen(book, Color(0.02, 0.02, 0.02, 0.97))


## Turns the page's own markup into BBCode: "## 4.1 Name" headings and "> " notes.
static func to_bbcode(body: String) -> String:
	var lines := PackedStringArray()
	for line in body.split("\n"):
		if line.begins_with("## "):
			var number := line.substr(3).get_slice(" ", 0)
			var name := line.substr(4 + number.length())
			lines.append("[font=%s][font_size=30][color=#%s]%s[/color][/font_size][/font]  [font=%s][font_size=28]%s[/font_size][/font]" % [
				SCRIPT_FONT, RED.to_html(false), number, CAPS_FONT, name])
		elif line.begins_with("> "):
			lines.append("[indent][font=%s][font_size=24][color=#%s]%s[/color][/font_size][/font][/indent]" % [
				SCRIPT_FONT, RED.to_html(false), line.substr(2)])
		else:
			lines.append(line)
	return "\n".join(lines)


static func _show(title: Label, page: RichTextLabel, manual_page: ManualPage, entries: Array[Button]) -> void:
	# The heading shows the name only: "4. Incision and exposure" becomes "Incision and exposure".
	title.text = manual_page.title.get_slice(". ", 1) if ". " in manual_page.title else manual_page.title
	page.text = to_bbcode(manual_page.body)
	page.scroll_to_line(0)
	for entry in entries:
		var color := RED if entry.get_meta("page") == manual_page else (GLOW if entry.get_meta("glowing") else INK)
		entry.add_theme_color_override("font_color", color)
		entry.add_theme_color_override("font_focus_color", color)
	Sfx.play("page_turn")


## A parchment sheet in the ruled frame.
static func _sheet(child: Control) -> PanelContainer:
	var paper := StyleBoxTexture.new()
	paper.texture = PAPER
	var sheet := PanelContainer.new()
	sheet.add_theme_stylebox_override("panel", paper)
	var frame := NinePatchRect.new()
	frame.texture = FRAME
	frame.patch_margin_left = FRAME_MARGIN
	frame.patch_margin_top = FRAME_MARGIN
	frame.patch_margin_right = FRAME_MARGIN
	frame.patch_margin_bottom = FRAME_MARGIN
	frame.mouse_filter = Control.MOUSE_FILTER_IGNORE
	sheet.add_child(frame)
	var margin := MarginContainer.new()
	for side: String in ["left", "right", "top", "bottom"]:
		margin.add_theme_constant_override("margin_" + side, FRAME_MARGIN + 20)
	margin.add_child(child)
	sheet.add_child(margin)
	return sheet


## Black rule inside a red one, like the title plates of old printed guides.
static func _title_box(child: Control) -> PanelContainer:
	var outer := PanelContainer.new()
	outer.add_theme_stylebox_override("panel", _rule(RED, 1, 4))
	var inner := PanelContainer.new()
	inner.add_theme_stylebox_override("panel", _rule(INK, 2, 6))
	inner.add_child(child)
	outer.add_child(inner)
	return outer


static func _rule(color: Color, width: int, padding: int) -> StyleBoxFlat:
	var box := StyleBoxFlat.new()
	box.draw_center = false
	box.border_color = color
	box.set_border_width_all(width)
	box.set_content_margin_all(padding)
	return box


static func _label(text: String, size: int, color: Color, font: String) -> Label:
	var label := Ui.label(text, size, color)
	label.add_theme_font_override("font", load(font))
	return label


static func _theme() -> Theme:
	var theme := Theme.new()
	theme.default_font = load(BODY_FONT)
	theme.default_font_size = 22
	var flat := StyleBoxEmpty.new()
	flat.set_content_margin_all(3)
	for state: String in ["normal", "hover", "pressed", "focus", "disabled"]:
		theme.set_stylebox(state, "Button", flat)
	theme.set_color("font_color", "Button", INK)
	theme.set_color("font_hover_color", "Button", RED)
	theme.set_color("font_pressed_color", "Button", RED)
	theme.set_color("font_hover_pressed_color", "Button", RED)
	theme.set_color("default_color", "RichTextLabel", INK)
	theme.set_font("normal_font", "RichTextLabel", load(BODY_FONT))
	theme.set_font("bold_font", "RichTextLabel", load(CAPS_FONT))
	theme.set_font("italics_font", "RichTextLabel", load(ITALIC_FONT))
	theme.set_font_size("normal_font_size", "RichTextLabel", 22)
	theme.set_font_size("bold_font_size", "RichTextLabel", 22)
	theme.set_font_size("italics_font_size", "RichTextLabel", 22)
	theme.set_color("table_odd_row_bg", "RichTextLabel", Color(0, 0, 0, 0))
	theme.set_color("table_even_row_bg", "RichTextLabel", Color(INK, 0.06))
	theme.set_constant("table_h_separation", "RichTextLabel", 16)
	theme.set_constant("line_separation", "RichTextLabel", 4)
	theme.set_stylebox("normal", "RichTextLabel", StyleBoxEmpty.new())
	for bar: String in ["VScrollBar"]:
		var grabber := StyleBoxFlat.new()
		grabber.bg_color = Color(INK, 0.35)
		grabber.content_margin_left = 3
		grabber.content_margin_right = 3
		theme.set_stylebox("scroll", bar, StyleBoxEmpty.new())
		for state: String in ["grabber", "grabber_highlight", "grabber_pressed"]:
			theme.set_stylebox(state, bar, grabber)
	return theme
