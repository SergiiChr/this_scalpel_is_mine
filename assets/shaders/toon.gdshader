// Cel shading with hard light bands, a hard specular spot, rim light and procedural grime.
// Used for everything that isn't skin. Tune per material from Materials.toon().
shader_type spatial;

uniform vec4 albedo : source_color = vec4(0.8, 0.8, 0.8, 1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, hint_default_white;
uniform float bands = 3.0;
uniform float rim_strength = 0.3;
uniform float specular_strength = 0.4;
uniform float roughness : hint_range(0.0, 1.0) = 0.7;
uniform float grime : hint_range(0.0, 1.0) = 0.25;
uniform float grime_scale = 14.0;
uniform vec4 grime_color : source_color = vec4(0.25, 0.22, 0.15, 1.0);
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
// Contamination glow for the Germaphobe quirk, set per tool.
uniform float contamination = 0.0;
// Patient body only: where the wound map says the skin is opened, the body is cut away so the cavity shows.
uniform sampler2D carve_map : filter_linear, hint_default_black;
uniform mat4 carve_inverse;
// x, z: half size of the surgical site, y: cavity depth. Zero disables carving.
uniform vec3 carve_box = vec3(0.0);

varying vec3 world_pos;

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
}

void fragment() {
	if (carve_box.x > 0.0) {
		vec3 lp = (carve_inverse * vec4(world_pos, 1.0)).xyz;
		vec2 cuv = lp.xz / (carve_box.xz * 2.0) + 0.5;
		if (all(greaterThan(cuv, vec2(0.0))) && all(lessThan(cuv, vec2(1.0))) && lp.y > -carve_box.y && texture(carve_map, cuv).r > 0.82) {
			discard;
		}
	}
	vec3 base = albedo.rgb * texture(albedo_texture, UV).rgb;
	float n = noise(world_pos.xz * grime_scale) * 0.6 + noise(world_pos.xy * grime_scale * 0.45 + 7.0) * 0.4;
	base = mix(base, base * grime_color.rgb * 1.6, grime * smoothstep(0.35, 0.9, n));
	ALBEDO = base;
	ROUGHNESS = roughness;
	float pulse = 0.5 + 0.5 * sin(TIME * 4.0);
	EMISSION = emission_color.rgb + vec3(0.2, 0.9, 0.2) * contamination * pulse * 0.6;
}

void light() {
	float ndl = max(dot(NORMAL, LIGHT), 0.0);
	// Band the angle only, keep distance falloff smooth. bands = 0 means smooth shading (room surfaces).
	float lit = (bands > 0.0 ? floor(ndl * bands + 0.5) / bands : ndl) * ATTENUATION;
	DIFFUSE_LIGHT += LIGHT_COLOR * lit / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	float spec_size = mix(0.995, 0.9, roughness);
	float spec = step(spec_size, dot(NORMAL, halfway)) * ATTENUATION;
	SPECULAR_LIGHT += LIGHT_COLOR * spec * specular_strength / PI;
	float rim = pow(1.0 - max(dot(NORMAL, VIEW), 0.0), 3.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * step(0.5, rim) * rim_strength * lit / PI;
}
