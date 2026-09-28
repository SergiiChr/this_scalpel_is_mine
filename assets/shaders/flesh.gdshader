// Wet interior tissue: cavity walls and organs. `damage` darkens and bruises an organ that got nicked.
shader_type spatial;
render_mode cull_disabled;

uniform vec4 albedo : source_color = vec4(0.55, 0.12, 0.12, 1.0);
uniform float damage = 0.0;
uniform float bands = 3.0;
uniform float wetness = 0.8;

varying vec3 local_pos;

float hash(vec3 p) {
	return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453);
}

void vertex() {
	local_pos = VERTEX;
	// Slow breathing/peristalsis wobble so organs never look like plastic.
	VERTEX += NORMAL * sin(TIME * 1.3 + VERTEX.x * 40.0) * 0.0008;
}

void fragment() {
	float veins = smoothstep(0.85, 1.0, hash(floor(local_pos * 180.0)));
	vec3 c = mix(albedo.rgb, albedo.rgb * 0.5, veins * 0.5);
	c = mix(c, vec3(0.18, 0.02, 0.08), clamp(damage, 0.0, 1.0));
	ALBEDO = c;
	ROUGHNESS = 1.0 - wetness;
}

void light() {
	float ndl = max(dot(NORMAL, LIGHT), 0.0);
	// Band the angle only, keep distance falloff smooth so lights don't draw hard circles on walls.
	float lit = floor(ndl * bands + 0.5) / bands * ATTENUATION;
	DIFFUSE_LIGHT += LIGHT_COLOR * lit / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	SPECULAR_LIGHT += LIGHT_COLOR * step(0.96, dot(NORMAL, halfway)) * ATTENUATION * wetness / PI;
}
