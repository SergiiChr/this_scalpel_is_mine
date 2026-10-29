// Layers under the skin, seen through an opened incision.
// layer 0 = subcutaneous fat (rounded lobules), layer 1 = muscle and fascia (red striated, white sheen),
// layer 2 = the cut face of the skin in the walls of a cut through it (the patient's skin tone, flecked with tiny vessels).
// Blood from the fluid map runs down into them, in streaks down a cut's walls. A wound bleeding inside the cut shows a
// spot on the walls at its depth (UV2.x: meters under the skin), its blood running down the wall from it (bleed_run()).
shader_type spatial;
render_mode cull_disabled;

#include "res://assets/shaders/wound.gdshaderinc"
#include "res://assets/shaders/skin_light.gdshaderinc"

uniform int layer = 0;
uniform bool cut_face = false;
uniform sampler2D fluid_map : filter_linear, hint_default_black;
uniform sampler2D bleed_map : filter_linear, hint_default_black;
// Layer 2 only: the skin's own color (Materials.skin_site()), so the cut face matches the skin around it.
uniform vec4 skin_color : source_color = vec4(0.84, 0.66, 0.56, 1.0);

varying vec3 local_pos;
varying float wall_depth;

float hash3(vec3 p) {
	return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453);
}

// Fine detail is baked into seamless 3D textures (tools/assetgen/tissue_textures.py) and sampled, not computed: the
// fat's cells took 27 neighbor cells and 81 hashes per pixel.
uniform sampler3D noise_texture : filter_linear_mipmap, repeat_enable;
uniform sampler3D cell_texture : filter_linear_mipmap, repeat_enable;
// How many noise lattice cells and fat cells one texture tile spans along each side: LATTICE and CELLS in
// tissue_textures.py.
const float NOISE_LATTICE = 16.0;
const float CELLS = 8.0;

// Smooth value noise, one lattice cell per unit.
float noise3(vec3 p) {
	return texture(noise_texture, p / NOISE_LATTICE).r;
}

// Distance to the nearest fat cell's center, one cell per unit. Three-dimensional cells stay rounded on horizontal
// surfaces and vertical incision walls.
float cells3(vec3 p) {
	return texture(cell_texture, p / CELLS).r;
}

void vertex() {
	local_pos = VERTEX;
	wall_depth = COLOR.r;
}

// The normal tilted by a height field over the surface (meters), from its screen-space slope.
vec3 bumped(vec3 normal, vec3 view_pos, float height) {
	vec3 dpdx = dFdx(view_pos);
	vec3 dpdy = dFdy(view_pos);
	vec3 r1 = cross(dpdy, normal);
	vec3 r2 = cross(normal, dpdx);
	float det = dot(dpdx, r1);
	vec3 grad = sign(det) * (dFdx(height) * r1 + dFdy(height) * r2);
	return normalize(abs(det) * normal - grad);
}

void fragment() {
	vec3 c;
	float wet;
	// Lobules and fibers are bumpy, so the lamp breaks into small glints instead of one mirror-like sheet.
	float height = 0.0;
	if (layer == 0) {
		float lobe = cells3(local_pos * 230.0);
		float membrane = smoothstep(0.47, 0.64, lobe);
		float mottling = noise3(local_pos * 55.0);
		c = mix(vec3(0.68, 0.44, 0.20), vec3(0.42, 0.24, 0.12), membrane * 0.42);
		c *= mix(0.86, 1.08, mottling);
		c = mix(c, vec3(0.82, 0.70, 0.52), smoothstep(0.58, 0.72, lobe) * 0.18);
		height = (1.0 - lobe) * 0.0006;
		wet = 0.46;
	} else if (layer == 2) {
		// The skin's color, a touch pinker, with its pores' fine relief and the odd tiny vessel.
		float grain = noise3(local_pos * 800.0);
		float speck = smoothstep(0.94, 1.0, hash3(floor(local_pos * 900.0)));
		c = mix(skin_color.rgb * vec3(0.88, 0.70, 0.67), vec3(0.52, 0.23, 0.22), wall_depth * 0.3) * mix(0.9, 1.02, grain);
		c = mix(c, vec3(0.62, 0.22, 0.22), speck * 0.35);
		height = (grain - 0.5) * 0.00008;
		wet = 0.42;
	} else {
		float warp = noise3(local_pos * 42.0) * 3.0;
		float fibers = sin(dot(local_pos, normalize(vec3(1.0, 0.16, 0.28))) * 430.0 + warp) * 0.5 + 0.5;
		float bundles = noise3(local_pos * 70.0);
		c = mix(vec3(0.40, 0.035, 0.055), vec3(0.62, 0.105, 0.09), fibers * 0.45 + bundles * 0.3);
		float fascia = smoothstep(0.91, 0.98, noise3(local_pos * 31.0 + vec3(7.0)));
		c = mix(c, vec3(0.72, 0.62, 0.57), fascia * 0.36);
		height = fibers * 0.0002;
		wet = 0.78;
	}
	if (cut_face) {
		// A thin connective membrane at the lip and darker recess below each layer break up the flat stripes.
		float connective = (1.0 - smoothstep(0.0, 0.16, wall_depth)) * (0.45 + 0.3 * noise3(local_pos * 350.0));
		c = mix(c, vec3(0.66, 0.48, 0.40), connective * 0.25);
		c *= mix(1.0, 0.62, smoothstep(0.55, 1.0, wall_depth));
	}
	NORMAL = bumped(NORMAL, VERTEX, height);
	// Down a cut's wall its uv stays that of the lip above: blood read there runs in straight columns, one per grid
	// cell. Wobbled by the tissue's own position, it lies in soft irregular patches instead.
	vec2 wobble = cut_face ? (vec2(noise3(local_pos * 90.0), noise3(local_pos * 90.0 + 13.0)) - 0.5) * 0.03 : vec2(0.0);
	float blood = smoothstep(0.05, 0.4, texture(fluid_map, UV + wobble).r);
	vec4 bleeds = texture(bleed_map, UV);
	if (cut_face) {
		// Blood coming over the lip from the skin runs a little way down the wall in streaks, not as one sheet down all
		// of it: a streak pattern fine across the wall and long down it, full at the lip, gone a centimeter under the
		// skin. A spot lower down shows clear of it, with its own blood running down from it (below).
		float streaks = noise3(vec3(local_pos.x * 420.0, local_pos.y * 35.0, local_pos.z * 420.0));
		float lip = 1.0 - smoothstep(0.002, 0.01, UV2.x + (streaks - 0.5) * 0.006);
		blood *= mix(1.0, smoothstep(0.38, 0.62, streaks), smoothstep(0.05, 0.3, wall_depth)) * lip;
		// What a bleed on this wall sheds runs down from its spot.
		blood = max(blood, bleed_run(bleeds, UV2.x, noise3(local_pos * 600.0)) * 0.9);
	}
	c = mix(c, vec3(0.22, 0.005, 0.02), blood * 0.76);
	float spot = cut_face ? paint_spot(c, bleed_spot(bleeds, UV2.x)) : 0.0;
	ALBEDO = c;
	// Blood takes the broad sheen off: under the lamp straight above it, that sheen would turn it grey.
	// The film on the tissue is patchy: drier and wetter spots break up the highlight. Not on blood, which a wetter
	// patch would turn grey the same way.
	float patchy = mix(0.75, 1.3, noise3(local_pos * 110.0 + 5.0));
	ROUGHNESS = mix((1.0 - max(wet, spot) * 0.82) * patchy, 1.0, blood * 0.85);
	EMISSION = ALBEDO * 0.025;
	// Only fat spreads light under its surface, softly: more blurs its cells away. Blood and bleeding vessels stay sharp,
	// with a margin around them so the blur doesn't draw them out.
	SSS_STRENGTH = layer == 0 ? 0.3 * (1.0 - min(max(blood, spot) * 2.0, 1.0)) : 0.0;
	// Fat and the skin's cut face let light through, muscle less so. A cut's wall is thinnest at the lip, where the
	// lamp shines through it.
	float thin = cut_face ? 1.0 - smoothstep(0.0, 0.3, wall_depth) : 0.2;
	BACKLIGHT = (layer == 1 ? vec3(0.6, 0.08, 0.06) : vec3(1.0, 0.45, 0.25)) * thin;
}

void light() {
	// The skin's cut face stands across the light from above: light through the skin keeps it from going dark.
	float wrap = layer == 2 ? 0.8 : 0.16;
	float ndl = max((dot(NORMAL, LIGHT) + wrap) / (1.0 + wrap), 0.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * ndl * ATTENUATION / PI;
	DIFFUSE_LIGHT += LIGHT_COLOR * translucence(NORMAL, LIGHT, VIEW, BACKLIGHT) * ATTENUATION / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	float n_dot_h = dot(NORMAL, halfway);
	float broad = pow(max(n_dot_h, 0.0), 18.0);
	float wet = clamp(1.0 - ROUGHNESS, 0.0, 1.0);
	SPECULAR_LIGHT += LIGHT_COLOR * (broad * wet * 0.32 + wet_glint(n_dot_h, dot(NORMAL, VIEW), wet)) * ATTENUATION / PI;
}
