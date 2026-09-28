class_name Modifiers
extends RefCounted
## Effect values stacked from quirks.
## Keys ending in "_mult" multiply, other numbers add up, text values collect into lists ("a|b").

var values: Dictionary = {}


static func parse_effects(text: String) -> Dictionary:
	var out: Dictionary = {}
	for pair: String in text.split(",", false):
		var kv := pair.split("=")
		if kv.size() != 2:
			continue
		var value := kv[1].strip_edges()
		out[kv[0].strip_edges()] = value.to_float() if value.is_valid_float() else value
	return out


## Builds modifiers from roll dictionaries {"id", "variant"} against a quirk table.
static func from_rolls(rolls: Array, table: Dictionary) -> Modifiers:
	var mods := Modifiers.new()
	for roll: Dictionary in rolls:
		var quirk: QuirkDef = table.get(roll.id)
		if quirk:
			mods.add(quirk.effects(roll.variant))
	return mods


func add(effects: Dictionary) -> void:
	for key: String in effects:
		var value: Variant = effects[key]
		if value is float:
			values[key] = mult(key) * value if key.ends_with("_mult") else num(key) + value
		else:
			var list: PackedStringArray = values.get(key, PackedStringArray())
			list.append_array(str(value).split("|", false))
			values[key] = list


func num(key: String, default: float = 0.0) -> float:
	var value: Variant = values.get(key, default)
	return value if value is float else default


func mult(key: String) -> float:
	return num(key, 1.0)


func flag(key: String) -> bool:
	return num(key) > 0.0


func list(key: String) -> PackedStringArray:
	var value: Variant = values.get(key, PackedStringArray())
	return value if value is PackedStringArray else PackedStringArray()
