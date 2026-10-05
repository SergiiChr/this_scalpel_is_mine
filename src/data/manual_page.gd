class_name ManualPage
extends RefCounted
## One page of the in-game manual, loaded from data/manual/*.txt.
## First line "tags: a, b" lists patient effect keys and drug ids that Divine knowledge marks the page for.
## Second line "title: ..." is the heading. The rest is BBCode, with two additions handled by ManualView:
## a line starting with "## " is a numbered section heading, one starting with "> " a hand written note.
## A folder named like the page file (17_conditions.txt, 17_conditions/) holds its sub-pages, listed by title.
## Game numbers are written as "{expression}" and worked out when the page loads, so the manual follows the game:
## constants of any class ("{Patient.HIGH_PRESSURE}"), drugs ("{drug.diazepam.duration / 60}"), tools
## ("{tool.heavy_saw.delay}") and patient quirk effects ("{quirk.heart_weak.arrest_mult}", a variant joined by "_").

const NUMBER := "\\{([^{}]+)\\}"

## Class name -> its constants, for the classes pages have used so far.
static var _constants: Dictionary = {}

var title: String
var tags: PackedStringArray
## The page as written, with its "{expression}" numbers.
var source: String
var body: String
var children: Array[ManualPage] = []


static func load_file(path: String) -> ManualPage:
	var page := ManualPage.new()
	var lines := FileAccess.get_file_as_string(path).split("\n")
	var start := 0
	for line in lines.slice(0, 2):
		if line.begins_with("tags:"):
			page.tags = PackedStringArray(Array(line.substr(5).split(",", false)).map(func(t: String) -> String: return t.strip_edges()))
			start += 1
		elif line.begins_with("title:"):
			page.title = line.substr(6).strip_edges()
			start += 1
	page.source = "\n".join(lines.slice(start)).strip_edges()
	page.body = fill_numbers(page.source, path)
	var dir := path.get_basename()
	for file in DirAccess.get_files_at(dir) if DirAccess.dir_exists_absolute(dir) else PackedStringArray():
		if file.get_extension() == "txt":
			page.children.append(load_file(dir.path_join(file)))
	page.children.sort_custom(func(a: ManualPage, b: ManualPage) -> bool: return a.title < b.title)
	return page


## Replaces every "{expression}" with its value. One that can't be worked out is reported and left as written.
static func fill_numbers(text: String, where: String) -> String:
	var out := text
	for found in RegEx.create_from_string(NUMBER).search_all(text):
		var value: Variant = evaluate(found.get_string(1))
		if value == null:
			push_error("Manual: can't work out {%s} in %s" % [found.get_string(1), where])
			continue
		out = out.replace(found.get_string(), _written(value))
	return out


## The value of one expression, or null when it can't be worked out.
static func evaluate(expression: String) -> Variant:
	var names: Array[String] = ["drug", "tool", "quirk"]
	var inputs: Array = [Db.drugs, Db.tools, _quirk_effects()]
	for word: String in RegEx.create_from_string("\\b[A-Z][A-Za-z]+\\b").search_all(expression).map(func(m: RegExMatch) -> String: return m.get_string()):
		if word not in names and _class_constants(word) != null:
			names.append(word)
			inputs.append(_class_constants(word))
	var parsed := Expression.new()
	if parsed.parse(expression, names) != OK:
		return null
	var value: Variant = parsed.execute(inputs, null, false)
	return null if parsed.has_execute_failed() else value


static func _written(value: Variant) -> String:
	if not (value is float or value is int):
		return str(value)
	var number := float(value)
	return str(roundi(number)) if absf(number - roundf(number)) < 0.001 else String.num(number, 2)


static func _class_constants(type_name: String) -> Variant:
	if not _constants.has(type_name):
		for entry: Dictionary in ProjectSettings.get_global_class_list():
			if entry["class"] == type_name:
				_constants[type_name] = (load(entry.path) as Script).get_script_constant_map()
	return _constants.get(type_name)


static func _quirk_effects() -> Dictionary:
	var out: Dictionary = {}
	for quirk: QuirkDef in Db.patient_quirks.values():
		for variant: String in quirk.variants if not quirk.variants.is_empty() else PackedStringArray([""]):
			out[quirk.id + ("_" + variant if variant else "")] = quirk.effects(variant)
	return out


## The page or one of its sub-pages is tagged with one of the keys.
func matches(keys: PackedStringArray) -> bool:
	return Array(tags).any(func(tag: String) -> bool: return tag in keys) or children.any(func(c: ManualPage) -> bool: return c.matches(keys))
