// Blood spread over a surface by SurfaceFilm (the body round the site, the bed, the floor).
// UV.x carries how much of the surface the liquid hides at each point (0..1), UV.y its optical depth (depth over
// SurfaceBlood.CoverDepth). The edge is drawn where the interpolated coverage crosses its middle: halfway between a
// wet point and a dry one, so it runs smoothly between the points instead of tracing the triangles. Colored like
// blood on the site skin (wound.gdshaderinc): a thin film fresh red, a deep pool dark.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;

varying vec3 world_pos;
varying float coverage;
varying float optical_depth;

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
	coverage = UV.x;
	optical_depth = UV.y;
}

void fragment() {
	float edge = coverage + (noise(world_pos.xz * 120.0 + world_pos.y * 50.0) - 0.5) * 0.15;
	float film = smoothstep(0.4, 0.55, edge);
	float blood = 1.0 - exp(-optical_depth);
	float pool = smoothstep(0.4, 0.85, blood);
	ALBEDO = mix(vec3(0.5, 0.03, 0.05), vec3(0.27, 0.0, 0.02), pool);
	ALPHA = film * 0.95;
	NORMAL = blood_normal(NORMAL, VERTEX, blood_thickness(blood));
	ROUGHNESS = mix(0.45, 0.08, film);
	SPECULAR = 0.5;
}
