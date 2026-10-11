// Room surface patterns in world space (Materials.Pattern), for toon.gdshader: 1 speckled sheet vinyl, 2 ceiling
// panels in a metal grid, 3 a theatre wall. Needs world_pos, hash() and noise() from the includer.
// Colors written here are linear.

uniform int pattern = 0;
// Theatre wall: the world axis (x or z) that runs along the wall, the paint above the rail (the tiles below it take
// albedo), and up to four features painted on: area (along, up, along, up corners in meters) and kind
// (Materials.WallFeature: 1 window, 2 door, 3 service panel, 0 unused).
uniform vec2 wall_axis = vec2(1.0, 0.0);
uniform vec4 wall_paint : source_color = vec4(0.22, 0.4, 0.55, 1.0);
uniform vec4 wall_features[4];
uniform int wall_feature_kinds[4];

// Rail between tiles and paint, and the baseboard's top (meters up).
const float RAIL = 1.0;
const float BASEBOARD = 0.1;

// 1 inside the box (corners lo, hi) shrunk by inset, 0 outside, smoothed over a pixel so far edges don't shimmer.
float inside(vec2 p, vec2 lo, vec2 hi, float inset) {
	vec2 d = max(lo + inset - p, p - hi + inset);
	float m = max(d.x, d.y);
	return 1.0 - smoothstep(0.0, fwidth(m), m);
}

// 1 within width of a grid line every period meters, smoothed over a pixel.
float grid_line(vec2 p, float period, float width) {
	vec2 to_line = abs(fract(p / period + 0.5) - 0.5) * period;
	float d = min(to_line.x, to_line.y);
	return 1.0 - smoothstep(width, width + fwidth(d), d);
}

// A window with blinds drawn: white frame, a shadowed reveal along its top and left, daylight through the slats.
vec3 window(vec3 c, vec2 p, vec2 lo, vec2 hi, inout vec3 glow) {
	float frame = inside(p, lo, hi, 0.0);
	float pane = inside(p, lo, hi, 0.06);
	float reveal = pane - inside(p, lo + vec2(0.025, 0.0), hi - vec2(0.0, 0.025), 0.06);
	float slat = smoothstep(0.35, 0.5, abs(fract(p.y / 0.025) - 0.5));
	vec3 blinds = mix(vec3(0.8, 0.82, 0.84), vec3(0.55, 0.57, 0.6), slat);
	c = mix(c, vec3(0.75, 0.76, 0.77), frame);
	c = mix(c, blinds, pane);
	c = mix(c, c * 0.6, reveal);
	glow += blinds * pane * (1.0 - reveal) * 0.25;
	return c;
}

// A door leaf in a steel frame with a vision panel, push plate and kick plate.
vec3 door(vec3 c, vec2 p, vec2 lo, vec2 hi) {
	float width = hi.x - lo.x;
	c = mix(c, vec3(0.2, 0.21, 0.22), inside(p, lo, hi, 0.0));
	c = mix(c, vec3(0.6, 0.62, 0.62), inside(p, lo, hi, 0.05));
	c = mix(c, vec3(0.42, 0.44, 0.46), inside(p, vec2(lo.x + 0.05, lo.y), vec2(hi.x - 0.05, lo.y + 0.3), 0.0));
	vec2 panel = vec2(lo.x + width * 0.5, hi.y - 0.55);
	c = mix(c, vec3(0.42, 0.44, 0.46), inside(p, panel - vec2(0.16, 0.3), panel + vec2(0.16, 0.3), 0.0));
	c = mix(c, vec3(0.12, 0.16, 0.2), inside(p, panel - vec2(0.16, 0.3), panel + vec2(0.16, 0.3), 0.02));
	vec2 plate = vec2(hi.x - 0.18, lo.y + 1.05);
	c = mix(c, vec3(0.45, 0.47, 0.5), inside(p, plate - vec2(0.05, 0.15), plate + vec2(0.05, 0.15), 0.0));
	return c;
}

// A wall service panel: a pale strip with a row of gas and power outlets, red and white by turns.
vec3 service_panel(vec3 c, vec2 p, vec2 lo, vec2 hi) {
	c = mix(c, vec3(0.62, 0.64, 0.66), inside(p, lo, hi, 0.0));
	float middle = (lo.y + hi.y) * 0.5;
	for (float x = lo.x + 0.1; x < hi.x - 0.05; x += 0.14) {
		vec3 outlet = mod(floor((x - lo.x) / 0.14), 2.0) < 1.0 ? vec3(0.55, 0.03, 0.03) : vec3(0.85, 0.86, 0.86);
		c = mix(c, outlet, inside(p, vec2(x - 0.035, middle - 0.035), vec2(x + 0.035, middle + 0.035), 0.0));
		c = mix(c, vec3(0.03), inside(p, vec2(x - 0.01, middle - 0.01), vec2(x + 0.01, middle + 0.01), 0.0));
	}
	return c;
}

// 20 cm glazed tiles up to the rail, roller-painted plaster above, a dark baseboard, a steel rail, then the features.
vec3 theatre_wall(vec3 tiles, vec3 p3, inout vec3 glow) {
	vec2 p = vec2(dot(p3.xz, wall_axis), p3.y);
	vec2 tile = p / 0.2;
	vec3 lower = tiles * (1.0 + (hash(floor(tile)) - 0.5) * 0.05) * (1.0 - 0.25 * grid_line(p, 0.2, 0.0015));
	float blotch = noise(p * 6.0) * 0.6 + noise(p * 17.0 + 5.0) * 0.4;
	float grain = noise(p * 400.0) * 0.6 + noise(p * 900.0 + 11.0) * 0.4;
	vec3 upper = wall_paint.rgb * (1.0 + (blotch - 0.5) * 0.06 + (grain - 0.5) * 0.1);
	float above = smoothstep(0.0, fwidth(p.y), p.y - RAIL);
	vec3 c = mix(lower, upper, above);
	c = mix(c, vec3(0.03, 0.05, 0.07), 1.0 - smoothstep(0.0, fwidth(p.y), p.y - BASEBOARD));
	float rail = 1.0 - smoothstep(0.0, fwidth(p.y), abs(p.y - RAIL) - 0.02);
	c = mix(c, mix(vec3(0.35, 0.37, 0.4), vec3(0.15, 0.16, 0.17), smoothstep(0.012, 0.02, abs(p.y - RAIL))), rail);
	for (int i = 0; i < 4; i++) {
		vec2 lo = wall_features[i].xy;
		vec2 hi = wall_features[i].zw;
		if (wall_feature_kinds[i] == 1) {
			c = window(c, p, lo, hi, glow);
		} else if (wall_feature_kinds[i] == 2) {
			c = door(c, p, lo, hi);
		} else if (wall_feature_kinds[i] == 3) {
			c = service_panel(c, p, lo, hi);
		}
	}
	return c;
}

// Applies the room pattern to base. Windows add their daylight to glow.
vec3 room_pattern(vec3 base, vec3 p, inout vec3 glow) {
	if (pattern == 1) {
		// Flecks a few millimeters across, some lighter and some darker than the sheet.
		float light_fleck = step(0.88, noise(p.xz * 160.0));
		float dark_fleck = step(0.88, noise(p.xz * 140.0 + 31.0));
		return base * (1.0 + light_fleck * 0.15 - dark_fleck * 0.18 + (noise(p.xz * 1.5) - 0.5) * 0.06);
	}
	if (pattern == 2) {
		return base * (1.0 - 0.35 * grid_line(p.xz, 0.6, 0.008));
	}
	if (pattern == 3) {
		return theatre_wall(base, p, glow);
	}
	return base;
}
