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

float noise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	vec3 u = f * f * (3.0 - 2.0 * f);
	return mix(
		mix(mix(hash(i), hash(i + vec3(1, 0, 0)), u.x), mix(hash(i + vec3(0, 1, 0)), hash(i + vec3(1, 1, 0)), u.x), u.y),
		mix(mix(hash(i + vec3(0, 0, 1)), hash(i + vec3(1, 0, 1)), u.x), mix(hash(i + vec3(0, 1, 1)), hash(i + vec3(1, 1, 1)), u.x), u.y),
		u.z);
}

// Vessels branching over the surface: thin lines where a warped noise crosses its middle. 0..1, 1 on a vessel.
float vessels(vec3 p, float scale, float width) {
	vec3 warp = vec3(noise(p * scale * 0.5), noise(p * scale * 0.5 + 17.0), noise(p * scale * 0.5 + 31.0)) - 0.5;
	float n = noise(p * scale + warp * 1.5);
	return 1.0 - smoothstep(0.0, width, abs(n - 0.5));
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
	// Large vessels darker and bluer (veins under the surface), fine ones a faint red web; the surface is mottled.
	float large = vessels(local_pos, 22.0, 0.035);
	float fine = vessels(local_pos + 5.0, 70.0, 0.05) * 0.5;
	float mottle = noise(local_pos * 45.0);
	vec3 c = albedo.rgb * mix(0.85, 1.1, mottle);
	c = mix(c, vec3(0.28, 0.06, 0.16), large * 0.6);
	c = mix(c, albedo.rgb * vec3(1.15, 0.6, 0.6), fine * (1.0 - large) * 0.5);
	c = mix(c, vec3(0.18, 0.02, 0.08), clamp(damage, 0.0, 1.0));
	ALBEDO = c;
	// A little of the tissue's own color even in shadow deep in a cavity, the way light scatters through it.
	EMISSION = c * 0.12;
	ROUGHNESS = 1.0 - wetness;
}

void light() {
	// Wrapped diffuse: wet tissue scatters light, so organs deep in a cavity still read on the sides the lamp misses.
	float ndl = clamp((dot(NORMAL, LIGHT) + 0.6) / 1.6, 0.0, 1.0);
	float lit = ndl * ATTENUATION;
	DIFFUSE_LIGHT += LIGHT_COLOR * lit / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	SPECULAR_LIGHT += LIGHT_COLOR * smoothstep(0.96, 0.98, dot(NORMAL, halfway)) * ATTENUATION * wetness * 0.5 / PI;
}
