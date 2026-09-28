class_name Materials
extends RefCounted
## Shared materials. Everything goes through here so the look can be tuned in one place.

const TOON := preload("res://assets/shaders/toon.gdshader")
const OUTLINE := preload("res://assets/shaders/outline.gdshader")
const FLESH := preload("res://assets/shaders/flesh.gdshader")
const SKIN := preload("res://assets/shaders/skin.gdshader")

const SKIN_TONES: Array[Color] = [
	Color(0.87, 0.7, 0.6), Color(0.78, 0.58, 0.45), Color(0.6, 0.42, 0.3), Color(0.42, 0.28, 0.2),
]

static var _cache: Dictionary = {}


## Cached per color/settings, so don't modify the result. Use toon_unique() for per-object tweaks.
static func toon(color: Color, grime: float = 0.25, outline: bool = true, roughness: float = 0.7) -> ShaderMaterial:
	var key := "%s|%.2f|%s|%.2f" % [color.to_html(), grime, outline, roughness]
	if not _cache.has(key):
		_cache[key] = toon_unique(color, grime, outline, roughness)
	return _cache[key]


## Walls, floors and big furniture: smooth shading, no outline, heavier grime.
static func environment(color: Color, grime: float = 0.6) -> ShaderMaterial:
	var key := "env|%s|%.2f" % [color.to_html(), grime]
	if not _cache.has(key):
		var mat := toon_unique(color, grime, false, 0.85)
		mat.set_shader_parameter("bands", 0.0)
		mat.set_shader_parameter("rim_strength", 0.0)
		mat.set_shader_parameter("specular_strength", 0.0)
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


## Patient body skin with its own outline, both able to carve out the cavity box (see set_carve).
static func body_skin(tone: Color) -> ShaderMaterial:
	var mat := toon_unique(tone, 0.12, false, 0.6)
	mat.set_shader_parameter("specular_strength", 0.12)
	mat.set_shader_parameter("rim_strength", 0.2)
	var outline := ShaderMaterial.new()
	outline.shader = OUTLINE
	mat.next_pass = outline
	return mat


## Cuts the body open wherever the wound map marks opened skin. site is the surgical site's global transform.
static func set_carve(mat: ShaderMaterial, site: Transform3D, half_size: Vector2, depth: float, wound_map: Texture2D) -> void:
	for pass_mat: ShaderMaterial in [mat, mat.next_pass as ShaderMaterial]:
		pass_mat.set_shader_parameter("carve_inverse", Projection(site.affine_inverse()))
		pass_mat.set_shader_parameter("carve_box", Vector3(half_size.x, depth, half_size.y))
		pass_mat.set_shader_parameter("carve_map", wound_map)


static func flesh(color: Color = Color(0.55, 0.12, 0.12)) -> ShaderMaterial:
	var mat := ShaderMaterial.new()
	mat.shader = FLESH
	mat.set_shader_parameter("albedo", color)
	return mat


static func skin_site(tone: Color, wound_tex: Texture2D, fluid_tex: Texture2D, size: Vector2) -> ShaderMaterial:
	var mat := ShaderMaterial.new()
	mat.shader = SKIN
	mat.set_shader_parameter("skin_color", tone)
	mat.set_shader_parameter("wound_map", wound_tex)
	mat.set_shader_parameter("fluid_map", fluid_tex)
	mat.set_shader_parameter("site_size", size)
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
