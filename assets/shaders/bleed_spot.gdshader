// A bleed's spot on the cavity pool's surface, drawn like the ones on the skin and the cut's walls: a plane whose UV
// spans the spot, its rim on the plane's inscribed circle.
shader_type spatial;

#include "res://assets/shaders/wound.gdshaderinc"

void fragment() {
	vec2 d = UV * 2.0 - 1.0;
	vec3 c = vec3(0.0);
	if (paint_spot(c, dot(d, d)) < 0.5) {
		discard;
	}
	ALBEDO = c;
	ROUGHNESS = 0.6;
	SPECULAR = 0.3;
}
