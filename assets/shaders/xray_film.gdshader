// A developed X-ray film: black where the beam got through, clear where something stopped it. Held against a light it
// shows bone and metal white; against a dark wall it's hard to read. `image` holds what the beam met (xray.gdshader's
// sum), `developed` fades it in from black.
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

uniform sampler2D image : source_color, filter_linear, hint_default_black;
uniform float developed = 1.0;
uniform float seed = 0.0;

float grain(vec2 at) {
	return fract(sin(dot(floor(at), vec2(12.9898, 78.233)) + seed) * 43758.5453);
}

void fragment() {
	float sum = texture(image, UV).r;
	// Film saturates: soft tissue stays grey, bone and metal go clear.
	float clear = 1.0 - exp(-2.6 * sum);
	clear += (grain(UV * 700.0) - 0.5) * 0.06;
	// The beam is weaker at the edges of the field.
	vec2 off = UV - 0.5;
	clear *= 1.0 - dot(off, off) * 0.6;
	clear = clamp(clear * developed, 0.0, 1.0);
	ALBEDO = vec3(0.015);
	ALPHA = 1.0 - clear * 0.94;
}
