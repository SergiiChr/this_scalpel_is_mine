// Wet interior tissue: cavity walls and organs. `damage` darkens and bruises an organ that got nicked.
// The cavity walls only show where the site is open (region_map, same layout as the body's carve_map),
// so they never peek out where the site hangs past the body's edge.
shader_type spatial;
render_mode cull_disabled;

uniform vec4 albedo : source_color = vec4(0.55, 0.12, 0.12, 1.0);
uniform float damage = 0.0;
uniform float wetness = 0.8;
// Unit model space: a local compression with a compensating raised rim.
// The radial kernel integrates to zero on a flat patch, keeping volume close
// to constant without shrinking the entire organ when it is pushed.
uniform vec3 press_direction = vec3(0.0, 1.0, 0.0);
uniform float press_strength = 0.0;

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

float noise3(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = mix(hash(i), hash(i + vec3(1.0, 0.0, 0.0)), f.x);
	float b = mix(hash(i + vec3(0.0, 1.0, 0.0)), hash(i + vec3(1.0, 1.0, 0.0)), f.x);
	float c = mix(hash(i + vec3(0.0, 0.0, 1.0)), hash(i + vec3(1.0, 0.0, 1.0)), f.x);
	float d = mix(hash(i + vec3(0.0, 1.0, 1.0)), hash(i + vec3(1.0)), f.x);
	return mix(mix(a, b, f.y), mix(c, d, f.y), f.z);
}

void vertex() {
	if (press_strength > 0.0001) {
		vec3 axis = normalize(press_direction);
		vec3 relative = VERTEX - axis * 0.8;
		float along = dot(relative, axis);
		vec3 radial = relative - axis * along;
		float q = dot(radial, radial) / 0.45;
		float dent_and_rim = (1.0 - q) * exp(-q) * exp(-along * along * 3.0);
		VERTEX -= axis * press_strength * dent_and_rim;
	}
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
	float coarse = noise3(world_pos * 38.0);
	float fine = noise3(world_pos * 110.0 + vec3(13.0));
	float vessels = smoothstep(0.78, 0.91, coarse) * smoothstep(0.35, 0.72, fine);
	vec3 c = albedo.rgb * mix(0.78, 1.12, coarse);
	c = mix(c, vec3(0.20, 0.018, 0.045), vessels * 0.42);
	c = mix(c, vec3(0.18, 0.02, 0.08), clamp(damage, 0.0, 1.0));
	ALBEDO = c;
	ROUGHNESS = 1.0 - wetness * 0.82;
	EMISSION = c * 0.08;
}

void light() {
	float ndl = max((dot(NORMAL, LIGHT) + 0.14) / 1.14, 0.0);
	float lit = ndl * ATTENUATION;
	DIFFUSE_LIGHT += LIGHT_COLOR * lit / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	float broad = pow(max(dot(NORMAL, halfway), 0.0), 14.0);
	SPECULAR_LIGHT += LIGHT_COLOR * broad * ATTENUATION * wetness * 0.28 / PI;
}
