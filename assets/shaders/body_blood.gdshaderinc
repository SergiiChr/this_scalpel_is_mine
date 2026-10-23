// One body-wide field, independent of the surgical site's UVs and the skin mesh that happens to draw a fragment.
uniform sampler2D body_blood_atlas : filter_linear, repeat_disable, hint_default_black;
uniform mat4 body_blood_inverse;
uniform vec3 body_blood_origin;
uniform vec3 body_blood_grid = vec3(0.0);
uniform float body_blood_cell = 0.0025;
uniform float body_blood_columns = 1.0;

float body_blood_slice(vec2 xy, float z) {
	vec2 tile = vec2(mod(z, body_blood_columns), floor(z / body_blood_columns));
	vec2 pixel = tile * body_blood_grid.xy + xy + 0.5;
	return texture(body_blood_atlas, pixel / vec2(textureSize(body_blood_atlas, 0))).r;
}

float body_blood(vec3 world) {
	if (body_blood_grid.x < 2.0) { return 0.0; }
	vec3 p = ((body_blood_inverse * vec4(world, 1.0)).xyz - body_blood_origin) / body_blood_cell;
	if (any(lessThan(p, vec3(0.0))) || any(greaterThan(p, body_blood_grid - 1.0))) { return 0.0; }
	float z = floor(p.z);
	return mix(body_blood_slice(p.xy, z), body_blood_slice(p.xy, min(z + 1.0, body_blood_grid.z - 1.0)), fract(p.z));
}
