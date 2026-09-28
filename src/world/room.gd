class_name Room
extends Node3D
## Builds the place where the surgery happens: geometry, lighting and the stations you walk to.
## Layout per environment lives in LAYOUTS, positions are meters with the table at the origin.
## Props are generated models (tools/assetgen/props.py) loaded through ModelSlot.

const TABLE_HEIGHT := 0.85

const LAYOUTS: Dictionary = {
	"or": {
		"size": Vector3(8.0, 3.2, 7.0),
		"spawns": [Vector3(0.0, 0.0, 0.95), Vector3(0.0, 0.0, -0.95)],
		"tray": Vector3(-1.4, 0.0, 0.0),
		"manual": Vector3(3.3, 0.0, -2.8),
		"card": Vector3(1.05, 0.0, 0.5),
		"bell": Vector3(-3.4, 0.0, 2.7),
		"gloves": Vector3(-3.4, 0.0, 1.9),
		"sanitizer": Vector3(-3.4, 0.0, 1.1),
		"iv": Vector3(0.95, 0.0, 0.85),
		"monitor": Vector3(1.25, 1.55, -0.95),
		"delivery": Vector3(-3.0, 0.0, 2.3),
		"xray": Vector3(-2.3, 0.0, -2.4),
	},
	"ambulance": {
		"size": Vector3(4.2, 2.1, 2.3),
		"spawns": [Vector3(0.0, 0.0, 0.8), Vector3(-0.5, 0.0, -0.8)],
		"tray": Vector3(-1.45, 0.0, 0.6),
		"manual": Vector3(1.8, 0.0, -0.85),
		"card": Vector3(1.0, 0.0, 0.5),
		"gloves": Vector3(1.8, 0.0, 0.85),
		"sanitizer": Vector3(-1.8, 0.0, -0.8),
		"iv": Vector3(0.85, 0.0, -0.75),
		"monitor": Vector3(1.4, 1.4, -1.05),
		"delivery": Vector3(-1.45, 0.0, 0.6),
	},
	"sidewalk": {
		"size": Vector3(14.0, 0.0, 10.0),
		"spawns": [Vector3(0.0, 0.0, 0.95), Vector3(0.0, 0.0, -0.95)],
		"tray": Vector3(-1.4, 0.0, 0.3),
		"manual": Vector3(1.6, 0.0, 1.6),
		"card": Vector3(1.05, 0.0, 0.5),
		"gloves": Vector3(-1.6, 0.0, -1.5),
		"iv": Vector3(0.95, 0.0, 0.85),
		"monitor": Vector3(1.4, 1.1, -1.0),
		"delivery": Vector3(-1.4, 0.0, 0.3),
	},
}

var environment_id := "or"
var layout: Dictionary
var monitor: PatientMonitor
var xray: XrayCart
var _flicker_lights: Array[Light3D] = []


func build(env: String, surgery: Surgery) -> void:
	environment_id = env if LAYOUTS.has(env) else "or"
	layout = LAYOUTS[environment_id]
	_build_environment()
	_build_shell()
	_build_table()
	_build_tray()
	_build_stations(surgery)
	Sfx.play_loop({"or": "fluorescent_buzz", "ambulance": "ambulance_rumble", "sidewalk": "street_ambience"}[environment_id], self)


func spawn_transform(index: int) -> Transform3D:
	var spots: Array = layout.spawns
	var pos: Vector3 = spots[index % spots.size()] + Vector3(0.35 * (index / spots.size()), 0, 0)
	var facing := 0.0 if pos.z > 0.0 else PI
	return Transform3D(Basis(Vector3.UP, facing), pos)


func tray_spots() -> Array[Vector3]:
	var spots: Array[Vector3] = []
	var origin: Vector3 = layout.tray + Vector3(0, 0.93, 0)
	for row in 5:
		for col in 5:
			spots.append(origin + Vector3(-0.25 + col * 0.125, 0.0, -0.28 + row * 0.14))
	return spots


func delivery_spot() -> Vector3:
	return layout.delivery + Vector3(randf_range(-0.1, 0.1), 1.0, randf_range(-0.1, 0.1))


## Flickering room lights, the one cheap trick every horror hospital needs. The surgical lamp stays on.
func flicker(duration: float) -> void:
	for light in _flicker_lights:
		# Remember the steady brightness so a flicker that starts during another one doesn't dim the room for good.
		var energy: float = light.get_meta("energy", light.light_energy)
		light.set_meta("energy", energy)
		if light.has_meta("tween"):
			(light.get_meta("tween") as Tween).kill()
		var tween := create_tween()
		light.set_meta("tween", tween)
		for i in int(duration * 6.0):
			tween.tween_property(light, "light_energy", randf_range(0.0, 0.2) * energy, 0.06)
			tween.tween_property(light, "light_energy", randf_range(0.6, 1.0) * energy, 0.1)
		tween.tween_property(light, "light_energy", energy, 0.1)


func _build_environment() -> void:
	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0.02, 0.025, 0.03) if environment_id != "sidewalk" else Color(0.03, 0.035, 0.06)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(0.3, 0.42, 0.42)
	env.ambient_light_energy = 0.5
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	env.glow_enabled = true
	env.glow_intensity = 0.4
	env.fog_enabled = true
	env.fog_light_color = Color(0.25, 0.32, 0.3)
	env.fog_density = 0.02
	env.ssao_enabled = true
	env.adjustment_enabled = true
	env.adjustment_saturation = 0.85
	var world := WorldEnvironment.new()
	world.environment = env
	add_child(world)

	var size: Vector3 = layout.size
	var indoors := environment_id != "sidewalk"
	# The surgical lamp's light starts just under its lens: from inside the lamp head it would shadow itself.
	var lamp := SpotLight3D.new()
	lamp.name = "SurgicalLamp"
	lamp.position = Vector3(0, _lamp_height() - 0.14, 0)
	lamp.rotation.x = -PI / 2
	lamp.spot_range = 3.0
	lamp.spot_angle = 30.0
	lamp.light_energy = 1.0
	lamp.spot_attenuation = 0.5
	lamp.light_color = Color(1.0, 0.97, 0.9)
	lamp.shadow_enabled = true
	add_child(lamp)
	# Overhead room light: a ceiling panel over the table that lights the whole room from above.
	if indoors:
		var panel := MeshInstance3D.new()
		var box := BoxMesh.new()
		box.size = Vector3(1.4, 0.04, 0.5)
		panel.mesh = box
		panel.material_override = Materials.glow(Color(0.95, 1.0, 0.97))
		panel.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		panel.position = Vector3(0, size.y - 0.03, 0)
		add_child(panel)
	var overhead := SpotLight3D.new()
	overhead.name = "Overhead"
	overhead.position = Vector3(0, size.y - 0.08, 0) if indoors else Vector3(3.0, 4.6, 2.9)
	overhead.rotation.x = -PI / 2 if indoors else -0.75
	# Outside it hangs off the streetlight and leans toward the table.
	overhead.rotation.y = 0.0 if indoors else 0.8
	overhead.spot_range = 12.0
	overhead.spot_angle = 70.0
	overhead.spot_attenuation = 0.3
	overhead.light_energy = 0.9 if indoors else 1.1
	overhead.light_color = Color(0.92, 1.0, 0.95) if indoors else Color(1.0, 0.75, 0.45)
	overhead.shadow_enabled = true
	add_child(overhead)
	_flicker_lights.append(overhead)
	var tubes := [Vector3(-2.0, size.y - 0.2, 1.5), Vector3(2.0, size.y - 0.2, -1.5)] if environment_id == "or" else [Vector3(0, size.y - 0.2, 0)]
	if not indoors:
		tubes = [Vector3(3.0, 4.5, 2.0)]
	for pos: Vector3 in tubes:
		var tube := OmniLight3D.new()
		tube.position = pos
		tube.omni_range = 9.0
		tube.light_color = Color(0.75, 0.95, 0.85) if indoors else Color(1.0, 0.7, 0.35)
		tube.light_energy = 0.6
		add_child(tube)
		_flicker_lights.append(tube)


func _lamp_height() -> float:
	return 2.35 if environment_id == "or" else 1.95


func _build_shell() -> void:
	var size: Vector3 = layout.size
	var floor_color := Color(0.28, 0.3, 0.29) if environment_id != "sidewalk" else Color(0.2, 0.2, 0.21)
	Shapes.slab(self, Vector3(size.x, 0.1, size.z), floor_color, Vector3(0, -0.05, 0), 0.7)
	var floor_body := Shapes.static_box(self, Vector3(size.x, 0.1, size.z), Vector3(0, -0.05, 0))
	floor_body.set_meta("floor", true)
	if environment_id == "sidewalk":
		_build_street(size)
		return
	var wall := Color(0.45, 0.52, 0.5) if environment_id == "or" else Color(0.75, 0.78, 0.8)
	for side: float in [-1.0, 1.0]:
		Shapes.slab(self, Vector3(size.x, size.y, 0.1), wall, Vector3(0, size.y * 0.5, side * size.z * 0.5), 0.6)
		Shapes.static_box(self, Vector3(size.x, size.y, 0.1), Vector3(0, size.y * 0.5, side * size.z * 0.5))
		Shapes.slab(self, Vector3(0.1, size.y, size.z), wall, Vector3(side * size.x * 0.5, size.y * 0.5, 0), 0.6)
		Shapes.static_box(self, Vector3(0.1, size.y, size.z), Vector3(side * size.x * 0.5, size.y * 0.5, 0))
	Shapes.slab(self, Vector3(size.x, 0.1, size.z), wall.darkened(0.5), Vector3(0, size.y, 0), 0.8)


func _build_street(size: Vector3) -> void:
	Shapes.slab(self, Vector3(size.x, 0.15, 2.0), Color(0.35, 0.35, 0.35), Vector3(0, 0.075, -size.z * 0.35), 0.8)
	var pole := ModelSlot.instantiate("props", "streetlight", self)
	pole.position = Vector3(3.0, 0.0, 2.9)
	var coat := {"tint": Materials.toon(Color(0.12, 0.12, 0.14), 0.5), "mask": Materials.toon(Color(0.2, 0.18, 0.16), 0.5), "skin": Materials.toon(Color(0.7, 0.55, 0.45), 0.2)}
	for i in 6:
		var angle := TAU * i / 6.0 + 0.3
		var bystander := Node3D.new()
		bystander.name = "Bystander%d" % i
		add_child(bystander)
		bystander.position = Vector3(cos(angle) * 3.2, 0.0, sin(angle) * 2.6)
		bystander.look_at(Vector3(0, 0, 0))
		ModelSlot.instantiate("surgeon", "body", bystander, coat)
		var head := ModelSlot.instantiate("surgeon", "head", bystander, coat)
		head.position.y = 1.62
		for part_name: String in ["Mask", "Cap"]:
			var part := head.find_child(part_name, true, false) as Node3D
			if part:
				part.visible = false


func _build_table() -> void:
	ModelSlot.instantiate("props", "operating_table", self)
	Shapes.static_box(self, Vector3(2.0, TABLE_HEIGHT, 0.62), Vector3(0, TABLE_HEIGHT * 0.5, 0))
	if environment_id != "or":
		ModelSlot.instantiate("props", "straps", self)
	var lamp := ModelSlot.instantiate("props", "surgical_lamp", self)
	lamp.position = Vector3(0.0, _lamp_height(), 0.0)
	lamp.visible = environment_id != "sidewalk"


func _build_tray() -> void:
	var tray := ModelSlot.instantiate("props", "instrument_tray", self)
	tray.position = layout.tray
	Shapes.static_box(self, Vector3(0.7, 0.05, 0.8), layout.tray + Vector3(0, 0.89, 0))


func _build_stations(s: Surgery) -> void:
	_station("manual", "Read the manual", Vector3(0.6, 1.8, 0.4), s.open_manual)
	_station("card", "Read the patient card", Vector3(0.3, 0.4, 0.2), s.open_card, 0.75)
	if s.scenario.nurse and layout.has("bell"):
		_station("bell", "Ring for the nurse", Vector3(0.5, 1.2, 0.5), s.open_nurse)
	if layout.has("gloves"):
		_station("gloves", "Change gloves", Vector3(0.4, 1.2, 0.4), s.change_gloves)
	if layout.has("sanitizer"):
		_station("sanitizer", "Sanitize held tool", Vector3(0.5, 1.2, 0.5), s.sanitize_tool)
	_station("iv", "Use held drug on the IV line", Vector3(0.4, 2.0, 0.4), s.use_iv)
	monitor = PatientMonitor.new()
	monitor.name = "Monitor"
	add_child(monitor)
	monitor.position = layout.monitor
	monitor.rotation.y = atan2(-monitor.position.x, -monitor.position.z)
	monitor.build()
	Interactable.create(self, "Order blood work", Vector3(0.5, 0.5, 0.3), layout.monitor, s.open_lab)
	for side: float in [-1.0, 1.0]:
		Interactable.create(self, "Turn the patient", Vector3(0.3, 0.3, 0.2), Vector3(-0.35, 1.0, 0.38 * side), s.turn_patient)
	if layout.has("xray"):
		xray = XrayCart.new()
		xray.name = "XrayCart"
		add_child(xray)
		xray.position = layout.xray
		xray.build(s)
	Interactable.create(self, "Talk to the patient", Vector3(0.25, 0.3, 0.3), Vector3(0.92, 1.05, 0.0), s.comfort_patient)


func _station(key: String, prompt: String, size: Vector3, callback: Callable, height: float = 1.0) -> void:
	var pos: Vector3 = layout[key]
	var root := ModelSlot.instantiate("props", key, self)
	root.position = pos
	Interactable.create(self, prompt, size, pos + Vector3(0, height, 0), callback)
