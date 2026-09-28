class_name QteView
extends Control
## Turning the patient: press the shown movement keys in time. A wrong key is a critical fail.

var _sequence: PackedStringArray
var _window: float
var _index := 0
var _timer := 0.0
var _hits := 0
var _critical := false
var _done: Callable
var _label: Label
var _bar: ProgressBar


func start(sequence: PackedStringArray, window: float, done: Callable) -> void:
	_sequence = sequence
	_window = window
	_done = done
	_timer = window
	set_anchors_preset(Control.PRESET_CENTER)
	var box := Ui.vbox(8)
	box.add_child(Ui.label("TURN THE PATIENT, TOGETHER", 26, Ui.PIP))
	_label = Ui.label("", 64, Ui.INK)
	_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	box.add_child(_label)
	_bar = Ui.bar(Ui.PIP)
	_bar.custom_minimum_size = Vector2(400, 14)
	box.add_child(_bar)
	var p := Ui.panel(box)
	p.position = Vector2(-240, -120)
	add_child(p)
	_show_key()


func _process(delta: float) -> void:
	_timer -= delta
	_bar.value = _timer / _window
	if _timer <= 0.0:
		_advance(false)


func _unhandled_input(event: InputEvent) -> void:
	if not (event is InputEventKey and event.is_pressed() and not event.is_echo()):
		return
	get_viewport().set_input_as_handled()
	if event.is_action(_sequence[_index]):
		_advance(true)
	elif _is_qte_key(event):
		_critical = true
		_advance(false)


func _advance(hit: bool) -> void:
	_hits += 1 if hit else 0
	_index += 1
	_timer = _window
	if _index >= _sequence.size():
		_done.call(_hits, _critical)
		queue_free()
	else:
		_show_key()


func _show_key() -> void:
	_label.text = InputActions.binding_text(_sequence[_index])


static func _is_qte_key(event: InputEvent) -> bool:
	return Array(Surgery.QTE_KEYS).any(func(action: String) -> bool: return event.is_action(action))
