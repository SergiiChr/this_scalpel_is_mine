// Surgical site skin: the outer tissue layer, simulated by TissueSim and lit exactly like the body's skin.
// Damage is painted into two maps by WoundMap and drawn here, so it looks specific instead of a red flash.
//   wound_map: R = cut groove, G = burn (red halo -> charred core), B = bruise, A = stitches / staples
//   fluid_map: R = blood, G = marker ink, B = iodine, A = grime / vomit
// Openings aren't painted: the sim pulls cut edges apart and gaping triangles are left out of the mesh,
// so you look through the skin into the fat and muscle layers below.
shader_type spatial;

uniform vec4 skin_color : source_color = vec4(0.84, 0.66, 0.56, 1.0);
uniform sampler2D wound_map : filter_linear, hint_default_black;
uniform sampler2D fluid_map : filter_linear, hint_default_black;
uniform float bands = 3.0;
uniform float rim_strength = 0.2;
uniform float specular_strength = 0.12;
uniform float grime = 0.12;
uniform float pallor = 0.0;

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
	vec4 w = textureLod(wound_map, UV, 0.0);
	// Cuts sink a little, bruises and burns swell.
	VERTEX.y += -smoothstep(0.1, 0.8, w.r) * 0.0015 + w.b * 0.002 + smoothstep(0.1, 0.5, w.g) * 0.0015;
}

void fragment() {
	vec4 w = texture(wound_map, UV);
	vec4 f = texture(fluid_map, UV);
	float n = noise(world_pos.xz * 14.0) * 0.6 + noise(world_pos.xy * 6.3 + 7.0) * 0.4;
	vec3 c = mix(skin_color.rgb, skin_color.rgb * vec3(0.25, 0.22, 0.15) * 1.6, grime * smoothstep(0.35, 0.9, n));
	c = mix(c, vec3(0.78, 0.8, 0.8), pallor * 0.5);
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
	ROUGHNESS = mix(0.6, 0.08, max(blood, cut * 0.6));
}

// Same banded light as toon.gdshader so the site blends into the rest of the body.
void light() {
	float ndl = max(dot(NORMAL, LIGHT), 0.0);
	float lit = floor(ndl * bands + 0.5) / bands * ATTENUATION;
	DIFFUSE_LIGHT += LIGHT_COLOR * lit / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	// Dry skin gets the body's small dull highlight; only blood and open cuts (low roughness) shine wet.
	float wet = clamp((0.6 - ROUGHNESS) / 0.52, 0.0, 1.0);
	float spec = step(mix(0.995, 0.9, ROUGHNESS), dot(NORMAL, halfway)) * ATTENUATION;
	SPECULAR_LIGHT += LIGHT_COLOR * spec * mix(specular_strength, 0.8, wet) / PI;
	float rim = pow(1.0 - max(dot(NORMAL, VIEW), 0.0), 3.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * step(0.5, rim) * rim_strength * lit / PI;
}
