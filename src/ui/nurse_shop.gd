class_name NurseShop
extends RefCounted
## The nurse bell as a shop: categories on the left, the chosen one's items as a list with [-] count [+], and the cart
## on the right. A cart holds up to Nurse.BATCH items, the same one more than once too, sent as one order.
## Built once per surgery and only shown and hidden after that, so ringing the bell doesn't build a page of controls
## mid-surgery. The cart stays between visits until it's ordered or emptied. Place order waits for the nurse to be free.
## Buttons carry metadata ("category", "add", "remove" with a category or tool id; "place") so tests can press them.

## The whole overlay, for the HUD to show and hide.
var view: Control
var _cart: Array[String] = []
var _on_order: Callable
var _subtitle: Label
var _cart_title: Label
var _cart_lines: Label
var _cart_eta: Label
var _place: Button
var _nurse_ready := true
## One list per category, shown while its button is down: {category: VBoxContainer}.
var _lists: Dictionary = {}
## {tool id: [count label, minus, plus]}.
var _rows: Dictionary = {}


## groups: {category: [ToolDef, ...]}, in display order. on_order receives the cart's tool ids, in the order added.
func _init(groups: Dictionary, on_order: Callable, on_close: Callable) -> void:
	_on_order = on_order
	var categories := Ui.vbox(4)
	categories.custom_minimum_size = Vector2(240, 0)
	var picked := ButtonGroup.new()
	var lists := Ui.vbox(0)
	for group: String in groups:
		var b := Button.new()
		b.text = group
		b.toggle_mode = true
		b.toggled.connect(func(on: bool) -> void: (_lists[group] as Control).visible = on)
		b.button_group = picked
		b.alignment = HORIZONTAL_ALIGNMENT_LEFT
		b.set_meta("category", group)
		categories.add_child(b)
		var list := Ui.vbox(4)
		list.hide()
		for def: ToolDef in groups[group]:
			list.add_child(_row(def))
		lists.add_child(list)
		_lists[group] = list
	if categories.get_child_count() > 0:
		(categories.get_child(0) as Button).button_pressed = true

	_cart_title = Ui.label("", 22, Ui.PIP)
	_cart_lines = Ui.label("", 18)
	_cart_lines.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_cart_eta = Ui.label("", 16, Ui.DIM)
	_place = Ui.button("Place order", _order)
	_place.set_meta("place", true)
	var cart := Ui.vbox(8)
	cart.custom_minimum_size = Vector2(300, 0)
	for part: Control in [_cart_title, _cart_lines, _cart_eta, _place, Ui.button("Empty cart", clear)]:
		cart.add_child(part)

	var columns := Ui.hbox(16)
	columns.size_flags_vertical = Control.SIZE_EXPAND_FILL
	columns.add_child(Ui.scroll(categories))
	var items := Ui.scroll(lists)
	items.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	columns.add_child(items)
	columns.add_child(Ui.panel(cart))
	var page := Ui.vbox(6)
	page.add_child(Ui.label("Ring for the nurse", 28, Ui.PIP))
	_subtitle = Ui.label("", 16, Ui.DIM, true)
	page.add_child(_subtitle)
	page.add_child(columns)
	page.add_child(Ui.button("Never mind  [Esc]", on_close))
	var holder := CenterContainer.new()
	var p := Ui.panel(page)
	p.custom_minimum_size = Vector2(1100, 640)
	holder.add_child(p)
	view = Ui.fullscreen(holder, Color(0, 0, 0, 0.6))
	_refresh()


## While it's shown: board is the nurse board's text, nurse_ready whether she takes an order now.
func update(board: String, nurse_ready: bool) -> void:
	_subtitle.text = board
	if nurse_ready != _nurse_ready:
		_nurse_ready = nurse_ready
		_refresh()


func clear() -> void:
	_cart.clear()
	_refresh()


func _row(def: ToolDef) -> Control:
	var row := Ui.hbox(8)
	var name := Ui.label(def.name, 20)
	name.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	row.add_child(name)
	row.add_child(Ui.label("%d s" % def.delay, 18, Ui.DIM))
	var minus := Ui.button(" − ", _change.bind(def.id, false))
	minus.set_meta("remove", def.id)
	var count := Ui.label("", 20)
	count.custom_minimum_size = Vector2(32, 0)
	count.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	var plus := Ui.button(" + ", _change.bind(def.id, true))
	plus.set_meta("add", def.id)
	for part: Control in [minus, count, plus]:
		row.add_child(part)
	_rows[def.id] = [count, minus, plus]
	return row


func _change(id: String, add: bool) -> void:
	if add and _cart.size() < Nurse.BATCH:
		_cart.append(id)
	elif not add and _cart.has(id):
		_cart.remove_at(_cart.rfind(id))
	_refresh()


func _order() -> void:
	_on_order.call(PackedStringArray(_cart))
	clear()


func _refresh() -> void:
	var full := _cart.size() >= Nurse.BATCH
	for id: String in _rows:
		var count := _cart.count(id)
		(_rows[id][0] as Label).text = str(count)
		(_rows[id][1] as Button).disabled = count == 0
		(_rows[id][2] as Button).disabled = full
	var longest := 0.0
	var lines := PackedStringArray()
	for id: String in _cart:
		longest = maxf(longest, Db.tool(id).delay)
		var line := "%s  ×%d" % [Db.tool(id).name, _cart.count(id)]
		if not lines.has(line):
			lines.append(line)
	_cart_lines.text = "\n".join(lines) if lines else "Nothing yet."
	_cart_title.text = "Cart  %d / %d" % [_cart.size(), Nurse.BATCH]
	_cart_eta.text = "Ready in about %d s" % longest if longest > 0.0 else ""
	_place.disabled = _cart.is_empty() or not _nurse_ready
