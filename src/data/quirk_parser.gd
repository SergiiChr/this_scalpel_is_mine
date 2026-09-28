class_name QuirkParser
extends RefCounted
## Reads the markdown quirk sheets.
## "## id" starts an entry, "- key: value" lines fill it, everything else is ignored.


static func parse(path: String, kind: QuirkDef.Kind) -> Dictionary:
	var result: Dictionary = {}
	var current: QuirkDef = null
	for raw: String in FileAccess.get_file_as_string(path).split("\n"):
		var line := raw.strip_edges()
		if line.begins_with("## "):
			current = QuirkDef.new(line.substr(3).strip_edges(), kind)
			result[current.id] = current
		elif current != null and line.begins_with("- ") and ":" in line:
			var sep := line.find(":")
			current.set_field(line.substr(2, sep - 2).strip_edges(), line.substr(sep + 1).strip_edges())
	for quirk: QuirkDef in result.values():
		quirk.resolve_icon(path.get_base_dir())
	return result
