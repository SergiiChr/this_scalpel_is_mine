// What a body part adds to an X-ray film: drawn only by the film's camera, every surface summed, nothing hiding
// anything. A ray crosses a solid's surface twice, so a thin part adds about 2 x density. edge brightens where the ray
// grazes the surface: soft tissue shows as its outline. Faces turned straight at the beam (facing 1) get the full
// density, grazing ones `rim` times it: bone and metal fade off a little at their edges.
// gap_radius > 0 leaves out a ball around gap_center (world space): a broken bone, its pieces drawn on their own.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_disabled, shadows_disabled, fog_disabled;

uniform float density = 0.5;
uniform float edge = 0.0;
uniform float rim = 1.0;
uniform vec3 gap_center;
uniform float gap_radius = 0.0;

varying vec3 world;

void vertex() {
	world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (distance(world, gap_center) < gap_radius) {
		discard;
	}
	float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
	float graze = 1.0 - facing;
	ALBEDO = vec3(density * mix(rim, 1.0, facing) + edge * graze * graze * graze);
}
