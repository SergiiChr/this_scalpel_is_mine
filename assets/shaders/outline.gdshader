// Inverted hull ink outline. Added as next_pass on toon materials.
// The same width on screen at any distance (`width`: a share of the screen's height, 0.0013 is about 1.4 px at 1080p),
// but never thicker than max_thickness meters, so a thin blade or forceps jaw gets a hairline where furniture gets a
// full line (Materials.outline_for()).
shader_type spatial;
render_mode unshaded, cull_front, depth_draw_opaque, skip_vertex_transform;

uniform vec4 color : source_color = vec4(0.06, 0.055, 0.055, 1.0);
uniform float width = 0.0013;
uniform float max_thickness = 0.003;
// Patient body only: cut away where the simulated skin takes over (see toon.gdshader).
uniform sampler2D carve_map : filter_linear, hint_default_black;
uniform mat4 carve_inverse;
// x, z: half size of the surgical site, y: cavity depth. Zero disables carving.
uniform vec3 carve_box = vec3(0.0);
// Points in carve_map: one texel per tissue grid point, so texel centers sit exactly on the grid.
uniform float carve_grid = 1.0;

varying vec3 world_pos;

void vertex() {
	VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 normal = normalize((MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	// Meters the screen's height covers at this depth.
	float screen = 2.0 * max(-VERTEX.z, 0.01) / PROJECTION_MATRIX[1][1];
	VERTEX += normal * min(width * screen, max_thickness);
	world_pos = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (carve_box.x > 0.0) {
		vec3 lp = (carve_inverse * vec4(world_pos, 1.0)).xyz;
		vec2 cuv = lp.xz / (carve_box.xz * 2.0) + 0.5;
		if (all(greaterThan(cuv, vec2(0.0))) && all(lessThan(cuv, vec2(1.0))) && lp.y > -carve_box.y && texture(carve_map, (cuv * (carve_grid - 1.0) + 0.5) / carve_grid).r > 0.5) {
			discard;
		}
	}
	ALBEDO = color.rgb;
}
