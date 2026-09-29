class_name ManualView
extends RefCounted
## The in-game manual. Takes the whole screen on purpose: reading it means not operating.
## Divine knowledge makes pages that match the patient's hidden conditions glow.


static func build(highlight_keys: PackedStringArray, on_close: Callable) -> Control:
	var book := Ui.hbox(0)
	var toc := Ui.vbox(4)
	toc.custom_minimum_size.x = 320
	var page := RichTextLabel.new()
	page.bbcode_enabled = true
	page.fit_content = false
	page.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	page.size_flags_vertical = Control.SIZE_EXPAND_FILL
	page.add_theme_color_override("default_color", Ui.PAPER_INK)
	page.add_theme_font_size_override("normal_font_size", 22)
	page.add_theme_font_size_override("bold_font_size", 24)
	var paper := StyleBoxFlat.new()
	paper.bg_color = Ui.PAPER
	paper.set_content_margin_all(40)
	page.add_theme_stylebox_override("normal", paper)

	toc.add_child(Ui.label("OPERATING THEATRE PROCEDURES", 24, Ui.PIP))
	toc.add_child(Ui.label("Revision 7", 14, Ui.DIM))
	for p in Db.manual:
		var glowing := not highlight_keys.is_empty() and p.matches(highlight_keys)
		var entry := Ui.button(("✦ " if glowing else "") + p.title, func() -> void: _show(page, p))
		entry.alignment = HORIZONTAL_ALIGNMENT_LEFT
		if glowing:
			entry.add_theme_color_override("font_color", Color(1.0, 0.85, 0.35))
		toc.add_child(entry)
	toc.add_child(Control.new())
	toc.add_child(Ui.button("Close  [Esc]", on_close))
	book.add_child(Ui.scroll(toc))
	book.add_child(page)
	if not Db.manual.is_empty():
		_show(page, Db.manual[0])
	return Ui.fullscreen(book, Color(0.02, 0.02, 0.02, 0.97))


static func _show(page: RichTextLabel, manual_page: ManualPage) -> void:
	page.text = "[font_size=34][b]%s[/b][/font_size]\n\n%s" % [manual_page.title, manual_page.body]
	Sfx.play("page_turn")
