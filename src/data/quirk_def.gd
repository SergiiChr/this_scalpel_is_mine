class_name QuirkDef
extends RefCounted
## One quirk parsed from data/quirks/*.md.
## Any field can be overridden per variant with a "field.variant" key, see QuirkDef.text().

enum Kind { PATIENT, SURGEON }

var id: String
var kind: Kind
var icon_path: String = ""
var variants: PackedStringArray = []
var _fields: Dictionary = {}


func _init(quirk_id: String, quirk_kind: Kind) -> void:
	id = quirk_id
	kind = quirk_kind


func set_field(key: String, value: String) -> void:
	if key == "variants":
		variants = PackedStringArray(Array(value.split(",", false)).map(func(v: String) -> String: return v.strip_edges()))
	else:
		_fields[key] = value


## Field value for a variant, falling back to the shared value.
func text(field: String, variant: String = "") -> String:
	return str(_fields.get("%s.%s" % [field, variant], _fields.get(field, "")))


func display_name(variant: String = "") -> String:
	var base := text("name", variant)
	return base if variant.is_empty() else "%s (%s)" % [base, variant.capitalize()]


func polarity(variant: String = "") -> String:
	return text("polarity", variant)


func is_exclusive() -> bool:
	return text("exclusive") == "true"


func is_red_herring() -> bool:
	return text("red_herring") == "true"


func effects(variant: String = "") -> Dictionary:
	return Modifiers.parse_effects(text("effects", variant))


func icon() -> Texture2D:
	return load(icon_path) as Texture2D if ResourceLoader.exists(icon_path) else null


## Codex/save key, unique across patient and surgeon quirks.
func unlock_key() -> String:
	return "%s:%s" % ["patient" if kind == Kind.PATIENT else "surgeon", id]


## Turns the markdown link "[x.svg](../../assets/x.svg)" into a res:// path relative to the sheet.
func resolve_icon(sheet_dir: String) -> void:
	var link := text("icon")
	var start := link.find("(")
	var end := link.rfind(")")
	if start != -1 and end > start:
		icon_path = sheet_dir.path_join(link.substr(start + 1, end - start - 1)).simplify_path()
