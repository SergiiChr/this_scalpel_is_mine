class_name ManualPage
extends RefCounted
## One page of the in-game manual, loaded from data/manual/*.txt.
## First line "tags: a, b" lists patient effect keys and drug ids that Divine knowledge marks the page for.
## Second line "title: ..." is the heading. The rest is BBCode, with two additions handled by ManualView:
## a line starting with "## " is a numbered section heading, one starting with "> " a hand written note.
## A folder named like the page file (17_conditions.txt, 17_conditions/) holds its sub-pages, listed by title.

var title: String
var tags: PackedStringArray
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
	page.body = "\n".join(lines.slice(start)).strip_edges()
	var dir := path.get_basename()
	for file in DirAccess.get_files_at(dir) if DirAccess.dir_exists_absolute(dir) else PackedStringArray():
		if file.get_extension() == "txt":
			page.children.append(load_file(dir.path_join(file)))
	page.children.sort_custom(func(a: ManualPage, b: ManualPage) -> bool: return a.title < b.title)
	return page


## The page or one of its sub-pages is tagged with one of the keys.
func matches(keys: PackedStringArray) -> bool:
	return Array(tags).any(func(tag: String) -> bool: return tag in keys) or children.any(func(c: ManualPage) -> bool: return c.matches(keys))
