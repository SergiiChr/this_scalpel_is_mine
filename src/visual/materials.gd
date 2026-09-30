class_name Materials
extends RefCounted
## Shared materials. Everything goes through here so the look can be tuned in one place.

const TOON := preload("res://assets/shaders/toon.gdshader")
const OUTLINE := preload("res://assets/shaders/outline.gdshader")
const FLESH := preload("res://assets/shaders/flesh.gdshader")
const SKIN := preload("res://assets/shaders/skin.gdshader")
const TISSUE_LAYER := preload("res://assets/shaders/tissue_layer.gdshader")

const SKIN_TONES: Array[Color] = [
	Color(0.87, 0.7, 0.6), Color(0.78, 0.58, 0.45), Color(0.6, 0.42, 0.3), Color(0.42, 0.28, 0.2),
]

## Hospital palette. Surgical green for walls and linens (it's easy on eyes that stare at red all day),
## ceil blue for the second surgeon's scrubs, one cool fluorescent white for every room light but the surgical lamp.
const SURGICAL_GREEN := Color(0.4, 0.55, 0.5)
const PATIENT_GOWN := Color(0.52, 0.64, 0.6)
const SCRUBS: Array[Color] = [Color(0.22, 0.4, 0.36), Color(0.26, 0.38, 0.52)]
const FLUORESCENT := Color(0.88, 1.0, 0.94)

## Fine relief drawn on a surface (toon.gdshader `detail`).
enum Detail { NONE, PORES, CREASES, WEAVE, BRUSHED }

## How each kind of surface responds to light, so skin, gloves, steel and cloth read apart under the same lamp.
## specular: highlight strength, rim: edge light (cloth sheen), wrap/scatter: light past the terminator (skin),
## grime: procedural dirt, metallic: 1 for metal, detail: fine relief (Detail). Roughness comes from the model's own
## material.
const FAMILIES: Dictionary = {
	"skin": {"specular": 0.15, "rim": 0.12, "wrap": 0.35, "scatter": Color(1.0, 0.42, 0.32), "grime": 0.05, "detail": Detail.PORES},
	"rubber": {"specular": 0.18, "rim": 0.08, "grime": 0.0, "detail": Detail.CREASES},
	"cloth": {"specular": 0.0, "rim": 0.2, "wrap": 0.25, "scatter": Color(1.0, 1.0, 1.0), "grime": 0.12, "detail": Detail.WEAVE},
	"metal": {"specular": 0.9, "rim": 0.04, "grime": 0.02, "metallic": 1.0, "detail": Detail.BRUSHED},
	"plastic": {"specular": 0.35, "rim": 0.12, "grime": 0.1},
	"tissue": {"specular": 0.55, "rim": 0.1, "wrap": 0.4, "scatter": Color(1.0, 0.3, 0.25), "grime": 0.0},
}
## Model material name -> family. Names not listed are plastic.
const FAMILY_OF: Dictionary = {
	"skin": "skin", "lips": "skin",
	"glove": "rubber",
	"tint": "cloth", "gown": "cloth", "mask": "cloth", "knit": "cloth", "fabric_white": "cloth", "fabric_dark": "cloth",
	"mattress": "cloth", "cotton": "cloth", "paper": "cloth", "hair": "cloth", "leather": "cloth",
	"steel": "metal", "dark_steel": "metal", "chrome": "metal", "brass": "metal", "gold": "metal",
	"flesh": "tissue", "organ": "tissue", "vessel": "tissue", "cartilage": "tissue", "bone": "tissue", "mouth": "tissue",
	"tongue": "tissue", "teeth": "tissue", "eye": "tissue", "iris": "tissue", "blood_bag": "tissue",
}
## Outline width for a part, as a share of its middle dimension (see outline_for()).
const OUTLINE_SHARE := 0.06

static var _cache: Dictionary = {}


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


## Walls, floors and big furniture: no highlights, no outline, heavier grime fixed in the world.
static func environment(color: Color, grime: float = 0.35) -> ShaderMaterial:
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
		mat.next_pass = outline_for(0.003)
	return mat


## A model's own material in the game's shading, by its family (FAMILIES): keeps its color, roughness, metallic and
## texture maps. outline: most the outline may grow (meters), from the part's size, 0 for none.
static func imported(source: BaseMaterial3D, outline: float) -> ShaderMaterial:
	var family_name: String = FAMILY_OF.get(source.resource_name, "plastic")
	var textured := source.albedo_texture or source.normal_texture or source.roughness_texture or source.metallic_texture
	var metallic := float(FAMILIES[family_name].get("metallic", 0.0)) if source.metallic > 0.5 or source.metallic_texture else 0.0
	var key := "imported|%s|%s|%.2f|%.1f|%.4f" % [source.resource_name, source.albedo_color.to_html(), source.roughness, metallic, outline]
	if not textured and _cache.has(key):
		return _cache[key]
	var mat := family_unique(family_name, source.albedo_color, source.roughness)
	mat.set_shader_parameter("metallic", metallic)
	if source.albedo_texture:
		mat.set_shader_parameter("albedo_texture", source.albedo_texture)
	if source.normal_enabled and source.normal_texture:
		mat.set_shader_parameter("normal_texture", source.normal_texture)
		mat.set_shader_parameter("use_normal_texture", true)
	if source.roughness_texture:
		mat.set_shader_parameter("roughness_texture", source.roughness_texture)
		mat.set_shader_parameter("roughness_channel", _channel(source.roughness_texture_channel))
	if source.metallic_texture:
		mat.set_shader_parameter("metallic_texture", source.metallic_texture)
		mat.set_shader_parameter("metallic_channel", _channel(source.metallic_texture_channel))
	if outline > 0.0:
		mat.next_pass = outline_for(outline)
	if not textured:
		_cache[key] = mat
	return mat


## A new material of a family (FAMILIES) with no outline, for per-object tweaks.
static func family_unique(family_name: String, color: Color, roughness: float) -> ShaderMaterial:
	var family: Dictionary = FAMILIES[family_name]
	var mat := toon_unique(color, family.grime, false, roughness)
	mat.set_shader_parameter("specular_strength", family.specular)
	mat.set_shader_parameter("rim_strength", family.rim)
	mat.set_shader_parameter("wrap", family.get("wrap", 0.0))
	mat.set_shader_parameter("scatter_tint", family.get("scatter", Color.WHITE))
	mat.set_shader_parameter("metallic", family.get("metallic", 0.0))
	mat.set_shader_parameter("detail", family.get("detail", Detail.NONE))
	return mat


## Most outline a part of this size gets: a hairline on a scalpel blade, a full line round a table.
static func outline_size(bounds: AABB) -> float:
	var sizes := [bounds.size.x, bounds.size.y, bounds.size.z]
	sizes.sort()
	return clampf(float(sizes[1]) * OUTLINE_SHARE, 0.0003, 0.003)


## The shared outline pass that grows at most max_thickness meters (outline.gdshader).
static func outline_for(max_thickness: float) -> ShaderMaterial:
	var key := "outline|%.4f" % max_thickness
	if not _cache.has(key):
		var mat := ShaderMaterial.new()
		mat.shader = OUTLINE
		mat.set_shader_parameter("max_thickness", max_thickness)
		_cache[key] = mat
	return _cache[key]


## Channel mask for a BaseMaterial3D texture channel (red, green, blue, alpha, grayscale).
static func _channel(channel: int) -> Vector4:
	return [Vector4(1, 0, 0, 0), Vector4(0, 1, 0, 0), Vector4(0, 0, 1, 0), Vector4(0, 0, 0, 1), Vector4(0.333, 0.333, 0.333, 0)][channel]


## Patient body skin with its own outline, both able to carve out the cavity box (see set_carve).
static func body_skin(tone: Color) -> ShaderMaterial:
	var mat := family_unique("skin", tone, 0.6)
	var outline := ShaderMaterial.new()
	outline.shader = OUTLINE
	outline.set_shader_parameter("max_thickness", 0.002)
	mat.next_pass = outline
	return mat


## Places the surgical site on the body: region marks where the simulated skin replaces the body (cut away there),
## the wound maps draw the damage on the body everywhere else. site is the surgical site's global transform.
## region has one texel per tissue grid point (TissueSim.RES + 1 wide).
static func set_carve(mat: ShaderMaterial, site: Transform3D, half_size: Vector2, depth: float, region: Texture2D) -> void:
	for pass_mat: ShaderMaterial in [mat, mat.next_pass as ShaderMaterial]:
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
	mat.set_shader_parameter("skin_color", tone)
	# Lit like the body's skin around it (body_skin()).
	var family: Dictionary = FAMILIES.skin
	mat.set_shader_parameter("specular_strength", family.specular)
	mat.set_shader_parameter("rim_strength", family.rim)
	mat.set_shader_parameter("grime", family.grime)
	mat.set_shader_parameter("wrap", family.wrap)
	mat.set_shader_parameter("scatter_tint", family.scatter)
	mat.set_shader_parameter("wound_map", wound_tex)
	mat.set_shader_parameter("fluid_map", fluid_tex)
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


## Standing blood: glossy, but not a mirror. The lamp is a wide dish of bulbs (Room), so a mirror would show a big
## white disc of it instead of a wet glint.
static func blood_pool() -> StandardMaterial3D:
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.22, 0.0, 0.02)
	mat.roughness = 0.22
	mat.metallic_specular = 0.6
	return mat
