// Room surface patterns in world space (Materials.Pattern), for toon.gdshader: 1 speckled sheet vinyl, 2 ceiling
// panels in a metal grid, 3 a theatre wall. Needs world_pos, hash() and noise() from the includer.
// Colors written here are linear. Raised parts fake their depth with a shadow cast down and a lit top edge, as if lit
// from the ceiling.

uniform int pattern = 0;
// Theatre wall: the world axis (x or z) that runs along the wall and the paint above the rail (the tiles below it take
// albedo).
uniform vec2 wall_axis = vec2(1.0, 0.0);
uniform vec4 wall_paint : source_color = vec4(0.22, 0.4, 0.55, 1.0);
// Up to four decorations drawn on (Materials.Decor: 1 window, 2 door, 3 service panel, 4 ceiling light, 0 unused).
// Areas are corners in meters: along the wall and up on a wall, x and z on the ceiling.
uniform vec4 decor_areas[4];
uniform int decor_kinds[4];

// Rail between tiles and paint, and the baseboard's top (meters up).
const float RAIL = 1.0;
const float BASEBOARD = 0.1;
const vec3 STEEL = vec3(0.42, 0.44, 0.47);
const vec3 WHITE = vec3(0.78, 0.79, 0.8);
// The theatre's LED white, bright enough to bloom.
const vec3 LED = vec3(1.7, 1.8, 1.9);
// Where a raised part's shadow falls (meters along, up).
const vec2 SHADOW = vec2(0.006, -0.012);

// 1 inside the box (corners lo, hi) shrunk by inset, 0 outside, smoothed over a pixel so far edges don't shimmer.
float inside(vec2 p, vec2 lo, vec2 hi, float inset) {
	vec2 d = max(lo + inset - p, p - hi + inset);
	float m = max(d.x, d.y);
	return 1.0 - smoothstep(0.0, fwidth(m), m);
}

float disc(vec2 p, vec2 center, float radius) {
	float d = length(p - center) - radius;
	return 1.0 - smoothstep(0.0, fwidth(d), d);
}

// 1 within width of a grid line every period meters, smoothed over a pixel.
float grid_line(vec2 p, float period, float width) {
	vec2 to_line = abs(fract(p / period + 0.5) - 0.5) * period;
	float d = min(to_line.x, to_line.y);
	return 1.0 - smoothstep(width, width + fwidth(d), d);
}

// A raised plate: its shadow, the plate, a lit top edge and a dark bottom edge.
vec3 plate(vec3 c, vec2 p, vec2 lo, vec2 hi, vec3 color) {
	c = mix(c, c * 0.55, inside(p - SHADOW, lo, hi, 0.0) * 0.7);
	c = mix(c, color, inside(p, lo, hi, 0.0));
	float bevel = min(0.004, (hi.y - lo.y) * 0.2);
	c = mix(c, color * 1.25, inside(p, vec2(lo.x, hi.y - bevel), hi, 0.0));
	c = mix(c, color * 0.6, inside(p, lo, vec2(hi.x, lo.y + bevel), 0.0));
	return c;
}

// A round raised fitting: shadow, body, highlight up top.
vec3 knob(vec3 c, vec2 p, vec2 center, float radius, vec3 color) {
	c = mix(c, c * 0.55, disc(p - SHADOW * 0.6, center, radius) * 0.7);
	c = mix(c, color, disc(p, center, radius));
	c = mix(c, color * 1.3, disc(p, center + vec2(-0.25, 0.3) * radius, radius * 0.45) * 0.6);
	return c;
}

// Slatted blinds: each slat lit on top and shaded under, daylight through the gaps, two lift cords.
vec3 blinds(vec2 p, vec2 lo, vec2 hi, inout float light) {
	float t = fract(p.y / 0.025);
	vec3 slat = mix(vec3(0.5, 0.52, 0.55), vec3(0.86, 0.87, 0.88), smoothstep(0.1, 0.9, t));
	float gap = 1.0 - smoothstep(0.0, 0.12, t);
	light = gap;
	vec3 c = mix(slat, vec3(1.0, 1.0, 0.97), gap);
	float width = hi.x - lo.x;
	for (int i = 1; i <= 2; i++) {
		float x = lo.x + width * float(i) / 3.0;
		c = mix(c, vec3(0.6, 0.6, 0.58), 1.0 - smoothstep(0.0, fwidth(p.x), abs(p.x - x) - 0.0015));
	}
	return c;
}

// A window: two panes behind a white frame and mullion, blinds drawn, a head rail, a deep reveal shadowed along the
// top, a sill standing out below.
vec3 window(vec3 c, vec2 p, vec2 lo, vec2 hi, inout vec3 glow) {
	float mid = (lo.x + hi.x) * 0.5;
	c = plate(c, p, lo, hi, WHITE * 0.95);
	float light = 0.0;
	float pane = (inside(p, lo, vec2(mid, hi.y), 0.05) + inside(p, vec2(mid, lo.y), hi, 0.05)) * (1.0 - inside(p, vec2(mid - 0.025, lo.y), vec2(mid + 0.025, hi.y), 0.0));
	vec3 shade = blinds(p, lo, hi, light);
	c = mix(c, shade, pane);
	c = mix(c, WHITE * 1.05, inside(p, vec2(lo.x + 0.05, hi.y - 0.11), vec2(hi.x - 0.05, hi.y - 0.05), 0.0));
	float reveal = smoothstep(hi.y - 0.2, hi.y - 0.11, p.y) * pane;
	c *= 1.0 - reveal * 0.35;
	// The sill.
	c = plate(c, p, vec2(lo.x - 0.04, lo.y - 0.035), vec2(hi.x + 0.04, lo.y), WHITE * 1.05);
	glow += mix(shade * 0.12, vec3(0.5, 0.52, 0.5), light * 0.6) * pane * (1.0 - reveal);
	return c;
}

// A door: steel frame, laminate leaf with a faint grain, three hinges, a closer arm, a wired glass vision panel, a
// lever handle on its rose, a brushed kick plate and a sign.
vec3 door(vec3 c, vec2 p, vec2 lo, vec2 hi) {
	vec2 leaf_lo = lo + vec2(0.05, 0.0);
	vec2 leaf_hi = hi - vec2(0.05, 0.05);
	c = plate(c, p, lo, hi, STEEL * 0.7);
	float grain = noise(vec2(p.x * 300.0, p.y * 4.0)) * 0.6 + noise(vec2(p.x * 900.0, p.y * 12.0)) * 0.4;
	vec3 laminate = vec3(0.6, 0.62, 0.62) * (0.96 + grain * 0.06);
	c = mix(c, laminate, inside(p, leaf_lo, leaf_hi, 0.0));
	c = mix(c, c * 0.7, inside(p, leaf_lo, vec2(leaf_hi.x, leaf_lo.y + 0.01), 0.0) + inside(p, vec2(leaf_hi.x - 0.004, leaf_lo.y), leaf_hi, 0.0));
	for (int i = 0; i < 3; i++) {
		float y = leaf_lo.y + 0.25 + float(i) * (leaf_hi.y - leaf_lo.y - 0.5) * 0.5;
		c = plate(c, p, vec2(leaf_lo.x - 0.012, y), vec2(leaf_lo.x + 0.012, y + 0.1), STEEL);
	}
	c = plate(c, p, vec2(leaf_lo.x + 0.08, leaf_hi.y - 0.07), vec2(leaf_lo.x + 0.45, leaf_hi.y - 0.04), vec3(0.08));
	vec2 panel = vec2((leaf_lo.x + leaf_hi.x) * 0.5, leaf_hi.y - 0.6);
	vec2 half_panel = vec2(0.13, 0.32);
	c = plate(c, p, panel - half_panel - 0.02, panel + half_panel + 0.02, STEEL);
	float glass = inside(p, panel - half_panel, panel + half_panel, 0.0);
	float streak = smoothstep(0.03, 0.0, abs(p.x - panel.x + (p.y - panel.y) * 0.6 + 0.04));
	vec3 pane = mix(vec3(0.12, 0.16, 0.2), vec3(0.4, 0.45, 0.5), streak * 0.5 + smoothstep(panel.y - half_panel.y, panel.y + half_panel.y, p.y) * 0.2);
	pane = mix(pane, pane * 0.7, grid_line(p - panel, 0.03, 0.0006));
	c = mix(c, pane, glass);
	c = plate(c, p, vec2(panel.x - 0.08, panel.y + half_panel.y + 0.07), vec2(panel.x + 0.08, panel.y + half_panel.y + 0.12), vec3(0.6, 0.04, 0.04));
	c = mix(c, vec3(0.85), inside(p, vec2(panel.x - 0.06, panel.y + half_panel.y + 0.09), vec2(panel.x + 0.06, panel.y + half_panel.y + 0.1), 0.0));
	vec2 handle = vec2(leaf_hi.x - 0.08, leaf_lo.y + 1.0);
	c = knob(c, p, handle, 0.03, STEEL);
	c = plate(c, p, handle - vec2(0.14, 0.012), handle + vec2(0.0, 0.012), STEEL * 1.2);
	vec2 kick_lo = vec2(leaf_lo.x + 0.02, leaf_lo.y + 0.02);
	vec2 kick_hi = vec2(leaf_hi.x - 0.02, leaf_lo.y + 0.3);
	vec3 brushed = STEEL * (0.92 + noise(vec2(p.x * 3.0, p.y * 600.0)) * 0.16);
	c = plate(c, p, kick_lo, kick_hi, STEEL);
	c = mix(c, brushed, inside(p, kick_lo, kick_hi, 0.004));
	return c;
}

// A medical gas outlet: colored ring (green oxygen, yellow air, white vacuum) round a dark socket.
vec3 gas_outlet(vec3 c, vec2 p, vec2 center, vec3 ring) {
	c = knob(c, p, center, 0.032, vec3(0.7));
	c = mix(c, ring, disc(p, center, 0.024));
	c = mix(c, vec3(0.04), disc(p, center, 0.011));
	return c;
}

// A power socket: a red or white face with two pins and an earth slot.
vec3 socket(vec3 c, vec2 p, vec2 center, vec3 face) {
	c = plate(c, p, center - 0.035, center + 0.035, face);
	c = mix(c, vec3(0.03), disc(p, center + vec2(-0.012, -0.004), 0.005) + disc(p, center + vec2(0.012, -0.004), 0.005));
	c = mix(c, vec3(0.03), inside(p, center + vec2(-0.002, 0.008), center + vec2(0.002, 0.02), 0.0));
	return c;
}

// A wall service panel like a bed head unit: a white strip with a lit top edge, screws at its ends, three gas
// outlets, then red and white sockets, each with a small label under it.
vec3 service_panel(vec3 c, vec2 p, vec2 lo, vec2 hi) {
	c = plate(c, p, lo, hi, WHITE);
	c = mix(c, WHITE * 0.85, inside(p, lo, hi, 0.012) - inside(p, lo, hi, 0.014));
	float middle = (lo.y + hi.y) * 0.5 + 0.015;
	for (int i = 0; i < 2; i++) {
		float x = i == 0 ? lo.x + 0.025 : hi.x - 0.025;
		c = mix(c, vec3(0.3), disc(p, vec2(x, hi.y - 0.025), 0.005) + disc(p, vec2(x, lo.y + 0.025), 0.005));
	}
	vec3 rings[3] = vec3[3](vec3(0.05, 0.45, 0.12), vec3(0.85, 0.7, 0.05), vec3(0.9));
	float x = lo.x + 0.1;
	for (int i = 0; i < 3; i++) {
		c = gas_outlet(c, p, vec2(x, middle), rings[i]);
		c = mix(c, vec3(0.1), inside(p, vec2(x - 0.025, middle - 0.065), vec2(x + 0.025, middle - 0.055), 0.0));
		x += 0.11;
	}
	x += 0.04;
	for (int i = 0; i < 8 && x < hi.x - 0.06; i++) {
		c = socket(c, p, vec2(x, middle), i % 2 == 0 ? vec3(0.55, 0.03, 0.03) : vec3(0.85, 0.86, 0.86));
		c = mix(c, vec3(0.1), inside(p, vec2(x - 0.025, middle - 0.065), vec2(x + 0.025, middle - 0.055), 0.0));
		x += 0.1;
	}
	return c;
}

// A recessed LED troffer: white trim, two lit diffusers split down the middle.
vec3 ceiling_light(vec3 c, vec2 p, vec2 lo, vec2 hi, inout vec3 glow) {
	c = mix(c, WHITE, inside(p, lo, hi, 0.0));
	float wide = step(hi.y - lo.y, hi.x - lo.x);
	vec2 split = mix(vec2((lo.x + hi.x) * 0.5, 0.0), vec2(0.0, (lo.y + hi.y) * 0.5), wide);
	float lit = inside(p, lo, hi, 0.035);
	lit *= 1.0 - mix(inside(p, vec2(split.x - 0.015, lo.y), vec2(split.x + 0.015, hi.y), 0.0), inside(p, vec2(lo.x, split.y - 0.015), vec2(hi.x, split.y + 0.015), 0.0), wide);
	c = mix(c, vec3(0.0), lit);
	glow += LED * lit;
	return c;
}

// 20 cm glazed tiles up to the rail, roller-painted plaster above, a dark baseboard, a steel rail, then the decor.
vec3 theatre_wall(vec3 tiles, vec3 p3, inout vec3 glow) {
	vec2 p = vec2(dot(p3.xz, wall_axis), p3.y);
	vec2 tile = p / 0.2;
	vec3 lower = tiles * (1.0 + (hash(floor(tile)) - 0.5) * 0.05) * (1.0 - 0.25 * grid_line(p, 0.2, 0.0015));
	float blotch = noise(p * 6.0) * 0.6 + noise(p * 17.0 + 5.0) * 0.4;
	float grain = noise(p * 400.0) * 0.6 + noise(p * 900.0 + 11.0) * 0.4;
	vec3 upper = wall_paint.rgb * (1.0 + (blotch - 0.5) * 0.06 + (grain - 0.5) * 0.1);
	vec3 c = mix(lower, upper, smoothstep(0.0, fwidth(p.y), p.y - RAIL));
	c = plate(c, p, vec2(-100.0, 0.0), vec2(100.0, BASEBOARD), vec3(0.03, 0.05, 0.07));
	c = plate(c, p, vec2(-100.0, RAIL - 0.02), vec2(100.0, RAIL + 0.02), STEEL * 0.8);
	for (int i = 0; i < 4; i++) {
		vec2 lo = decor_areas[i].xy;
		vec2 hi = decor_areas[i].zw;
		if (decor_kinds[i] == 1) {
			c = window(c, p, lo, hi, glow);
		} else if (decor_kinds[i] == 2) {
			c = door(c, p, lo, hi);
		} else if (decor_kinds[i] == 3) {
			c = service_panel(c, p, lo, hi);
		}
	}
	return c;
}

// 60 cm panels in a metal grid, then the lights.
vec3 theatre_ceiling(vec3 base, vec2 p, inout vec3 glow) {
	vec3 c = base * (1.0 - 0.35 * grid_line(p, 0.6, 0.008));
	for (int i = 0; i < 4; i++) {
		if (decor_kinds[i] == 4) {
			c = ceiling_light(c, p, decor_areas[i].xy, decor_areas[i].zw, glow);
		}
	}
	return c;
}

// Applies the room pattern to base. Windows and lights add their light to glow.
vec3 room_pattern(vec3 base, vec3 p, inout vec3 glow) {
	if (pattern == 1) {
		// Flecks a few millimeters across, some lighter and some darker than the sheet.
		float light_fleck = step(0.88, noise(p.xz * 160.0));
		float dark_fleck = step(0.88, noise(p.xz * 140.0 + 31.0));
		return base * (1.0 + light_fleck * 0.15 - dark_fleck * 0.18 + (noise(p.xz * 1.5) - 0.5) * 0.06);
	}
	if (pattern == 2) {
		return theatre_ceiling(base, p.xz, glow);
	}
	if (pattern == 3) {
		return theatre_wall(base, p, glow);
	}
	return base;
}
