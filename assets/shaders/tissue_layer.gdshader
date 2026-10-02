// Layers under the skin, seen through an opened incision.
// layer 0 = subcutaneous fat (rounded lobules), layer 1 = muscle and fascia (red striated, white sheen),
// layer 2 = the cut face of the skin in the walls of a cut through it (the patient's skin tone, flecked with tiny vessels).
// Blood from the fluid map runs down into them.
shader_type spatial;
render_mode cull_disabled;

uniform int layer = 0;
uniform sampler2D fluid_map : filter_linear, hint_default_black;
// Layer 2 only: the skin's own color (Materials.skin_site()), so the cut face matches the skin around it.
uniform vec4 skin_color : source_color = vec4(0.84, 0.66, 0.56, 1.0);

varying vec3 local_pos;

float hash3(vec3 p) {
	return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453);
}

float noise3(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = mix(hash3(i), hash3(i + vec3(1.0, 0.0, 0.0)), f.x);
	float b = mix(hash3(i + vec3(0.0, 1.0, 0.0)), hash3(i + vec3(1.0, 1.0, 0.0)), f.x);
	float c = mix(hash3(i + vec3(0.0, 0.0, 1.0)), hash3(i + vec3(1.0, 0.0, 1.0)), f.x);
	float d = mix(hash3(i + vec3(0.0, 1.0, 1.0)), hash3(i + vec3(1.0)), f.x);
	return mix(mix(a, b, f.y), mix(c, d, f.y), f.z);
}

// Three-dimensional cells stay rounded on horizontal surfaces and vertical
// incision walls; the previous XZ-only pattern stretched into columns.
float cells3(vec3 p) {
	vec3 i = floor(p);
	float best = 8.0;
	for (int z = -1; z <= 1; z++) {
		for (int y = -1; y <= 1; y++) {
			for (int x = -1; x <= 1; x++) {
				vec3 cell = i + vec3(float(x), float(y), float(z));
				vec3 center = cell + vec3(hash3(cell), hash3(cell + 17.0), hash3(cell + 41.0));
				best = min(best, distance(p, center));
			}
		}
	}
	return best;
}

void vertex() {
	local_pos = VERTEX;
}

// The normal tilted by a height field over the surface (meters), from its screen-space slope.
vec3 bumped(vec3 normal, vec3 view_pos, float height) {
	vec3 dpdx = dFdx(view_pos);
	vec3 dpdy = dFdy(view_pos);
	vec3 r1 = cross(dpdy, normal);
	vec3 r2 = cross(normal, dpdx);
	float det = dot(dpdx, r1);
	vec3 grad = sign(det) * (dFdx(height) * r1 + dFdy(height) * r2);
	return normalize(abs(det) * normal - grad);
}

void fragment() {
	vec3 c;
	float wet;
	// Lobules and fibers are bumpy, so the lamp breaks into small glints instead of one mirror-like sheet.
	float height = 0.0;
	if (layer == 0) {
		float lobe = cells3(local_pos * 105.0);
		float membrane = smoothstep(0.47, 0.64, lobe);
		float mottling = noise3(local_pos * 55.0);
		c = mix(vec3(0.76, 0.47, 0.18), vec3(0.48, 0.22, 0.08), membrane * 0.42);
		c *= mix(0.86, 1.08, mottling);
		c = mix(c, vec3(0.82, 0.70, 0.52), smoothstep(0.58, 0.72, lobe) * 0.18);
		height = (1.0 - lobe) * 0.0004;
		wet = 0.62;
	} else if (layer == 2) {
		// The skin's color, a touch pinker, with its pores' fine relief and the odd tiny vessel.
		float grain = noise3(local_pos * 800.0);
		float speck = smoothstep(0.94, 1.0, hash3(floor(local_pos * 900.0)));
		c = skin_color.rgb * vec3(1.0, 0.93, 0.9) * mix(0.9, 1.02, grain);
		c = mix(c, vec3(0.62, 0.22, 0.22), speck * 0.35);
		height = (grain - 0.5) * 0.00008;
		wet = 0.35;
	} else {
		float warp = noise3(local_pos * 42.0) * 3.0;
		float fibers = sin(dot(local_pos, normalize(vec3(1.0, 0.16, 0.28))) * 430.0 + warp) * 0.5 + 0.5;
		float bundles = noise3(local_pos * 70.0);
		c = mix(vec3(0.40, 0.035, 0.055), vec3(0.62, 0.105, 0.09), fibers * 0.45 + bundles * 0.3);
		float fascia = smoothstep(0.91, 0.98, noise3(local_pos * 31.0 + vec3(7.0)));
		c = mix(c, vec3(0.72, 0.62, 0.57), fascia * 0.36);
		height = fibers * 0.0002;
		wet = 0.78;
	}
	NORMAL = bumped(NORMAL, VERTEX, height);
	float blood = smoothstep(0.05, 0.4, texture(fluid_map, UV).r);
	ALBEDO = mix(c, vec3(0.22, 0.005, 0.02), blood * 0.76);
	ROUGHNESS = 1.0 - wet * 0.82;
	EMISSION = ALBEDO * 0.025;
}

void light() {
	// The skin's cut face stands across the light from above: light through the skin keeps it from going dark.
	float wrap = layer == 2 ? 0.8 : 0.16;
	float ndl = max((dot(NORMAL, LIGHT) + wrap) / (1.0 + wrap), 0.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * ndl * ATTENUATION / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	float broad = pow(max(dot(NORMAL, halfway), 0.0), 18.0);
	SPECULAR_LIGHT += LIGHT_COLOR * broad * ATTENUATION * (1.0 - ROUGHNESS) * 0.32 / PI;
}
