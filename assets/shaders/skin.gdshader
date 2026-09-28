// Surgical site skin. Everything that happens to the patient's skin is painted into two maps by WoundMap
// and rendered here, so damage looks specific instead of a generic red flash.
//   wound_map: R = cut depth (above open_threshold the skin is gone and the cavity shows through)
//              G = burn (red halo -> charred core), B = bruise, A = stitches / staples
//   fluid_map: R = blood, G = marker ink, B = iodine, A = grime / vomit
// Skin also deforms: pulls[] tents the skin toward a grabbing tool, cuts sink in, bruises and burns swell.
shader_type spatial;

uniform vec4 skin_color : source_color = vec4(0.84, 0.66, 0.56, 1.0);
uniform vec4 shadow_tint : source_color = vec4(0.55, 0.35, 0.4, 1.0);
uniform sampler2D wound_map : filter_linear, hint_default_black;
uniform sampler2D fluid_map : filter_linear, hint_default_black;
uniform float open_threshold = 0.82;
uniform float bands = 3.0;
uniform vec2 site_size = vec2(0.3, 0.3);
// xy = anchor uv, zw = pull in meters (x, z). Up to 4 simultaneous grips.
uniform vec4 pulls[4];
uniform int pull_count = 0;
uniform float pallor = 0.0;

void vertex() {
	vec4 w = textureLod(wound_map, UV, 0.0);
	vec3 offset = vec3(0.0);
	for (int i = 0; i < 4; i++) {
		if (i >= pull_count) {
			break;
		}
		vec2 d = (UV - pulls[i].xy) * site_size;
		float falloff = exp(-dot(d, d) / 0.0006);
		offset.xz += pulls[i].zw * falloff * 0.8;
		offset.y += length(pulls[i].zw) * falloff * 0.6;
	}
	offset.y -= smoothstep(0.1, 0.8, w.r) * 0.004;
	offset.y += (w.b * 0.003 + smoothstep(0.1, 0.5, w.g) * 0.002);
	VERTEX += offset;
}

void fragment() {
	vec4 w = texture(wound_map, UV);
	vec4 f = texture(fluid_map, UV);
	if (w.r > open_threshold) {
		discard;
	}
	vec3 c = mix(skin_color.rgb, vec3(0.78, 0.8, 0.8), pallor * 0.5);
	// Bruises go purple, older edges yellow-green.
	c = mix(c, mix(vec3(0.55, 0.52, 0.25), vec3(0.32, 0.14, 0.32), smoothstep(0.2, 0.6, w.b)), clamp(w.b, 0.0, 1.0) * 0.75);
	c = mix(c, vec3(0.55, 0.3, 0.1), f.b * 0.6);
	c = mix(c, vec3(0.78, 0.25, 0.18), smoothstep(0.05, 0.4, w.g));
	c = mix(c, vec3(0.3, 0.12, 0.06), smoothstep(0.45, 0.7, w.g));
	c = mix(c, vec3(0.05, 0.04, 0.03), smoothstep(0.7, 0.95, w.g));
	float cut = smoothstep(0.08, 0.45, w.r);
	float edge = smoothstep(0.02, 0.1, w.r) - cut;
	c = mix(c, vec3(0.62, 0.3, 0.3), edge * 0.6);
	c = mix(c, vec3(0.32, 0.02, 0.03), cut);
	c = mix(c, vec3(0.06, 0.06, 0.1), step(0.5, w.a));
	c = mix(c, vec3(0.1, 0.15, 0.55), step(0.3, f.g) * 0.85);
	float blood = smoothstep(0.05, 0.45, f.r);
	c = mix(c, vec3(0.25, 0.0, 0.02), blood);
	c = mix(c, vec3(0.45, 0.42, 0.18), f.a * 0.7);
	ALBEDO = c;
	ROUGHNESS = mix(0.7, 0.08, max(blood, cut * 0.6));
}

void light() {
	float ndl = max(dot(NORMAL, LIGHT), 0.0);
	// Band the angle only, keep distance falloff smooth so lights don't draw hard circles on walls.
	float lit = floor(ndl * bands + 0.5) / bands * ATTENUATION;
	DIFFUSE_LIGHT += LIGHT_COLOR * mix(shadow_tint.rgb * 0.2, vec3(1.0), lit) * lit / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	// Dry skin gets a pin-point highlight, blood and open tissue a wide wet one.
	float spec = step(mix(0.96, 0.999, ROUGHNESS), dot(NORMAL, halfway)) * ATTENUATION;
	SPECULAR_LIGHT += LIGHT_COLOR * spec * (1.0 - ROUGHNESS) * 0.8 / PI;
}
