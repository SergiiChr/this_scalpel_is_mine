// Layers under the skin, seen through an opened incision.
// layer 0 = subcutaneous fat (yellow lobules), layer 1 = muscle and fascia (red striated, white sheen).
// Blood from the fluid map runs down into them.
shader_type spatial;
render_mode cull_disabled;

uniform int layer = 0;
uniform sampler2D fluid_map : filter_linear, hint_default_black;
uniform float bands = 3.0;

varying vec3 local_pos;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

// Distance to the nearest cell center: lumpy fat lobules.
float cells(vec2 p) {
	vec2 i = floor(p);
	float best = 8.0;
	for (int y = -1; y <= 1; y++) {
		for (int x = -1; x <= 1; x++) {
			vec2 cell = i + vec2(float(x), float(y));
			vec2 center = cell + vec2(hash(cell), hash(cell + 17.0));
			best = min(best, distance(p, center));
		}
	}
	return best;
}

void vertex() {
	local_pos = VERTEX;
}

void fragment() {
	vec3 c;
	float wet;
	if (layer == 0) {
		float lobe = cells(local_pos.xz * 260.0);
		c = mix(vec3(0.93, 0.8, 0.35), vec3(0.72, 0.5, 0.2), smoothstep(0.35, 0.75, lobe));
		wet = 0.7;
	} else {
		float fibers = sin(local_pos.x * 900.0 + sin(local_pos.z * 120.0) * 2.0) * 0.5 + 0.5;
		c = mix(vec3(0.5, 0.06, 0.07), vec3(0.72, 0.16, 0.14), fibers);
		c = mix(c, vec3(0.85, 0.82, 0.8), smoothstep(0.93, 1.0, hash(floor(local_pos.xz * 60.0))) * 0.4);
		wet = 0.85;
	}
	float blood = smoothstep(0.05, 0.4, texture(fluid_map, UV).r);
	ALBEDO = mix(c, vec3(0.3, 0.0, 0.03), blood * 0.8);
	ROUGHNESS = 1.0 - wet;
}

void light() {
	float ndl = max(dot(NORMAL, LIGHT), 0.0);
	float lit = floor(ndl * bands + 0.5) / bands * ATTENUATION;
	DIFFUSE_LIGHT += LIGHT_COLOR * lit / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	SPECULAR_LIGHT += LIGHT_COLOR * step(0.96, dot(NORMAL, halfway)) * ATTENUATION * (1.0 - ROUGHNESS) / PI;
}
