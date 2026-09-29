// Wet interior tissue: cavity walls and organs. `damage` darkens and bruises an organ that got nicked.
// The cavity walls only show where the site is open (region_map, same layout as the body's carve_map),
// so they never peek out where the site hangs past the body's edge.
shader_type spatial;
render_mode cull_disabled;

uniform vec4 albedo : source_color = vec4(0.55, 0.12, 0.12, 1.0);
uniform float damage = 0.0;
uniform float wetness = 0.8;

uniform sampler2D region_map : filter_linear, hint_default_white;
uniform mat4 region_inverse;
// x, z: half size of the surgical site. Zero shows everything (organs).
uniform vec3 region_box = vec3(0.0);
uniform float region_grid = 1.0;

varying vec3 local_pos;
varying vec3 world_pos;

float hash(vec3 p) {
	return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453);
}

void vertex() {
	local_pos = VERTEX;
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	// Slow breathing/peristalsis wobble so organs never look like plastic.
	VERTEX += NORMAL * sin(TIME * 1.3 + VERTEX.x * 40.0) * 0.0008;
}

void fragment() {
	if (region_box.x > 0.0) {
		vec2 ruv = (region_inverse * vec4(world_pos, 1.0)).xz / (region_box.xz * 2.0) + 0.5;
		if (texture(region_map, (ruv * (region_grid - 1.0) + 0.5) / region_grid).r < 0.5) {
			discard;
		}
	}
	float veins = smoothstep(0.85, 1.0, hash(floor(local_pos * 180.0)));
	vec3 c = mix(albedo.rgb, albedo.rgb * 0.5, veins * 0.5);
	c = mix(c, vec3(0.18, 0.02, 0.08), clamp(damage, 0.0, 1.0));
	ALBEDO = c;
	ROUGHNESS = 1.0 - wetness;
}

void light() {
	float ndl = max(dot(NORMAL, LIGHT), 0.0);
	float lit = ndl * ATTENUATION;
	DIFFUSE_LIGHT += LIGHT_COLOR * lit / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	SPECULAR_LIGHT += LIGHT_COLOR * smoothstep(0.96, 0.98, dot(NORMAL, halfway)) * ATTENUATION * wetness / PI;
}
