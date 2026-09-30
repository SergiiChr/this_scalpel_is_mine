class_name Room
extends Node3D
## Builds the place where the surgery happens: geometry, lighting and the stations you walk to.
## Layout per environment lives in LAYOUTS, positions are meters with the table at the origin.
## Props are generated models (tools/assetgen/props.py) loaded through ModelSlot.

const TABLE_HEIGHT := 0.85
## Table top ends along x: the feet lie toward FOOT, the head toward HEAD.
const TABLE_FOOT := -1.3
const TABLE_HEAD := 1.0
## Bottom of the drip chamber on the IV stand model, where the tubing starts.
const IV_DRIP_POINT := Vector3(0.08, 1.6, 0.0)
## Solid footprint (width, height, depth) of props you can put things on and can't walk through.
const STATION_SOLIDS: Dictionary = {
	"bell": Vector3(0.6, 0.9, 0.45),
	"gloves": Vector3(0.6, 0.9, 0.45),
	"sanitizer": Vector3(0.6, 0.9, 0.45),
	"sink": Vector3(0.7, 0.9, 0.5),
	"delivery_tray": Vector3(0.5, 0.92, 0.38),
	"defib_cart": Vector3(0.55, 1.06, 0.45),
}

const LAYOUTS: Dictionary = {
	# A cramped old operating room: about 1.3 m between the table and the cabinets behind you.
	# Stations stand against the walls, so reaching them still means walking away from the table.
	"or": {
		"size": Vector3(5.2, 2.8, 4.4),
		# Right at the table edge: from here both of you reach the middle of the patient.
		"spawns": [Vector3(0.0, 0.0, 0.62), Vector3(0.0, 0.0, -0.62)],
		"tray": Vector3(-1.7, 0.0, 0.0),
		"manual": Vector3(1.9, 0.0, -2.0),
		"card": Vector3(1.05, 0.0, 0.5),
		"bell": Vector3(-2.25, 0.0, 1.92),
		"gloves": Vector3(-1.55, 0.0, 1.92),
		"delivery_tray": Vector3(-0.85, 0.0, 1.95),
		"sink": Vector3(1.5, 0.0, 1.9),
		"sanitizer": Vector3(2.25, 0.0, 1.92),
		"iv": Vector3(0.95, 0.0, 0.85),
		"monitor": Vector3(1.25, 1.55, -0.95),
		"defib_cart": Vector3(0.3, 0.0, -1.95),
		"xray": Vector3(-1.9, 0.0, -1.7),
		# Stations turned to face the room (radians), the rest face +Z.
		"yaw": {"bell": PI, "gloves": PI, "delivery_tray": PI, "sink": PI, "sanitizer": PI},
	},
	"ambulance": {
		"size": Vector3(4.2, 2.1, 2.3),
		"spawns": [Vector3(0.0, 0.0, 0.62), Vector3(-0.5, 0.0, -0.62)],
		"tray": Vector3(-1.72, 0.0, 0.55),
		"manual": Vector3(1.8, 0.0, -0.85),
		"card": Vector3(1.0, 0.0, 0.5),
		"gloves": Vector3(1.8, 0.0, 0.85),
		"sanitizer": Vector3(-1.8, 0.0, -0.8),
		"iv": Vector3(0.85, 0.0, -0.75),
		"monitor": Vector3(1.4, 1.4, -1.05),
		"defib_cart": Vector3(1.8, 0.0, 0.0),
		"yaw": {"defib_cart": -PI / 2},
	},
	"sidewalk": {
		"size": Vector3(14.0, 0.0, 10.0),
		"spawns": [Vector3(0.0, 0.0, 0.62), Vector3(0.0, 0.0, -0.62)],
		"tray": Vector3(-1.7, 0.0, 0.3),
		"manual": Vector3(1.6, 0.0, 1.6),
		"card": Vector3(1.05, 0.0, 0.5),
		"gloves": Vector3(-1.6, 0.0, -1.5),
		"iv": Vector3(0.95, 0.0, 0.85),
		"monitor": Vector3(1.4, 1.1, -1.0),
		"defib_cart": Vector3(-0.6, 0.0, 1.8),
		"yaw": {"defib_cart": PI},
	},
}

var environment_id := "or"
var layout: Dictionary
var monitor: PatientMonitor
var xray: XrayCart
## Tubing from the IV stand to the patient, shown once a line is in.
var iv_line: IvLine
var _iv_stand: Node3D
var _flicker_lights: Array[Light3D] = []
## Over the bell: the order on its way and the nurse's cooldown.
var _nurse_board: Label3D


func build(env: String, surgery: Surgery) -> void:
	environment_id = env if LAYOUTS.has(env) else "or"
	layout = LAYOUTS[environment_id]
	_build_environment()
	_build_shell()
	_build_table()
	_build_tray()
	_build_stations(surgery)
	Sfx.play_loop({"or": "fluorescent_buzz", "ambulance": "ambulance_rumble", "sidewalk": "street_ambience"}[environment_id], self)


func _process(_delta: float) -> void:
	if _nurse_board and Surgery.current:
		_nurse_board.text = nurse_board_text(Surgery.current.status)


## status: the host's last status (Surgery.status).
static func nurse_board_text(status: Dictionary) -> String:
	var order: Array = status.get("order", [])
	if not order.is_empty():
		var done := roundi(order[2] * 10.0)
		return "%s\n%s  %d s" % [order[0], "■".repeat(done) + "□".repeat(10 - done), ceili(order[1])]
	var cooldown: float = status.get("nurse", 0.0)
	return "Nurse is busy for %d s" % ceili(cooldown) if cooldown > 0.0 else "Nurse ready"


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


## On top of the delivery tray, or the instrument tray where there's no nurse (they bring nothing there anyway).
func delivery_spot() -> Vector3:
	if layout.has("delivery_tray"):
		return layout.delivery_tray + Vector3(randf_range(-0.12, 0.12), 1.0, randf_range(-0.08, 0.08))
	return layout.tray + Vector3(randf_range(-0.2, 0.2), 1.0, randf_range(-0.2, 0.2))


## Tools that live on their own station instead of the tray: [[tool id, Transform3D], ...].
## The defibrillator always waits on its cart, whatever the scenario put on the tray.
func station_tools() -> Array:
	if not layout.has("defib_cart"):
		return []
	var yaw: float = layout.get("yaw", {}).get("defib_cart", 0.0)
	var basis := Basis(Vector3.UP, yaw)
	return [["defibrillator", Transform3D(basis, layout.defib_cart + basis * Vector3(0.0, 1.1, 0.12))]]


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
	env.ambient_light_color = Color(0.48, 0.52, 0.54)
	env.ambient_light_energy = 0.3
	# Metals need reflected illumination as well as direct light.
	# A quiet studio-like radiance field stands in for the ceiling and floor; the room backdrop stays opaque.
	var sky_material := ProceduralSkyMaterial.new()
	sky_material.sky_top_color = Color(0.32, 0.38, 0.42)
	sky_material.sky_horizon_color = Color(0.62, 0.66, 0.65)
	sky_material.ground_horizon_color = Color(0.32, 0.36, 0.35)
	sky_material.ground_bottom_color = Color(0.08, 0.10, 0.11)
	var sky := Sky.new()
	sky.sky_material = sky_material
	env.sky = sky
	env.reflected_light_source = Environment.REFLECTION_SOURCE_SKY
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	env.glow_enabled = true
	env.glow_intensity = 0.3
	# Only real light sources glow (lamp lens, screens); lit skin up close must not bloom the whole view white.
	env.glow_hdr_threshold = 1.6
	env.fog_enabled = true
	env.fog_light_color = Color(0.25, 0.32, 0.3)
	env.fog_density = 0.02
	env.ssao_enabled = true
	env.adjustment_enabled = true
	env.adjustment_saturation = 1.0
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
	lamp.light_energy = 0.5
	lamp.spot_attenuation = 0.5
	lamp.light_color = Color(1.0, 0.97, 0.94)
	lamp.shadow_enabled = true
	add_child(lamp)
	# Overhead room light: a ceiling panel over the table that lights the whole room from above.
	if indoors:
		var panel := MeshInstance3D.new()
		var box := BoxMesh.new()
		box.size = Vector3(1.4, 0.04, 0.5)
		panel.mesh = box
		panel.material_override = Materials.glow(Materials.FLUORESCENT)
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
	overhead.light_energy = 0.35 if indoors else 1.0
	overhead.light_color = Color(0.94, 0.98, 1.0) if indoors else Color(1.0, 0.75, 0.45)
	overhead.shadow_enabled = true
	add_child(overhead)
	_flicker_lights.append(overhead)
	var tubes := [Vector3(-size.x * 0.28, size.y - 0.2, size.z * 0.28), Vector3(size.x * 0.28, size.y - 0.2, -size.z * 0.28)] if environment_id == "or" else [Vector3(0, size.y - 0.2, 0)]
	if not indoors:
		tubes = [Vector3(3.0, 4.5, 2.0)]
	for pos: Vector3 in tubes:
		var tube := OmniLight3D.new()
		tube.position = pos
		tube.omni_range = 9.0
		tube.light_color = Materials.FLUORESCENT if indoors else Color(1.0, 0.7, 0.35)
		tube.light_energy = 0.18
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
	var wall := Materials.SURGICAL_GREEN if environment_id == "or" else Color(0.75, 0.78, 0.8)
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
	Shapes.static_box(self, Vector3(TABLE_HEAD - TABLE_FOOT, TABLE_HEIGHT, 0.62), Vector3((TABLE_HEAD + TABLE_FOOT) * 0.5, TABLE_HEIGHT * 0.5, 0))
	if environment_id != "or":
		ModelSlot.instantiate("props", "straps", self)
	var lamp := ModelSlot.instantiate("props", "surgical_lamp", self)
	lamp.position = Vector3(0.0, _lamp_height(), 0.0)
	# The lamp head hangs right under the ceiling light; its shadow would black out the middle of the table.
	for mesh in lamp.find_children("*", "MeshInstance3D", true, false):
		(mesh as MeshInstance3D).cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
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
		_nurse_board = Shapes.label(self, "", (layout.bell as Vector3) + Vector3(0, 1.5, 0), 40)
		_nurse_board.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	if layout.has("gloves"):
		_station("gloves", "Change gloves", Vector3(0.4, 1.2, 0.4), s.change_gloves)
	if layout.has("sanitizer"):
		_station("sanitizer", "Sanitize held tool", Vector3(0.5, 1.2, 0.5), s.sanitize_tool)
	if layout.has("sink"):
		_station("sink", "Wash held tool", Vector3(0.6, 1.2, 0.5), s.wash_tool)
	if layout.has("delivery_tray"):
		_prop("delivery_tray")
	if layout.has("defib_cart"):
		_prop("defib_cart")
	_iv_stand = _station("iv", "Use held drug on the IV line", Vector3(0.4, 2.0, 0.4), s.use_iv)
	iv_line = IvLine.new()
	iv_line.name = "IvLine"
	add_child(iv_line)
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
		xray.bounds = Vector2(layout.size.x, layout.size.z) * 0.5 - Vector2(0.45, 0.45)
		xray.build(s)
	Interactable.create(self, "Talk to the patient", Vector3(0.25, 0.3, 0.3), Vector3(0.92, 1.05, 0.0), s.comfort_patient)


func _station(key: String, prompt: String, size: Vector3, callback: Callable, height: float = 1.0) -> Node3D:
	var root := _prop(key)
	Interactable.create(self, prompt, size, (layout[key] as Vector3) + Vector3(0, height, 0), callback)
	return root


## Runs the IV tubing from the stand's drip chamber to a point on the patient (local to `to`).
func connect_iv(to: Node3D, point: Vector3) -> void:
	iv_line.attach(_iv_stand, IV_DRIP_POINT, to, point)


## Places a layout prop, turned by its yaw, solid if it has a footprint in STATION_SOLIDS.
func _prop(key: String) -> Node3D:
	var pos: Vector3 = layout[key]
	var yaw: float = layout.get("yaw", {}).get(key, 0.0)
	var root := ModelSlot.instantiate("props", key, self)
	root.position = pos
	root.rotation.y = yaw
	if STATION_SOLIDS.has(key):
		var solid: Vector3 = STATION_SOLIDS[key]
		if absf(sin(yaw)) > 0.5:
			solid = Vector3(solid.z, solid.y, solid.x)
		Shapes.static_box(self, solid, pos + Vector3(0, solid.y * 0.5, 0))
	return root
