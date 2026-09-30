class_name Materials
extends RefCounted
## Shared materials. Everything goes through here so the look can be tuned in one place.

const TOON := preload("res://assets/shaders/toon.gdshader")
const OUTLINE := preload("res://assets/shaders/outline.gdshader")
const FLESH := preload("res://assets/shaders/flesh.gdshader")
const SKIN := preload("res://assets/shaders/skin.gdshader")
const TISSUE_LAYER := preload("res://assets/shaders/tissue_layer.gdshader")
const SURGICAL_DRAPE := preload("res://assets/shaders/surgical_drape.gdshader")

const SKIN_TONES: Array[Color] = [
	Color(0.87, 0.7, 0.6), Color(0.78, 0.58, 0.45), Color(0.6, 0.42, 0.3), Color(0.42, 0.28, 0.2),
]

## Hospital palette. Surgical green for walls and linens (it's easy on eyes that stare at red all day),
## ceil blue for the second surgeon's scrubs, one cool fluorescent white for every room light but the surgical lamp.
const SURGICAL_GREEN := Color(0.4, 0.55, 0.5)
const PATIENT_GOWN := Color(0.52, 0.64, 0.6)
const SCRUBS: Array[Color] = [Color(0.22, 0.4, 0.36), Color(0.26, 0.38, 0.52)]
const FLUORESCENT := Color(0.88, 1.0, 0.94)

static var _cache: Dictionary = {}
static var _imported: Dictionary = {}


## Preserve authored glTF inputs; family settings only tune the common hospital finish.
## Cached by the resource itself so unrelated textured materials never alias by color.
static func imported(base: BaseMaterial3D) -> ShaderMaterial:
	if _imported.has(base):
		return _imported[base]
	var family := base.resource_name
	var metal := base.metallic > 0.5
	var mat := toon_unique(base.albedo_color, 0.035 if metal else 0.06, not metal, base.roughness)
	mat.set_shader_parameter("metallic", base.metallic)
	mat.set_shader_parameter("specular_strength", base.metallic_specular)
	mat.set_shader_parameter("uv_scale", Vector2(base.uv1_scale.x, base.uv1_scale.y))
	mat.set_shader_parameter("uv_offset", Vector2(base.uv1_offset.x, base.uv1_offset.y))
	for entry: Array in [["albedo_texture", base.albedo_texture], ["roughness_texture", base.roughness_texture], ["metallic_texture", base.metallic_texture]]:
		if entry[1] != null:
			mat.set_shader_parameter(entry[0], entry[1])
	mat.set_shader_parameter("roughness_channel", _channel(base.roughness_texture_channel))
	mat.set_shader_parameter("metallic_channel", _channel(base.metallic_texture_channel))
	if base.normal_enabled and base.normal_texture:
		mat.set_shader_parameter("normal_enabled", true)
		mat.set_shader_parameter("normal_texture", base.normal_texture)
		mat.set_shader_parameter("normal_scale", base.normal_scale)
	if base.ao_enabled and base.ao_texture:
		mat.set_shader_parameter("ao_texture", base.ao_texture)
		mat.set_shader_parameter("ao_channel", _channel(base.ao_texture_channel))
		mat.set_shader_parameter("ao_light_affect", base.ao_light_affect)
	if base.emission_enabled:
		mat.set_shader_parameter("emission_color", base.emission * base.emission_energy_multiplier)
		if base.emission_texture:
			mat.set_shader_parameter("emission_texture", base.emission_texture)
	match family:
		"glove":
			mat.set_shader_parameter("albedo", Color(0.22, 0.38, 0.52))
			mat.set_shader_parameter("roughness", 0.72)
			mat.set_shader_parameter("specular_strength", 0.25)
			mat.set_shader_parameter("grime", 0.015)
			mat.set_shader_parameter("surface_detail", 0.10)
			mat.next_pass = null
		"fabric_white", "fabric_dark", "gown", "mask", "cotton":
			mat.set_shader_parameter("roughness", 0.92)
			mat.set_shader_parameter("specular_strength", 0.18)
			mat.set_shader_parameter("surface_detail", 0.16)
			mat.next_pass = null
		"skin":
			mat.set_shader_parameter("grime", 0.025)
			mat.set_shader_parameter("specular_strength", 0.3)
			mat.set_shader_parameter("rim_strength", 0.0)
	_imported[base] = mat
	return mat


static func _channel(channel: int) -> Vector4:
	return [Vector4(1, 0, 0, 0), Vector4(0, 1, 0, 0), Vector4(0, 0, 1, 0), Vector4(0, 0, 0, 1), Vector4(0.333333, 0.333333, 0.333334, 0)][clampi(channel, 0, 4)]


## Cached per color/settings, so don't modify the result. Use toon_unique() for per-object tweaks.
static func toon(color: Color, grime: float = 0.25, outline: bool = true, roughness: float = 0.7) -> ShaderMaterial:
	var key := "%s|%.2f|%s|%.2f" % [color.to_html(), grime, outline, roughness]
	if not _cache.has(key):
		_cache[key] = toon_unique(color, grime, outline, roughness)
	return _cache[key]


## See-through glass (syringe barrels, vials), so the liquid level inside shows. No outline: it would hide the liquid.
static func glass() -> StandardMaterial3D:
	if not _cache.has("glass"):
		var mat := StandardMaterial3D.new()
		mat.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		mat.albedo_color = Color(0.85, 0.93, 0.97, 0.22)
		mat.roughness = 0.1
		mat.metallic_specular = 0.8
		_cache["glass"] = mat
	return _cache["glass"]


## Walls, floors and big furniture: no highlights, no outline, heavier grime.
static func environment(color: Color, grime: float = 0.6) -> ShaderMaterial:
	var key := "env|%s|%.2f" % [color.to_html(), grime]
	if not _cache.has(key):
		var mat := toon_unique(color, grime, false, 0.85)
		mat.set_shader_parameter("rim_strength", 0.0)
		mat.set_shader_parameter("specular_strength", 0.0)
		mat.set_shader_parameter("world_grime", true)
		_cache[key] = mat
	return _cache[key]


static func toon_unique(color: Color, grime: float = 0.25, outline: bool = true, roughness: float = 0.7) -> ShaderMaterial:
	var mat := ShaderMaterial.new()
	mat.shader = TOON
	mat.set_shader_parameter("albedo", color)
	mat.set_shader_parameter("grime", grime)
	mat.set_shader_parameter("roughness", roughness)
	if outline:
		mat.next_pass = _outline()
	return mat


## Compatibility entry point for main's drape and character builders. The
## evaluation shader remains authoritative; families only select its tuning.
static func family_unique(family: String, color: Color, roughness: float) -> ShaderMaterial:
	var mat := toon_unique(color, 0.0, false, roughness)
	match family:
		"skin":
			mat.set_shader_parameter("grime", 0.025)
			mat.set_shader_parameter("specular_strength", 0.3)
			mat.set_shader_parameter("rim_strength", 0.0)
		"cloth":
			mat.set_shader_parameter("grime", 0.08)
			mat.set_shader_parameter("specular_strength", 0.18)
			mat.set_shader_parameter("surface_detail", 0.16)
		"rubber":
			mat.set_shader_parameter("specular_strength", 0.25)
			mat.set_shader_parameter("surface_detail", 0.10)
	return mat


## Main requests a size-aware outline. Evaluation deliberately keeps one thin
## outline, so the size hint is accepted without changing its visual language.
static func outline_for(_max_thickness: float) -> ShaderMaterial:
	return _outline()


## Patient body skin with its own outline, both able to carve out the cavity box (see set_carve).
static func body_skin(tone: Color) -> ShaderMaterial:
	# Keep light skin from turning cream under the surgical lamp while retaining
	# separation in darker tones. A small red bias reads as perfused skin rather
	# than yellow plastic in the Compatibility renderer.
	var clinical := Color(tone.r * 0.94, tone.g * 0.87, tone.b * 0.88, tone.a)
	var mat := toon_unique(clinical.darkened(0.08), 0.025, false, 0.66)
	mat.set_shader_parameter("specular_strength", 0.3)
	mat.set_shader_parameter("rim_strength", 0.0)
	mat.set_shader_parameter("world_grime", true)
	return mat


## Places the surgical site on the body: region marks where the simulated skin replaces the body (cut away there),
## the wound maps draw the damage on the body everywhere else. site is the surgical site's global transform.
## region has one texel per tissue grid point (TissueSim.RES + 1 wide).
static func set_carve(mat: ShaderMaterial, site: Transform3D, half_size: Vector2, depth: float, region: Texture2D) -> void:
	var passes: Array[ShaderMaterial] = [mat]
	if mat.next_pass is ShaderMaterial:
		passes.append(mat.next_pass as ShaderMaterial)
	for pass_mat in passes:
		pass_mat.set_shader_parameter("carve_inverse", Projection(site.affine_inverse()))
		pass_mat.set_shader_parameter("carve_box", Vector3(half_size.x, depth, half_size.y))
		pass_mat.set_shader_parameter("carve_map", region)
		pass_mat.set_shader_parameter("carve_grid", float(region.get_width()))


## Cavity walls: only drawn inside the region (see flesh.gdshader).
static func set_reveal(mat: ShaderMaterial, site: Transform3D, half_size: Vector2, region: Texture2D) -> void:
	mat.set_shader_parameter("region_inverse", Projection(site.affine_inverse()))
	mat.set_shader_parameter("region_box", Vector3(half_size.x, 1.0, half_size.y))
	mat.set_shader_parameter("region_map", region)
	mat.set_shader_parameter("region_grid", float(region.get_width()))


static func set_site_maps(mat: ShaderMaterial, wounds: Texture2D, fluids: Texture2D) -> void:
	mat.set_shader_parameter("site_wounds", wounds)
	mat.set_shader_parameter("site_fluids", fluids)


static func flesh(color: Color = Color(0.55, 0.12, 0.12)) -> ShaderMaterial:
	var mat := ShaderMaterial.new()
	mat.shader = FLESH
	mat.set_shader_parameter("albedo", color)
	return mat


static func skin_site(tone: Color, wound_tex: Texture2D, fluid_tex: Texture2D) -> ShaderMaterial:
	var mat := ShaderMaterial.new()
	mat.shader = SKIN
	var clinical := Color(tone.r * 0.94, tone.g * 0.87, tone.b * 0.88, tone.a)
	mat.set_shader_parameter("skin_color", clinical.darkened(0.08))
	mat.set_shader_parameter("wound_map", wound_tex)
	mat.set_shader_parameter("fluid_map", fluid_tex)
	return mat


static func surgical_drape() -> ShaderMaterial:
	var mat := ShaderMaterial.new()
	mat.shader = SURGICAL_DRAPE
	return mat


## Fat (layer 0) or muscle (layer 1) under the surgical site skin.
static func tissue_layer(layer: int, fluid_tex: Texture2D) -> ShaderMaterial:
	var mat := ShaderMaterial.new()
	mat.shader = TISSUE_LAYER
	mat.set_shader_parameter("layer", layer)
	mat.set_shader_parameter("fluid_map", fluid_tex)
	return mat


static func glow(color: Color) -> StandardMaterial3D:
	var mat := StandardMaterial3D.new()
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.albedo_color = color
	return mat


static func blood_pool() -> StandardMaterial3D:
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.22, 0.0, 0.02)
	mat.roughness = 0.05
	mat.metallic_specular = 0.9
	return mat


static func _outline() -> ShaderMaterial:
	if not _cache.has("outline"):
		var mat := ShaderMaterial.new()
		mat.shader = OUTLINE
		_cache["outline"] = mat
	return _cache["outline"]
