class_name ToolEffects
extends Node3D
## Short-lived visual effects where a tool works on the body, on every peer (the host sends them, see
## Surgery.effect()). Lasting marks (cuts, burns, stitches, ink) are painted into the wound map instead.
##   smoke   cautery and lighter: a thin plume of tissue smoke rising from the tip
##   dust    saw on bone: a spray of pale bone dust with a little blood
##   spatter blunt impact (mallet): blood droplets thrown up from the skin
##   spark   defibrillator discharge: a blue-white flash and sparks at the paddles, the body jerks
##   bead    injection or catheter: a bead of blood welling up where the needle went in

## Blood beads stay this long (seconds), then dry away.
const BEAD_LIFE := 90.0

var patient: Patient


func play(kind: String, at: Vector3) -> void:
	match kind:
		"smoke":
			_burst(at, _smoke_params())
		"dust":
			_burst(at, _dust_params())
			patient.body.blood.spray(at, 2, 0.6)
		"spatter":
			patient.body.blood.spray(at, 6, 1.0)
		"spark":
			_burst(at, _spark_params())
			_flash(at)
			patient.body.animator.jolt()
		"bead":
			_bead(at)


## One-shot particles that free themselves when done.
func _burst(at: Vector3, params: Dictionary) -> void:
	var particles := CPUParticles3D.new()
	particles.one_shot = true
	particles.explosiveness = params.explosiveness
	particles.amount = params.amount
	particles.lifetime = params.lifetime
	particles.direction = Vector3.UP
	particles.spread = params.spread
	particles.initial_velocity_min = params.speed * 0.5
	particles.initial_velocity_max = params.speed
	particles.gravity = params.gravity
	particles.damping_min = params.get("damping", 0.0)
	particles.damping_max = params.get("damping", 0.0)
	particles.scale_amount_min = params.size * 0.6
	particles.scale_amount_max = params.size
	particles.scale_amount_curve = params.get("grow")
	particles.color_ramp = params.colors
	var quad := QuadMesh.new()
	quad.size = Vector2.ONE
	particles.mesh = quad
	particles.material_override = _particle_material(params.get("glow", false))
	particles.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(particles)
	particles.global_position = at
	particles.emitting = true
	particles.finished.connect(particles.queue_free)


func _particle_material(glow: bool) -> StandardMaterial3D:
	var mat := StandardMaterial3D.new()
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.billboard_mode = BaseMaterial3D.BILLBOARD_PARTICLES
	# Particle scale (the size and growth curves) only survives billboarding with this on.
	mat.billboard_keep_scale = true
	mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	mat.vertex_color_use_as_albedo = true
	mat.albedo_texture = _soft_dot()
	if glow:
		mat.blend_mode = BaseMaterial3D.BLEND_MODE_ADD
	return mat


## A round, soft-edged sprite, made once.
static func _soft_dot() -> GradientTexture2D:
	var texture := GradientTexture2D.new()
	texture.fill = GradientTexture2D.FILL_RADIAL
	texture.fill_from = Vector2(0.5, 0.5)
	texture.fill_to = Vector2(1.0, 0.5)
	texture.width = 32
	texture.height = 32
	var gradient := Gradient.new()
	gradient.set_color(0, Color.WHITE)
	gradient.set_color(1, Color(1, 1, 1, 0))
	texture.gradient = gradient
	return texture


static func _ramp(colors: Array[Color]) -> Gradient:
	var gradient := Gradient.new()
	var offsets := PackedFloat32Array()
	for i in colors.size():
		offsets.append(float(i) / (colors.size() - 1))
	gradient.offsets = offsets
	gradient.colors = PackedColorArray(colors)
	return gradient


static func _grow(from: float, to: float) -> Curve:
	var curve := Curve.new()
	curve.add_point(Vector2(0, from))
	curve.add_point(Vector2(1, to))
	return curve


func _smoke_params() -> Dictionary:
	return {
		"amount": 6, "lifetime": 1.8, "explosiveness": 0.3, "spread": 25.0, "speed": 0.05, "size": 0.05, "damping": 0.02,
		"gravity": Vector3(0.01, 0.06, 0.0), "grow": _grow(0.3, 1.0),
		"colors": _ramp([Color(0.5, 0.5, 0.48, 0.4), Color(0.52, 0.52, 0.5, 0.6), Color(0.6, 0.6, 0.6, 0.0)]),
	}


func _dust_params() -> Dictionary:
	return {
		"amount": 14, "lifetime": 0.7, "explosiveness": 0.8, "spread": 70.0, "speed": 0.5, "size": 0.012,
		"gravity": Vector3(0, -6.0, 0), "grow": _grow(1.0, 0.6),
		"colors": _ramp([Color(0.95, 0.92, 0.82, 1.0), Color(0.9, 0.86, 0.76, 0.0)]),
	}


func _spark_params() -> Dictionary:
	return {
		"amount": 24, "lifetime": 0.3, "explosiveness": 1.0, "spread": 80.0, "speed": 0.9, "size": 0.012, "glow": true,
		"gravity": Vector3(0, -3.0, 0), "grow": _grow(1.0, 0.2),
		"colors": _ramp([Color(0.85, 0.92, 1.0, 1.0), Color(0.4, 0.6, 1.0, 0.0)]),
	}


func _flash(at: Vector3) -> void:
	var light := OmniLight3D.new()
	light.light_color = Color(0.75, 0.85, 1.0)
	light.light_energy = 4.0
	light.omni_range = 1.2
	add_child(light)
	light.global_position = at + Vector3(0, 0.05, 0)
	var fade := create_tween()
	fade.tween_property(light, "light_energy", 0.0, 0.18)
	fade.tween_callback(light.queue_free)


## A small dome of blood on the skin, stuck to the body so it follows turns and breathing.
func _bead(at: Vector3) -> void:
	var bead := MeshInstance3D.new()
	bead.name = "BloodBead"
	var dome := SphereMesh.new()
	dome.radius = 1.0
	dome.height = 2.0
	dome.radial_segments = 12
	dome.rings = 4
	dome.is_hemisphere = true
	bead.mesh = dome
	bead.material_override = Materials.blood_pool()
	bead.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var body := patient.body
	var root := body.root()
	var normal := root.global_basis.y.normalized()
	var probe := body.probe(at)
	if probe.zone == "site":
		# The needle tip is under the skin. Its puncture is directly above it on the drawn skin, riding the site.
		root = body.site
		var local := root.to_local(at)
		local.y = body.skin_height(probe.uv)
		at = root.to_global(local)
		normal = root.global_basis.y.normalized()
	elif probe.zone != "cavity":
		var query := PhysicsRayQueryParameters3D.create(at + Vector3.UP * 0.03, at + Vector3.DOWN * 0.03, PatientBody.SURFACE_LAYER)
		var hit := get_world_3d().direct_space_state.intersect_ray(query)
		if not hit.is_empty():
			at = hit.position
			normal = hit.normal
		if str(probe.get("part", "")).begins_with("arm"):
			root = body.iv_site(at).node
	root.add_child(bead, true)
	bead.global_position = at
	var across := root.global_basis.x.slide(normal).normalized()
	if across.is_zero_approx():
		across = root.global_basis.z.slide(normal).normalized()
	bead.global_basis = Basis(across, normal, across.cross(normal))
	bead.scale = Vector3(0.002, 0.0015, 0.002)
	var grow := create_tween()
	grow.tween_property(bead, "scale", Vector3(0.0035, 0.0025, 0.0035), 2.0)
	grow.tween_interval(BEAD_LIFE)
	grow.tween_property(bead, "scale", Vector3(0.002, 0.0004, 0.002), 10.0)
	grow.tween_callback(bead.queue_free)
