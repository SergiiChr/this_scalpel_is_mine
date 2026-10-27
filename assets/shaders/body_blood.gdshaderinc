// One body-wide field, independent of the surgical site's UVs and the skin mesh that happens to draw a fragment.
uniform sampler2D body_blood_atlas : filter_linear, repeat_disable, hint_default_black;
uniform mat4 body_blood_inverse;
uniform vec3 body_blood_origin;
uniform vec3 body_blood_grid = vec3(0.0);
uniform float body_blood_cell = 0.0025;
uniform float body_blood_columns = 1.0;
// How far outside the skin, in voxels, the field is read (BodyBloodLayer.ReadOutside): blood lies on the skin's
// outside, so its underside and the wall of a cut read nothing of it.
const float BODY_BLOOD_READ_OUTSIDE = 0.75;

float body_blood_slice(vec2 xy, float z) {
	vec2 tile = vec2(mod(z, body_blood_columns), floor(z / body_blood_columns));
	vec2 pixel = tile * body_blood_grid.xy + xy + 0.5;
	return texture(body_blood_atlas, pixel / vec2(textureSize(body_blood_atlas, 0))).r;
}

// Blood on skin at world facing world_normal.
float body_blood(vec3 world, vec3 world_normal) {
	if (body_blood_grid.x < 2.0) { return 0.0; }
	vec3 at = (body_blood_inverse * vec4(world, 1.0)).xyz
		+ normalize(mat3(body_blood_inverse) * world_normal) * body_blood_cell * BODY_BLOOD_READ_OUTSIDE;
	vec3 p = (at - body_blood_origin) / body_blood_cell;
	if (any(lessThan(p, vec3(0.0))) || any(greaterThan(p, body_blood_grid - 1.0))) { return 0.0; }
	float z = floor(p.z);
	return mix(body_blood_slice(p.xy, z), body_blood_slice(p.xy, min(z + 1.0, body_blood_grid.z - 1.0)), fract(p.z));
}

// How tall the thickest blood stands off the skin, in meters. Only seen through body_blood_raised(): the skin itself
// doesn't move.
const float BODY_BLOOD_HEIGHT = 0.003;
const int BODY_BLOOD_STEPS = 8;

// The blood's height where it reads `blood`, as a share of BODY_BLOOD_HEIGHT.
float body_blood_height(float blood) {
	return smoothstep(0.05, 0.85, blood);
}

// Blood seen at a point on the skin as if it stood up to BODY_BLOOD_HEIGHT tall (parallax occlusion): the view ray
// (world_view points to the camera) is followed down from that height to where it first dips into the blood. A pool
// then hides the skin behind it and its near rim shows as a wall, with no geometry drawn.
float body_blood_raised(vec3 world, vec3 world_normal, vec3 world_view) {
	if (body_blood_grid.x < 2.0) { return 0.0; }
	vec3 n = normalize(world_normal);
	float facing = dot(world_view, n);
	// How far along the skin the ray runs toward the camera from BODY_BLOOD_HEIGHT down to the skin. Capped at
	// grazing angles, where it would reach far past any pool.
	vec3 run = (world_view - n * facing) / max(facing, 0.25) * BODY_BLOOD_HEIGHT;
	float here = body_blood(world, world_normal);
	float top = body_blood(world + run, world_normal);
	// The field is smooth over a few voxels: dry at both ends of the ray means dry along it.
	if (max(here, top) < 0.05) { return here; }
	float above = 1.0 - body_blood_height(top);
	float last = top;
	for (int i = 1; i <= BODY_BLOOD_STEPS; i++) {
		float h = 1.0 - float(i) / float(BODY_BLOOD_STEPS);
		float b = i == BODY_BLOOD_STEPS ? here : body_blood(world + run * h, world_normal);
		float below = body_blood_height(b) - h;
		if (below >= 0.0) {
			// Where between this step and the last the ray met the blood's surface.
			return mix(last, b, above / max(above + below, 1e-5));
		}
		above = -below;
		last = b;
	}
	return here;
}
