// Smooth diffuse light, a soft specular spot, rim light and procedural grime.
// Used for everything except the simulated site skin. Materials.imported() picks the settings per material family
// (skin, rubber, cloth, metal, plastic, tissue), Materials.toon() makes the plain ones.
shader_type spatial;

uniform vec4 albedo : source_color = vec4(0.8, 0.8, 0.8, 1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, hint_default_white;
// Maps carried over from the imported model. The channel vectors pick which channel of the texture holds the value.
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap;
uniform bool use_normal_texture = false;
uniform sampler2D roughness_texture : filter_linear_mipmap, hint_default_white;
uniform vec4 roughness_channel = vec4(1.0, 0.0, 0.0, 0.0);
uniform sampler2D metallic_texture : filter_linear_mipmap, hint_default_white;
uniform vec4 metallic_channel = vec4(1.0, 0.0, 0.0, 0.0);
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float rim_strength = 0.3;
uniform float specular_strength = 0.4;
uniform float roughness : hint_range(0.0, 1.0) = 0.7;
// Light that wraps past the terminator, tinted (skin: red, like light scattered under it). 0 for hard surfaces.
uniform float wrap = 0.0;
uniform vec4 scatter_tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float grime : hint_range(0.0, 1.0) = 0.25;
uniform float grime_scale = 14.0;
// Grime fixed in the world, for walls and floors that never move. Anything else carries its own, so it doesn't
// slide across a moving model.
uniform bool world_grime = false;
// Fine surface relief in the model's own space (Materials.Detail): 1 skin pores, 2 rubber creases, 3 cloth weave
// and soft folds, 4 brushed steel. Only tilts the normal, so the silhouette stays the model's.
uniform int detail = 0;
uniform float detail_strength = 1.0;
uniform vec4 grime_color : source_color = vec4(0.25, 0.22, 0.15, 1.0);
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
// Fine geometry laid directly on simulated skin uses the same camera offset as skin.gdshader, so it keeps physical
// contact without being hidden by that shader's depth-bias displacement. Zero for every ordinary material.
uniform float camera_bias = 0.0;
// Contamination glow for the Germaphobe quirk, set per tool.
uniform float contamination = 0.0;
// Patient body only. carve_map marks where the simulated skin takes over (around cuts and pinched skin):
// the body is cut away there so the tissue layers and the cavity show. Everywhere else in the site the body
// itself shows the damage from the wound maps.
uniform sampler2D carve_map : filter_linear, hint_default_black;
uniform mat4 carve_inverse;
// x, z: half size of the surgical site, y: cavity depth. Zero disables carving.
uniform vec3 carve_box = vec3(0.0);
// Points in carve_map: one texel per tissue grid point, so texel centers sit exactly on the grid.
uniform vec2 carve_grid = vec2(1.0);
uniform sampler2D site_wounds : filter_linear, hint_default_black;
uniform sampler2D site_fluids : filter_linear, hint_default_black;
uniform float pallor = 0.0;
// Tools only: blood on the working end. coat is how bloody (0..1), reaching coat_reach meters back from the tip.
// coat_inverse is the world-to-tool transform (tip at -Z * coat_length), updated while the tool is bloody.
uniform float coat = 0.0;
uniform float coat_length = 0.1;
uniform float coat_reach = 0.02;
uniform mat4 coat_inverse;
// Surgeon scrubs only: old blood wiped off the gloves (0..1). Drawn in the model's own space so the blotches ride along.
uniform float stains = 0.0;

varying vec3 world_pos;
varying vec3 local_pos;

#include "res://assets/shaders/wound.gdshaderinc"

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

// Height of the fine relief at a point of the model (meters), see `detail`.
float relief(vec3 p) {
	if (detail == 1) {
		return (noise(p.xz * 900.0 + p.y * 300.0) + noise(p.xy * 700.0) - 1.0) * 0.00004;
	}
	if (detail == 2) {
		// Creases run across the fingers (the glove's X), with a faint grain everywhere.
		return noise(vec2(p.x * 260.0, p.z * 35.0 + p.y * 35.0)) * 0.00012 + noise(p.yz * 1400.0) * 0.00002;
	}
	if (detail == 3) {
		float weave = sin(p.x * 2600.0) * sin(p.z * 2600.0) * 0.00002;
		float folds = noise(p.xz * 18.0) * 0.0025 + noise(p.xz * 55.0 + 3.0) * 0.0008;
		return weave + folds;
	}
	if (detail == 4) {
		return noise(vec2(p.z * 4.0, (p.x + p.y) * 2400.0)) * 0.000015;
	}
	return 0.0;
}

void vertex() {
	if (camera_bias > 0.0) {
		vec3 camera = (inverse(MODEL_MATRIX) * vec4(MAIN_CAM_INV_VIEW_MATRIX[3].xyz, 1.0)).xyz;
		VERTEX += normalize(camera - VERTEX) * camera_bias;
	}
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	local_pos = VERTEX;
}

void fragment() {
	vec3 base = albedo.rgb * texture(albedo_texture, UV).rgb;
	vec3 grime_pos = world_grime ? world_pos : local_pos;
	float n = noise(grime_pos.xz * grime_scale) * 0.6 + noise(grime_pos.xy * grime_scale * 0.45 + 7.0) * 0.4;
	base = mix(base, base * grime_color.rgb * 1.6, grime * smoothstep(0.35, 0.9, n));
	if (stains > 0.0) {
		float s = noise(local_pos.xy * 16.0 + local_pos.z * 7.0) * 0.65 + noise(local_pos.zy * 40.0) * 0.35;
		float edge = 1.0 - stains * 0.55;
		base = mix(base, vec3(0.3, 0.03, 0.04), smoothstep(edge, edge + 0.04, s) * 0.85);
	}
	float wet = 0.0;
	float blood = 0.0;
	if (carve_box.x > 0.0) {
		vec3 lp = (carve_inverse * vec4(world_pos, 1.0)).xyz;
		vec2 cuv = lp.xz / (carve_box.xz * 2.0) + 0.5;
		bool in_site = all(greaterThan(cuv, vec2(0.0))) && all(lessThan(cuv, vec2(1.0))) && lp.y > -carve_box.y;
		if (in_site && texture(carve_map, (cuv * (carve_grid - 1.0) + 0.5) / carve_grid).r > 0.5) {
			discard;
		}
		base = mix(base, vec3(0.78, 0.8, 0.8), pallor * 0.5);
		// Only the skin facing out of the site gets its damage, not the far side of the body under it.
		// Its up axis is the second row of the world-to-site matrix (column-major: [column][row]).
		vec3 site_up = normalize((VIEW_MATRIX * vec4(carve_inverse[0][1], carve_inverse[1][1], carve_inverse[2][1], 0.0)).xyz);
		float facing = smoothstep(-0.3, 0.2, dot(NORMAL, site_up));
		if (in_site && facing > 0.0) {
			vec4 f = texture(site_fluids, cuv);
			// The fluid map ends at the site's edges: blood thins out toward them instead of stopping at a line.
			vec2 inside = min(cuv, 1.0 - cuv);
			f.r *= smoothstep(0.0, 0.1, min(inside.x, inside.y)) * facing;
			f.r = blood_amount(f.r, noise(world_pos.xz * 120.0 + world_pos.y * 50.0));
			blood = f.r;
			wet = apply_wounds(base, texture(site_wounds, cuv), f);
		}
		NORMAL = blood_normal(NORMAL, VERTEX, blood_thickness(blood));
	}
	if (detail > 0) {
		NORMAL = blood_normal(NORMAL, VERTEX, relief(local_pos) * detail_strength);
	}
	if (coat > 0.0) {
		vec3 tp = (coat_inverse * vec4(world_pos, 1.0)).xyz;
		float from_tip = tp.z + coat_length;
		float edge = coat_reach * (0.75 + 0.5 * noise(tp.xy * 500.0 + tp.z * 260.0));
		float blood_coat = coat * (1.0 - smoothstep(edge * 0.7, edge, from_tip));
		base = mix(base, vec3(0.42, 0.02, 0.04), blood_coat * 0.9);
		wet = max(wet, blood_coat);
	}
	if (use_normal_texture) {
		NORMAL_MAP = texture(normal_texture, UV).rgb;
	}
	float metal = metallic * dot(texture(metallic_texture, UV), metallic_channel) * (1.0 - wet);
	float rough = roughness * dot(texture(roughness_texture, UV), roughness_channel);
	vec3 reflection = vec3(0.0);
	if (metal > 0.0) {
		// No reflection probes here: metal mirrors a bright ceiling above and a dark floor below, sharper when polished.
		vec3 r = (INV_VIEW_MATRIX * vec4(reflect(-VIEW, NORMAL), 0.0)).xyz;
		float sky = smoothstep(mix(0.6, -0.2, rough), 1.0, r.y);
		reflection = base * mix(0.18, 0.7, sky) * metal;
	}
	// METALLIC stays 0: the engine would scale the lit diffuse down by it and turn steel black. Metal is handled here
	// and in light(), which reads the uniform.
	ALBEDO = base * (1.0 - metal * 0.4);
	ROUGHNESS = mix(rough, 0.08, wet);
	float pulse = 0.5 + 0.5 * sin(TIME * 4.0);
	EMISSION = emission_color.rgb + reflection + vec3(0.2, 0.9, 0.2) * contamination * pulse * 0.6;
}

void light() {
	float facing = dot(NORMAL, LIGHT);
	float ndl = max(facing, 0.0);
	float lit = ndl * ATTENUATION;
	float wrapped = max((facing + wrap) / (1.0 + wrap), 0.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * (lit + scatter_tint.rgb * (wrapped - ndl) * ATTENUATION) / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	// Wet spots (blood, open cuts: ROUGHNESS below the material's own) get a bright glint.
	float wet = clamp((roughness - ROUGHNESS) / max(roughness - 0.08, 0.01), 0.0, 1.0);
	float spec_size = mix(mix(0.995, 0.9, ROUGHNESS), 0.994, wet);
	float spec = smoothstep(spec_size, (spec_size + 1.0) * 0.5, dot(NORMAL, halfway)) * ATTENUATION;
	// Metal highlights take the metal's color; everything else reflects the light's own.
	vec3 spec_color = mix(vec3(1.0), ALBEDO / (1.0 - metallic * 0.4), metallic);
	SPECULAR_LIGHT += LIGHT_COLOR * spec_color * spec * mix(specular_strength, 0.5, wet) / PI;
	float rim = pow(1.0 - max(dot(NORMAL, VIEW), 0.0), 3.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * rim * rim_strength * lit / PI;
}
