// Full screen grading: desaturated sick-green tint, vignette, film grain, slight chromatic split.
// Surgeon state drives the extra uniforms (sickness wobble, blackout when passing out, blur, blood on the view).
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
// Blood splashed on the view: how much (more drops, more opaque), seconds since it landed, and its pattern.
uniform float lens_blood : hint_range(0.0, 1.0) = 0.0;
uniform float lens_age = 0.0;
uniform float lens_seed = 0.0;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

// At most one drop per grid cell: a lumpy blob that slides down, leaving a thin trail. Drops gather toward the edges.
// p is aspect-corrected screen space times scale.
float lens_drops(vec2 p, float scale, float seed, float density) {
	vec2 cell = floor(p);
	vec2 f = fract(p);
	float h = hash(cell + seed);
	if (h > density * (0.3 + 1.6 * length((cell + 0.5) / scale - 0.5))) {
		return 0.0;
	}
	float size = hash(cell + seed + 5.3);
	float r = 0.05 + 0.16 * size * size;
	float start = 0.12 + 0.2 * hash(cell + seed + 3.1);
	// Big drops run first and furthest; the smallest stay put.
	float slide = min(lens_age * 0.07 * smoothstep(0.07, 0.12, r), 0.95 - start - r);
	vec2 d = f - vec2(0.25 + 0.5 * hash(cell + seed + 1.7), start + slide);
	vec2 lump = (vec2(hash(cell + seed + 7.1), hash(cell + seed + 9.4)) - 0.5) * r * 1.4;
	float blob = min(length(d) / r, length(d - lump) / (r * 0.7));
	float drop = 1.0 - smoothstep(0.8, 1.0, blob);
	// The trail narrows toward where the drop started.
	float width = r * (0.15 + 0.45 * clamp(1.0 + d.y / max(slide, 0.001), 0.0, 1.0));
	float trail = step(-slide, d.y) * step(d.y, 0.0) * (1.0 - smoothstep(width * 0.7, width, abs(d.x)));
	return max(drop, trail * 0.7);
}

void fragment() {
	vec2 uv = SCREEN_UV;
	float lens = 0.0;
	if (lens_blood > 0.0) {
		vec2 p = vec2((SCREEN_UV.x - 0.5) * SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x + 0.5, SCREEN_UV.y);
		float density = 0.04 + 0.2 * lens_blood;
		lens = max(lens_drops(p * 4.0, 4.0, lens_seed, density), lens_drops(p * 9.0, 9.0, lens_seed + 17.0, density * 0.5));
		// Seen through a drop the view shifts a little, like through a lens.
		uv += (hash(floor(p * 4.0) + lens_seed) - 0.5) * 0.02 * lens;
	}
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
	// Thin blood is see-through red, the middle of a drop close to opaque.
	col = mix(col, col * vec3(0.45, 0.05, 0.05) + vec3(0.06, 0.0, 0.005), lens * min(lens_blood * 3.0, 0.9));
	col = mix(col, vec3(0.0), blackout);
	COLOR = vec4(col, 1.0);
}
