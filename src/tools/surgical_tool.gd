class_name SurgicalTool
extends RigidBody3D
## A grabbable item. Physics runs on the host only, clients get transforms from ToolManager.
## The grip is at the origin and the tip at -Z * length.

## CARRIED: pinched at another tool's tip (a cotton pad in forceps), holder is that tool's uid.
enum State { FREE, HELD, BELT, STANDING, INSIDE, CONSUMED, CARRIED }

const TOOL_LAYER := 8
const IODINE_COLOR := Color(0.55, 0.3, 0.12)
## Liquid that is all blood. A mix tints toward it by its share of blood.
const BLOOD_COLOR := Color(0.5, 0.03, 0.04)
## A hung IV bag comes with this much fluid (ml), with room left in it for drugs pushed in.
const DRIP_FLUID := 500.0
## Smallest size (meters) a tool counts as across for how hard it is to turn, see setup().
const MIN_TURNING_SIZE := 0.04
## How thick a tourniquet's band is where it wraps a limb.
const BAND_THICKNESS := 0.012
## Seconds a spreader takes to go down into a cut once set (dig_to()).
const DIG_TIME := 0.2

var uid: int
var def: ToolDef
var state := State.FREE
## Peer id of whoever holds it (HELD, BELT) or last held it.
var holder := 0
## Hand index while HELD, belt slot while on the BELT.
var slot := -1
var sterile := true
## Fell on the floor: visibly dirty. Needs the sink before the sanitizer can make it sterile again.
var soiled := false
var charges := -1
## How bloody the working end is (0..1), for everyone. Host-side exposure builds up in blood_exposure.
var blood := 0.0
var blood_exposure := 0.0
## How full of liquid it is (0..1): iodine in the dish, soaked into a cotton pad, a syringe, vial or kidney dish.
## Exact on the host, in steps elsewhere (a syringe, vial or kidney dish is exact everywhere, see ml).
var fill := 0.0
## ml of liquid in a syringe, vial or kidney dish, ml of air drawn into a syringe and the share of the liquid that is
## blood and iodine (0..1). Exact on every peer: the host sends changes (ToolManager.add_liquid() and transfer()).
var ml := 0.0
var air := 0.0
var red := 0.0
var iodine := 0.0
## Host only: how much of each drug is in the liquid (drug id -> amount in its unit, "blood" in ml).
## A syringe drawn from two vials holds a mix.
var contents: Dictionary = {}
## Host only, the IV drip: ml pushed into the bag that haven't run down the line yet (they went in by the port at its
## bottom, where the line leaves it, so they run before the bag's own fluid), with the drugs in contents, and ml run
## since debug mode last told and since the bolus started running.
var bolus := 0.0
var dripped_ml := 0.0
var dripped_total := 0.0
## Host only, for debug mode: ml a syringe pushed out since its needle went where it is now, the drugs in it and where
## that is ("the vein", "the IV bag"), told once the needle is somewhere else (ToolActions.report_pushed()).
var pushed_ml := 0.0
var pushed_drugs: Array[String] = []
var pushed_into := ""

# Host-side use state, see ToolActions.
var grip_info: Dictionary = {}
var lowered_before := false
var trigger_before := false
var pressed_before := false
var level_before := 0
var stroke := 0
## Deepest level this stroke's blade point has been pressed in at (see ToolActions, action "cut").
var stabbed_level := 0
var last_uv := Vector2(-1, -1)
var last_tip := Vector3.INF
var charge_time := 0.0
## Wiping time not painted yet, and where it was last painted (see ToolActions._gather()).
var paint_dt := 0.0
var paint_uv := Vector2(-1, -1)
var reported: Dictionary = {}
## Host: seconds since a tool lying on the skin last checked it still lies on top of it (ToolManager._keep_on_top()).
var on_top_check := 0.0

## A needle's running suture: the live thread's id (0 for none), its tension (the wheel) and the layer it's in.
## Each Use tool press makes one hole, at where the needle last rested on the patient (-1, -1 for nowhere): suture_hold
## is how long it's been held, suture_press_used that it already did something (tied off, sewed an internal injury).
var suture_thread := 0
var suture_tension := 1.15
var suture_layer := TissueSim.Depth.NONE
var suture_hold := 0.0
var suture_at := Vector2(-1, -1)
var suture_press_used := false

## A spreader's opening (meters between its tips, the wheel) and whether it's set in a wound, on every peer (see
## ToolManager.sync_spread()). Set in a wound it stays where it went in, held or not.
var spread := ToolActions.SPREAD_RANGE.x:
	set(value):
		spread = value
		_animator.open_to(value, -def.length)
var in_wound := false
## Where a spreader set just now eases down from and to, and how far it is along (1 there).
var _dig_from := Transform3D()
var _dig_to := Transform3D()
var _dig := 1.0

## The model's box in the tool's own space (the grip at the origin).
var bounds := AABB()
## The band around a limb while this tool is wrapped around one (a tourniquet), see wrap_around().
var band: MeshInstance3D = null

var _model: Node3D
var _animator := ToolAnimator.new()
## This tool's own copies of its toon materials, made the first time it needs to look different from the rest.
var _own_materials: Array[ShaderMaterial] = []


func setup(tool_uid: int, tool_def: ToolDef) -> void:
	uid = tool_uid
	def = tool_def
	name = "Tool%d_%s" % [uid, def.id]
	sterile = def.sterile
	charges = def.charges
	mass = 1.2 if def.size == "heavy" else 0.2
	collision_layer = TOOL_LAYER
	collision_mask = 1 | 2 | 4 | TOOL_LAYER
	continuous_cd = true
	contact_monitor = true
	max_contacts_reported = 2
	_model = ToolModel.build(def, self)
	# The collision box wraps the model itself, so a bag or a flask rests on the tray instead of sinking into it.
	bounds = _model_bounds()
	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = bounds.size.max(Vector3.ONE * 0.006)
	shape.shape = box
	shape.position = bounds.get_center()
	add_child(shape)
	# A thin blade has next to no inertia about its length, so contacts would set it spinning and it would never
	# settle, rocking half into the tray. Treat every tool as at least a few centimeters thick for turning.
	var turning := bounds.size.max(Vector3.ONE * MIN_TURNING_SIZE)
	inertia = mass / 12.0 * Vector3(turning.y * turning.y + turning.z * turning.z, turning.x * turning.x + turning.z * turning.z, turning.x * turning.x + turning.y * turning.y)
	angular_damp = 1.0
	_animator.setup(_model, def.action)
	if def.action == "spread":
		_animator.open_to(spread, -def.length)
	# Vials come full of their drug, the IV drip with a bag of plain fluid.
	if def.action == "vial":
		ml = def.volume
		contents[def.drug] = def.volume * def.concentration
	elif def.action == "drip":
		ml = DRIP_FLUID
	fill = ml / def.volume if def.volume > 0.0 else fill
	if def.volume > 0.0:
		show_liquid()
	freeze_mode = RigidBody3D.FREEZE_MODE_KINEMATIC
	freeze = not multiplayer.is_server()
	if def.grip == "needle":
		_animator.animate(false, true, 0.0)


func _process(delta: float) -> void:
	var active := false
	# A suture needle is supplied already locked in its holder; it must not float between open jaws while idle.
	var closed := state == State.STANDING or def.grip == "needle"
	if state == State.HELD and Surgery.current and Surgery.current.surgeons.has(holder):
		var hand: SurgeonHand = (Surgery.current.surgeons[holder] as Surgeon).hands[slot]
		active = ToolActions.in_use(def.action, hand.lowered, hand.trigger, hand.level)
		closed = hand.attached or def.grip == "needle"
	_animator.animate(active, closed, delta)
	if _dig < 1.0:
		_dig = minf(_dig + delta / DIG_TIME, 1.0)
		global_transform = _dig_from.interpolate_with(_dig_to, ease(_dig, 0.4))
	if blood > 0.0:
		for mat in _own_materials:
			mat.set_shader_parameter("coat_inverse", Projection(global_transform.affine_inverse()))


## The model's bounding box in the tool's own space. Falls back to a thin box along the tool if there's no model.
func _model_bounds() -> AABB:
	var all := AABB(Vector3(-def.width * 0.5, -def.width * 0.3, -def.length), Vector3(def.width, def.width * 0.6, def.length))
	var first := true
	for node in _model.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		var box := (global_transform.affine_inverse() * mesh.global_transform) * mesh.get_aabb()
		all = box if first else all.merge(box)
		first = false
	return all


## Set in a cut, a spreader goes down into it to `pose`, its points as deep as the cut goes, so the arms show how deep
## that is.
func dig_to(pose: Transform3D) -> void:
	_dig_from = global_transform
	_dig_to = pose
	_dig = 0.0


func tip_position() -> Vector3:
	return global_transform * Vector3(0, 0, -def.length)


## The name the HUD shows. Syringes carry no drug name, only whether there's anything in them.
func label() -> String:
	if def.action == "syringe":
		return "%s (%s)" % [def.name, "full" if ml > 0.0 else "empty"]
	return def.name


func is_improvised() -> bool:
	return def.improvised


## Germaphobe quirk: unsterile tools glow for this player only.
func show_contamination(visible_to_me: bool) -> void:
	var glow := 0.0 if sterile or not visible_to_me else 1.0
	if glow > 0.0 or not _own_materials.is_empty():
		for mat in _materials():
			mat.set_shader_parameter("contamination", glow)


## Floor dirt shows as heavy grime on the tool for everyone.
func set_soiled(value: bool) -> void:
	soiled = value
	if value or not _own_materials.is_empty():
		for mat in _materials():
			mat.set_shader_parameter("grime", 0.95 if value else mat.get_meta("grime", 0.1))


## Blood on the working end. Gauze and swabs soak through along their whole length; instruments only near the tip.
func set_blood(amount: float) -> void:
	blood = amount
	if amount > 0.0 or not _own_materials.is_empty():
		var soaks := def.action == "swab"
		for mat in _materials():
			mat.set_shader_parameter("coat", amount)
			mat.set_shader_parameter("coat_length", def.length)
			mat.set_shader_parameter("coat_reach", def.length * (1.2 if soaks else 0.12 + 0.3 * amount))
			mat.set_shader_parameter("coat_inverse", Projection(global_transform.affine_inverse()))


## Iodine soaked into it tints the whole tool (a cotton pad).
func show_fill(amount: float) -> void:
	for mat in _materials():
		if not mat.has_meta("albedo"):
			mat.set_meta("albedo", mat.get_shader_parameter("albedo"))
		mat.set_shader_parameter("albedo", (mat.get_meta("albedo") as Color).lerp(IODINE_COLOR, minf(amount * 2.0, 1.0)))


## A syringe, vial or dish shows exactly what's in it (ml, air, red), tinted toward blood by its share of blood.
## In a syringe the air sits at the needle end (it rises there, so a push lets it out first), the liquid behind it
## and the plunger right behind the liquid. A vial's "Level" stretches from its end, the kidney dish's "Pool" rises,
## the iodine dish's "Liquid" spreads.
func show_liquid() -> void:
	var amount := ml / def.volume
	var level := _model.find_child("Level", true, false) as MeshInstance3D
	if level:
		level.visible = amount > 0.0
		level.scale = Vector3(1.0, 1.0, maxf(amount, 0.001))
		if not level.has_meta("rest"):
			level.set_meta("rest", level.position)
		level.position = level.get_meta("rest") + Vector3(0, 0, level.get_aabb().size.z * air / def.volume)
		_animator.fill = (ml + air) / def.volume
	var pool := _model.find_child("Pool", true, false) as MeshInstance3D
	if pool:
		# The dish widens upward: the top of the pool keeps to its wall at every level.
		var wide := lerpf(0.88, 1.0, amount)
		pool.visible = amount > 0.0
		pool.scale = Vector3(wide, maxf(amount, 0.001), wide)
	var liquid := _model.find_child("Liquid", true, false) as MeshInstance3D
	if liquid:
		liquid.visible = amount > 0.0
		liquid.scale = Vector3.ONE * lerpf(0.6, 1.0, amount)
	for part in [level, pool, liquid]:
		if part:
			_tint_liquid(part)


## The liquid part gets its own material the first time it holds blood or iodine, then follows their share in it.
func _tint_liquid(part: MeshInstance3D) -> void:
	var mat := part.get_surface_override_material(0) as ShaderMaterial
	if mat == null or red <= 0.0 and iodine <= 0.0 and not mat.has_meta("albedo"):
		return
	if not mat.has_meta("albedo"):
		mat = mat.duplicate() as ShaderMaterial
		mat.set_meta("albedo", mat.get_shader_parameter("albedo"))
		part.set_surface_override_material(0, mat)
	# Blood and iodine are opaque: a little already colors the whole liquid, so the tint rises fast at first.
	var color := (mat.get_meta("albedo") as Color).lerp(IODINE_COLOR, 1.0 - pow(1.0 - iodine, 3.0))
	mat.set_shader_parameter("albedo", color.lerp(BLOOD_COLOR, 1.0 - pow(1.0 - red, 3.0)))


## The tool your hand would pick up glows faintly (local player only).
func set_highlight(on: bool) -> void:
	for mat in _materials():
		mat.set_shader_parameter("emission_color", Color(0.25, 0.3, 0.22) if on else Color.BLACK)


## Toon materials are shared between tools, so the first per-tool change swaps in copies of its own.
func _materials() -> Array[ShaderMaterial]:
	if _own_materials.is_empty():
		_own_materials = ModelSlot.own_materials(_model)
		for mat in _own_materials:
			mat.set_meta("grime", mat.get_shader_parameter("grime"))
	return _own_materials


## Wrapped around a limb (a tourniquet): the tool's own model gives way to a band around it, snug on the skin.
## The tool itself sits on top of the band, where a hand reaches for it to take it off again.
func wrap_around(center: Vector3, axis: Vector3, radius: float) -> void:
	unwrap()
	band = MeshInstance3D.new()
	band.name = "Band"
	var torus := TorusMesh.new()
	torus.inner_radius = radius
	torus.outer_radius = radius + BAND_THICKNESS
	torus.rings = 32
	band.mesh = torus
	band.material_override = Materials.toon(def.color, 0.2)
	band.top_level = true
	add_child(band)
	# A torus turns about its Y axis.
	var side := axis.cross(Vector3.UP).normalized()
	band.global_transform = Transform3D(Basis(side, axis, side.cross(axis)), center)
	var up := (Vector3.UP - axis * axis.dot(Vector3.UP)).normalized()
	global_transform = Transform3D(Basis(side, up, side.cross(up)) if side.length_squared() > 0.5 else global_basis, center + up * (radius + BAND_THICKNESS))
	_model.visible = false


func unwrap() -> void:
	if band:
		band.queue_free()
		band = null
	_model.visible = true


func set_state(new_state: State, new_holder: int, new_slot: int) -> void:
	state = new_state
	holder = new_holder
	slot = new_slot
	if state != State.STANDING:
		unwrap()
	var physical := state == State.FREE
	visible = state != State.CONSUMED
	# Left holding onto the patient (set, clamped, hooked), a tool doesn't keep hands or other tools off what's under it.
	var on_patient := state == State.STANDING and not def.fixed
	collision_layer = TOOL_LAYER if state in [State.FREE, State.STANDING, State.INSIDE] and not on_patient else 0
	freeze = not (physical and multiplayer.is_server())
	if state != State.HELD:
		lowered_before = false
		trigger_before = false
		pressed_before = false
		level_before = 0
		last_tip = Vector3.INF
