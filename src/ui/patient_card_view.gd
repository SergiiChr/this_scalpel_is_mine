class_name PatientCardView
extends RefCounted
## The chart clipped to the bed. Mixes real conditions with random noise, so reading it carefully matters.

const FIRST_NAMES: PackedStringArray = ["Dale", "Mira", "Tobias", "Irene", "Vic", "Noor", "Gus", "Lena", "Otto", "June", "Rafe", "Ada"]
const LAST_NAMES: PackedStringArray = ["Kowalski", "Hart", "Mendez", "Voss", "Okafor", "Lind", "Brandt", "Szabo", "Moreau", "Reyes"]
const RED_HERRINGS: PackedStringArray = [
	"Allergic to cats.", "Vegetarian.", "Previous appendectomy (2011).", "Reports frequent headaches.",
	"Occasional cannabis use.", "Lactose intolerant.", "Wears contact lenses.", "Recent travel abroad.",
	"Claims to be allergic to 'bad vibes'.", "Night shift worker.", "Takes fish oil supplements.",
	"Has a tattoo reading 'DO NOT RESUSCITATE (JK)'.", "Mild seasonal allergies.", "Former smoker (quit 1 week ago).",
]


static func build(patient: Patient, seed_value: int, on_close: Callable) -> Control:
	var rng := RandomNumberGenerator.new()
	rng.seed = seed_value + 5
	var age := {"child": rng.randi_range(6, 11), "elderly": rng.randi_range(78, 94)}.get(patient.age, rng.randi_range(22, 64)) as int
	var lines := patient.revealed_card_lines()
	var blood := patient.blood_type
	var wrong := Surgery.current.run_mods.flag("card_error") and not Surgery.current.chart_corrected
	if wrong:
		# Chart mix-up: someone else's blood type and allergy, and one real condition missing.
		# Own RNG so the rest of the card (name, age) stays the same once it's corrected.
		var error_rng := RandomNumberGenerator.new()
		error_rng.seed = seed_value + 9
		blood = Patient.BLOOD_TYPES[(Patient.BLOOD_TYPES.find(blood) + 2) % Patient.BLOOD_TYPES.size()]
		if not lines.is_empty():
			lines.remove_at(error_rng.randi_range(0, lines.size() - 1))
		lines.append("Known allergy: %s." % Db.drug(["cefazolin", "morphine", "lidocaine"][error_rng.randi_range(0, 2)]).name)
	for i in rng.randi_range(2, 3):
		lines.append(RED_HERRINGS[rng.randi_range(0, RED_HERRINGS.size() - 1)])
	var shuffled := Array(lines)
	for i in range(shuffled.size() - 1, 0, -1):
		var j := rng.randi_range(0, i)
		var tmp: String = shuffled[i]
		shuffled[i] = shuffled[j]
		shuffled[j] = tmp

	var text := RichTextLabel.new()
	text.bbcode_enabled = true
	text.custom_minimum_size = Vector2(900, 700)
	text.add_theme_color_override("default_color", Ui.PAPER_INK)
	text.add_theme_font_size_override("normal_font_size", 24)
	var paper := StyleBoxFlat.new()
	paper.bg_color = Ui.PAPER
	paper.set_content_margin_all(48)
	text.add_theme_stylebox_override("normal", paper)
	var header := "[font_size=36][b]PATIENT CHART[/b][/font_size]%s\n\n" % ("   [color=#8a1c1c][i](corrected copy)[/i][/color]" if Surgery.current.chart_corrected else "")
	text.text = header + \
		"[b]Name:[/b] %s %s     [b]Age:[/b] %d\n" % [FIRST_NAMES[rng.randi_range(0, FIRST_NAMES.size() - 1)], LAST_NAMES[rng.randi_range(0, LAST_NAMES.size() - 1)], age] + \
		"[b]Blood type:[/b] %s\n\n" % ("unknown, lab pending" if rng.randf() < 0.3 else blood) + \
		"[b]Admission:[/b] %s\n\n" % patient.scenario.complaint + \
		"[b]History and notes:[/b]\n" + "\n".join(shuffled.map(func(l: String) -> String: return "  • " + l))
	var box := Ui.vbox(16)
	box.alignment = BoxContainer.ALIGNMENT_CENTER
	var center := CenterContainer.new()
	center.add_child(text)
	box.add_child(center)
	var close := Ui.button("Clip it back  [Esc]", on_close)
	close.size_flags_horizontal = Control.SIZE_SHRINK_CENTER
	box.add_child(close)
	return Ui.fullscreen(box)
