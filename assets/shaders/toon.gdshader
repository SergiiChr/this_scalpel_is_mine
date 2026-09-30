// Smooth diffuse light, a soft specular spot, rim light and procedural grime.
// Used for everything except the simulated site skin. Tune per material from Materials.toon().
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

uniform vec4 albedo : source_color = vec4(0.8, 0.8, 0.8, 1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, hint_default_white;
uniform vec2 uv_scale = vec2(1.0);
uniform vec2 uv_offset = vec2(0.0);
uniform sampler2D roughness_texture : filter_linear_mipmap, hint_default_white;
uniform sampler2D metallic_texture : filter_linear_mipmap, hint_default_white;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap;
uniform sampler2D ao_texture : filter_linear_mipmap, hint_default_white;
uniform sampler2D emission_texture : source_color, filter_linear_mipmap, hint_default_white;
uniform vec4 roughness_channel = vec4(1.0, 0.0, 0.0, 0.0);
uniform vec4 metallic_channel = vec4(1.0, 0.0, 0.0, 0.0);
uniform vec4 ao_channel = vec4(1.0, 0.0, 0.0, 0.0);
uniform float ao_light_affect = 0.0;
uniform bool normal_enabled = false;
uniform float normal_scale = 1.0;
uniform float metallic = 0.0;
uniform float rim_strength = 0.0;
uniform float specular_strength = 0.4;
uniform float roughness : hint_range(0.0, 1.0) = 0.7;
uniform float grime : hint_range(0.0, 1.0) = 0.25;
uniform float grime_scale = 14.0;
uniform bool world_grime = false;
uniform float surface_detail = 0.0;
uniform vec4 grime_color : source_color = vec4(0.25, 0.22, 0.15, 1.0);
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
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
uniform float carve_grid = 1.0;
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

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	local_pos = VERTEX;
}

void fragment() {
	vec2 material_uv = UV * uv_scale + uv_offset;
	vec3 base = albedo.rgb * texture(albedo_texture, material_uv).rgb;
	vec3 gp = world_grime ? world_pos : local_pos;
	float n = noise(gp.xz * grime_scale) * 0.6 + noise(gp.xy * grime_scale * 0.45 + 7.0) * 0.4;
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
	if (coat > 0.0) {
		vec3 tp = (coat_inverse * vec4(world_pos, 1.0)).xyz;
		float from_tip = tp.z + coat_length;
		float edge = coat_reach * (0.75 + 0.5 * noise(tp.xy * 500.0 + tp.z * 260.0));
		float blood_coat = coat * (1.0 - smoothstep(edge * 0.7, edge, from_tip));
		base = mix(base, vec3(0.42, 0.02, 0.04), blood_coat * 0.9);
		wet = max(wet, blood_coat);
	}
	ALBEDO = base;
	float detail = noise(local_pos.xy * 650.0 + local_pos.z * 150.0) - 0.5;
	ROUGHNESS = mix(clamp(roughness * dot(texture(roughness_texture, material_uv), roughness_channel) + detail * surface_detail, 0.08, 1.0), 0.12, wet);
	METALLIC = metallic * dot(texture(metallic_texture, material_uv), metallic_channel) * (1.0 - wet);
	SPECULAR = mix(specular_strength, 0.5, wet);
	RIM = rim_strength;
	AO = dot(texture(ao_texture, material_uv), ao_channel);
	AO_LIGHT_AFFECT = ao_light_affect;
	if (normal_enabled) {
		NORMAL_MAP = texture(normal_texture, material_uv).rgb;
		NORMAL_MAP_DEPTH = normal_scale;
	}
	float pulse = 0.5 + 0.5 * sin(TIME * 4.0);
	EMISSION = emission_color.rgb * texture(emission_texture, material_uv).rgb + vec3(0.2, 0.9, 0.2) * contamination * pulse * 0.6;
}
