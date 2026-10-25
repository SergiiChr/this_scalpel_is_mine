// Shared by the body (toon.gdshader) and the simulated site skin (skin.gdshader), so damage looks the same on both.
//   wounds: R = cut groove, G = burn (red halo -> charred core), B = bruise,
//            A = stitches / staples (>= 0.5) or a closed-suture seam mask (< 0.5)
//   fluids: R = blood, G = marker ink, B = iodine, A = grime / vomit
//   bleeds: where wounds bleed from (WoundMap.Layer.Bleeds), each 1 at a spot's center falling off as 1 - (d / r)^2:
//           R = a spot on the skin, G = a spot inside a cut, B = G times its radius over MAX_SPOT_RADIUS,
//           A = G times its center's depth under the skin over MAX_SPOT_DEPTH

// WoundMap.MaxSpotRadius and WoundMap.MaxSpotDepth.
const float MAX_SPOT_RADIUS = 0.004;
const float MAX_SPOT_DEPTH = 0.05;
// A bleeding vessel's cut end: fresh blood (apply_wounds' film a tenth darker) inside a dark, bluish vessel wall.
const vec3 SPOT_BLOOD = vec3(0.5, 0.03, 0.05) * 0.9;
const vec3 SPOT_WALL = vec3(0.1, 0.06, 0.14);

// Shared blood pigment for the whole body, independent of surgical-site damage.
float apply_blood(inout vec3 c, float amount) {
	// Blood: a thin smear only darkens the skin, a film turns it fresh red, a thick pool goes glossy and dark.
	float stain = smoothstep(0.02, 0.15, amount);
	float film = smoothstep(0.12, 0.35, amount);
	float pool = smoothstep(0.4, 0.85, amount);
	c = mix(c, vec3(0.5, 0.13, 0.1), stain * 0.45);
	c = mix(c, vec3(0.5, 0.03, 0.05), film * 0.9);
	c = mix(c, vec3(0.27, 0.0, 0.02), pool);
	return film;
}

// Paints the damage over skin color c. Returns how wet the spot is (blood or an open cut), for roughness.
float apply_wounds(inout vec3 c, vec4 w, vec4 f) {
	c = mix(c, mix(vec3(0.55, 0.52, 0.25), vec3(0.32, 0.14, 0.32), smoothstep(0.2, 0.6, w.b)), clamp(w.b, 0.0, 1.0) * 0.75);
	c = mix(c, vec3(0.55, 0.3, 0.1), f.b * 0.6);
	c = mix(c, vec3(0.78, 0.25, 0.18), smoothstep(0.05, 0.4, w.g));
	c = mix(c, vec3(0.3, 0.12, 0.06), smoothstep(0.45, 0.7, w.g));
	c = mix(c, vec3(0.05, 0.04, 0.03), smoothstep(0.7, 0.95, w.g));
	float cut = smoothstep(0.08, 0.45, w.r);
	float edge = smoothstep(0.02, 0.1, w.r) - cut;
	c = mix(c, vec3(0.62, 0.3, 0.3), edge * 0.6);
	c = mix(c, vec3(0.32, 0.02, 0.03), cut);
	c = mix(c, vec3(0.06, 0.06, 0.1), step(0.5, w.a));
	c = mix(c, vec3(0.1, 0.15, 0.55), step(0.3, f.g) * 0.85);
	float film = apply_blood(c, f.r);
	c = mix(c, vec3(0.45, 0.42, 0.18), f.a * 0.7);
	return max(film, cut * 0.6);
}

// Blood amount with a ragged edge: grain (0..1 noise at a few millimeters) eats into the thin rim, not the pool.
float blood_amount(float blood, float grain) {
	return clamp(blood + (grain - 0.5) * 0.2 * smoothstep(0.0, 0.15, blood) * (1.0 - smoothstep(0.2, 0.45, blood)), 0.0, 1.0);
}

// Tilts the view space normal along the blood's edges as if it were a raised film, so the light catches its rim
// and it reads as liquid instead of paint. Built from screen derivatives, so it needs no tangents.
vec3 blood_normal(vec3 normal, vec3 view_pos, float thickness) {
	vec3 dpdx = dFdx(view_pos);
	vec3 dpdy = dFdy(view_pos);
	vec3 r1 = cross(dpdy, normal);
	vec3 r2 = cross(normal, dpdx);
	float det = dot(dpdx, r1);
	vec3 grad = sign(det) * (dFdx(thickness) * r1 + dFdy(thickness) * r2);
	return normalize(abs(det) * normal - grad);
}

// How far a point on a cut's wall `depth` meters under the skin is from a bleed spot's center, squared over its radius
// squared: 0 at the center, 1 on its rim, more outside it.
float bleed_spot(vec4 s, float depth) {
	// Toward a spot's edge its radius and depth come back less exactly (8 bits times a falling spot): stop short of it.
	if (s.g < 0.15) {
		return 2.0;
	}
	float r = s.b / s.g * MAX_SPOT_RADIUS;
	float down = depth - s.a / s.g * MAX_SPOT_DEPTH;
	return 1.0 - s.g + down * down / max(r * r, 1e-9);
}

// The same for a bleed spot on the skin.
float skin_spot(vec4 s) {
	return s.r < 0.02 ? 2.0 : 1.0 - s.r;
}

// Paints a bleed spot `reach` (bleed_spot(), skin_spot()) away over color c. Returns how much of it covers the point.
float paint_spot(inout vec3 c, float reach) {
	float cover = 1.0 - smoothstep(0.75, 0.85, reach);
	c = mix(c, mix(SPOT_BLOOD, SPOT_WALL, smoothstep(0.4, 0.55, reach)), cover);
	return cover;
}
