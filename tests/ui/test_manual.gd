extends GutTest
## The manual's chronic conditions section as a reader opens it: listed as one entry, its conditions show under it once
## it's open, and Divine knowledge points at the patient's condition through both levels.

const TAGS = ["smoke"]


func test_conditions_open_under_their_section_and_divine_knowledge_points_through() -> void:
	var view := ManualView.build(PackedStringArray(["aneurysm"]), func() -> void: pass)
	add_child_autofree(view)
	var buttons: Array[Button] = []
	buttons.assign(view.find_children("*", "Button", true, false).filter(func(b: Button) -> bool: return b.has_meta("page")))
	var section := _entry(buttons, "17. Chronic conditions")
	var aneurysm := _entry(buttons, "Aneurysm")
	var pacemaker := _entry(buttons, "Implanted pacemaker")
	assert_not_null(section, "the contents list Chronic conditions")
	assert_not_null(aneurysm, "Aneurysm is a condition entry")
	if section == null or aneurysm == null or pacemaker == null:
		return
	assert_false(aneurysm.visible, "conditions are hidden until their section is open")
	assert_true(section.text.begins_with("☞"), "the section points at the patient's condition inside it")
	section.pressed.emit()
	assert_true(aneurysm.visible and pacemaker.visible, "opening the section lists its conditions")
	assert_true(aneurysm.text.contains("☞"), "the patient's condition is pointed at")
	assert_false(pacemaker.text.contains("☞"), "other conditions are not")
	aneurysm.pressed.emit()
	var page := view.find_children("*", "RichTextLabel", true, false)[0] as RichTextLabel
	assert_string_contains(page.text, "Known history of aneurysm", "the entry shows the chart line to look for")
	assert_true(aneurysm.visible, "the section stays open while one of its conditions is read")
	_entry(buttons, "1. Preoperative preparation").pressed.emit()
	assert_false(aneurysm.visible, "opening another page closes the section")


func _entry(buttons: Array[Button], title: String) -> Button:
	for button in buttons:
		if (button.get_meta("page") as ManualPage).title == title:
			return button
	return null
