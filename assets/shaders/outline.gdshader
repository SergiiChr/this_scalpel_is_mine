// Inverted hull ink outline. Added as next_pass on toon materials.
shader_type spatial;
render_mode unshaded, cull_front, depth_draw_opaque;

uniform vec4 color : source_color = vec4(0.04, 0.035, 0.035, 1.0);
uniform float thickness = 0.003;
uniform mat4 carve_inverse;
uniform vec3 carve_extent = vec3(0.0);

varying vec3 world_pos;

void vertex() {
	VERTEX += NORMAL * thickness;
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (carve_extent.x > 0.0 && all(lessThan(abs((carve_inverse * vec4(world_pos, 1.0)).xyz), carve_extent))) {
		discard;
	}
	ALBEDO = color.rgb;
}
