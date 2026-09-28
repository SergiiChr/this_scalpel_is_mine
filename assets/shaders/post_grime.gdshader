// Full screen grading: desaturated sick-green tint, vignette, film grain, slight chromatic split.
// Surgeon state drives the extra uniforms (sickness wobble, blackout when passing out, blur).
shader_type canvas_item;

uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap;
uniform vec3 tint = vec3(0.93, 1.0, 0.94);
uniform float desaturate : hint_range(0.0, 1.0) = 0.3;
uniform float contrast = 1.1;
uniform float vignette : hint_range(0.0, 1.0) = 0.5;
uniform float grain : hint_range(0.0, 0.3) = 0.05;
uniform float aberration = 0.0012;
uniform float wobble = 0.0;
uniform float blur = 0.0;
uniform float blackout : hint_range(0.0, 1.0) = 0.0;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
	vec2 uv = SCREEN_UV;
	uv.x += sin(uv.y * 10.0 + TIME * 1.7) * 0.006 * wobble;
	uv.y += cos(uv.x * 8.0 + TIME * 1.3) * 0.004 * wobble;
	float split = aberration * (1.0 + wobble * 4.0);
	vec3 col = vec3(
		textureLod(screen_texture, uv + vec2(split, 0.0), blur).r,
		textureLod(screen_texture, uv, blur).g,
		textureLod(screen_texture, uv - vec2(split, 0.0), blur).b);
	float gray = dot(col, vec3(0.299, 0.587, 0.114));
	col = mix(col, vec3(gray), desaturate) * tint;
	col = (col - 0.5) * contrast + 0.5;
	float d = distance(SCREEN_UV, vec2(0.5));
	col *= 1.0 - smoothstep(0.3, 0.85, d) * vignette;
	col += (hash(SCREEN_UV * 931.0 + fract(TIME)) - 0.5) * grain;
	col = mix(col, vec3(0.0), blackout);
	COLOR = vec4(col, 1.0);
}
