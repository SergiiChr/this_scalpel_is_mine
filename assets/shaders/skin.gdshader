// Surgical site skin where it's cut or pinched: the outer tissue layer, simulated by TissueSim, lit like the body.
// Damage comes from the two WoundMap textures, drawn by wound.gdshaderinc exactly like on the body around it.
// Openings aren't painted: the sim pulls cut edges apart and gaping triangles are left out of the mesh,
// so you look through the skin into the fat and muscle layers below.
shader_type spatial;

uniform vec4 skin_color : source_color = vec4(0.84, 0.66, 0.56, 1.0);
uniform sampler2D wound_map : filter_linear, hint_default_black;
uniform sampler2D fluid_map : filter_linear, hint_default_black;
uniform float rim_strength = 0.2;
uniform float specular_strength = 0.12;
uniform float grime = 0.12;
uniform float pallor = 0.0;
// Set from Materials.FAMILIES.skin, like the body's skin (toon.gdshader).
uniform float wrap = 0.0;
uniform vec4 scatter_tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);

varying vec3 world_pos;

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
	vec4 w = textureLod(wound_map, UV, 0.0);
	// Cuts sink a little, bruises and burns swell, blood stands on top.
	VERTEX.y += -smoothstep(0.1, 0.8, w.r) * 0.0015 + w.b * 0.002 + smoothstep(0.1, 0.5, w.g) * 0.0015;
	VERTEX.y += blood_thickness(textureLod(fluid_map, UV, 0.0).r);
}

void fragment() {
	vec4 w = texture(wound_map, UV);
	vec4 f = texture(fluid_map, UV);
	f.r = blood_amount(f.r, noise(world_pos.xz * 120.0 + world_pos.y * 50.0));
	NORMAL = blood_normal(NORMAL, VERTEX, blood_thickness(f.r));
	float n = noise(world_pos.xz * 14.0) * 0.6 + noise(world_pos.xy * 6.3 + 7.0) * 0.4;
	vec3 c = mix(skin_color.rgb, skin_color.rgb * vec3(0.25, 0.22, 0.15) * 1.6, grime * smoothstep(0.35, 0.9, n));
	c = mix(c, vec3(0.78, 0.8, 0.8), pallor * 0.5);
	float wet = apply_wounds(c, w, f);
	ALBEDO = c;
	ROUGHNESS = mix(0.6, 0.08, wet);
}

// Same light as toon.gdshader so the site blends into the rest of the body.
void light() {
	float facing = dot(NORMAL, LIGHT);
	float ndl = max(facing, 0.0);
	float lit = ndl * ATTENUATION;
	float wrapped = max((facing + wrap) / (1.0 + wrap), 0.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * (lit + scatter_tint.rgb * (wrapped - ndl) * ATTENUATION) / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	// Dry skin gets the body's small dull highlight; only blood and open cuts (low roughness) shine wet.
	float wet = clamp((0.6 - ROUGHNESS) / 0.52, 0.0, 1.0);
	float spec_size = mix(mix(0.995, 0.9, ROUGHNESS), 0.994, wet);
	float spec = smoothstep(spec_size, (spec_size + 1.0) * 0.5, dot(NORMAL, halfway)) * ATTENUATION;
	SPECULAR_LIGHT += LIGHT_COLOR * spec * mix(specular_strength, 0.5, wet) / PI;
	float rim = pow(1.0 - max(dot(NORMAL, VIEW), 0.0), 3.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * rim * rim_strength * lit / PI;
}
