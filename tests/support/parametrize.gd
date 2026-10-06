extends RefCounted
## Parametrized tests, each value its own case, like pytest.mark.parametrize. A test script declares
##   static func parametrize() -> Dictionary: return {"scenario": ["hand_stitch", "appendectomy"]}
## and a method taking one value, func scenario(id: String). Before the run, every value becomes a case of its own,
## test_scenario_hand_stitch and so on: timed, selected (--case), reported and isolated like a written one.
## GUT's own use_parameters() runs all values inside one case instead.

const CollectedScript := preload("res://addons/gut/collected_script.gd")


## A collected script whose cases include the generated ones: GUT loads this subclass instead of the file.
class Expanded extends CollectedScript:
	var generated: GDScript

	func load_script() -> Variant:
		return generated


## Generates the cases of every collected script that declares parametrize(), in place.
static func expand(gut: Variant) -> void:
	var scripts: Array = gut.get_test_collector().scripts
	for i in scripts.size():
		var collected: CollectedScript = scripts[i]
		var script: GDScript = collected.load_script()
		if collected.inner_class_name != "" or not _declares(script):
			continue
		var params: Dictionary = script.call("parametrize")
		var source := "extends \"%s\"\n" % collected.path
		var names: Array[String] = []
		for method: String in params:
			for value: Variant in params[method]:
				var name := "test_%s_%s" % [method, _slug(value)]
				names.append(name)
				source += "\n\nfunc %s() -> void:\n\tawait %s(%s)\n" % [name, method, var_to_str(value)]
		var generated := GDScript.new()
		generated.source_code = source
		generated.resource_path = collected.path + "::parametrized"
		if generated.reload() != OK:
			gut.logger.error("Couldn't generate the parametrized cases of %s" % collected.path)
			continue
		var expanded := Expanded.new()
		for property in collected.get_property_list():
			if property.usage & PROPERTY_USAGE_SCRIPT_VARIABLE and property.name != "name":
				expanded.set(property.name, collected.get(property.name))
		expanded.generated = generated
		for name in names:
			var test: Variant = GutUtils.CollectedTest.new()
			test.name = name
			expanded.tests.append(test)
		for test: Variant in expanded.tests:
			test.collected_script = weakref(expanded)
		scripts[i] = expanded


static func _declares(script: GDScript) -> bool:
	return script.get_script_method_list().any(func(method: Dictionary) -> bool: return method.name == "parametrize")


## A value as part of a case name: lowercase letters, digits and underscores.
static func _slug(value: Variant) -> String:
	var out := ""
	for c in str(value).to_lower():
		out += c if (c >= "a" and c <= "z") or (c >= "0" and c <= "9") else "_"
	return out
