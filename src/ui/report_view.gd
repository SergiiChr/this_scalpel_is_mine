class_name ReportView
extends RefCounted
## Post-op report: pass/fail, stars, what you did, what happened to the patient afterwards.


static func build(report: Dictionary, scenario: ScenarioDef) -> Control:
	var box := Ui.vbox(10)
	var success: bool = report.success
	box.add_child(Ui.label("POST-OP REPORT: %s" % scenario.title.to_upper(), 30, Ui.PIP))
	var verdict := "PATIENT SURVIVED" if success else "FAILED: %s" % report.reason
	box.add_child(Ui.label(verdict, 26, Ui.GOOD if success else Ui.ALERT))
	box.add_child(Ui.label("%s   score %d   time %d:%02d" % ["★".repeat(report.stars) + "☆".repeat(3 - report.stars), report.score, int(report.time) / 60, int(report.time) % 60], 24))

	var details := Ui.vbox(4)
	details.add_child(Ui.label("During the operation", 20, Ui.PIP))
	for event: Array in report.events:
		details.add_child(Ui.label("%+4d   %s%s" % [event[1], event[0], "  x%d" % event[2] if event[2] > 1 else ""], 16, Ui.GOOD if event[1] >= 0 else Ui.ALERT, true))
	if not report.consequences.is_empty():
		details.add_child(Ui.label("Afterwards", 20, Ui.PIP))
		for line: Array in report.consequences:
			details.add_child(Ui.label("%+4d   %s" % [line[1], line[0]], 16, Ui.GOOD if line[1] >= 0 else Ui.ALERT, true))
	details.add_child(Ui.label("The patient had", 20, Ui.PIP))
	if report.patient_quirks.is_empty():
		details.add_child(Ui.label("Nothing unusual. Lucky.", 16, Ui.DIM))
	for roll: Dictionary in report.patient_quirks:
		details.add_child(Ui.quirk_line(Db.quirk(QuirkDef.Kind.PATIENT, roll.id), roll.variant, false))
	box.add_child(Ui.scroll(details))

	var buttons := Ui.hbox(12)
	if Net.is_host():
		buttons.add_child(Ui.button("New run (back to lobby)", Net.return_to_lobby))
	buttons.add_child(Ui.button("Main menu", Net.back_to_menu))
	box.add_child(buttons)
	var p := Ui.panel(box)
	p.custom_minimum_size = Vector2(1000, 800)
	var center := CenterContainer.new()
	center.add_child(p)
	return Ui.fullscreen(center)
