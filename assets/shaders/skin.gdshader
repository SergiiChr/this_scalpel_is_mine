// Surgical site skin where it's cut or pinched: the outer tissue layer, simulated by TissueSim, lit like the body.
// Damage and fluids use wound.gdshaderinc exactly like the surrounding body; a separate mask reveals closed seams.
// Openings aren't painted: the sim pulls cut edges apart and gaping triangles are left out of the mesh,
// so you look through the skin into the fat and muscle layers below.
shader_type spatial;

uniform vec4 skin_color : source_color = vec4(0.84, 0.66, 0.56, 1.0);
uniform sampler2D wound_map : filter_linear, hint_default_black;
uniform sampler2D fluid_map : filter_linear, hint_default_black;
uniform sampler2D seam_map : filter_linear, hint_default_black;
uniform sampler2D bleed_map : filter_linear, hint_default_black;
uniform float rim_strength = 0.2;
uniform float specular_strength = 0.12;
uniform float grime = 0.12;
uniform float pallor = 0.0;
// Attached runoff only: a rounded cross-section, whose mean is its conserved film depth.
instance uniform float rivulet_depth = 0.0;
// Shared with the volume-backed surface film through Materials.SkinSite.
uniform float blood_cover_depth = 0.00015;
// Set from Materials.FAMILIES.skin, like the body's skin (toon.gdshader).
uniform float wrap = 0.0;
uniform vec4 scatter_tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
// Site space to the body model's own space: pores and grime are laid out where the body's are (toon.gdshader uses the
// model's space), so they run on unbroken where this skin takes over from the body.
uniform mat4 site_to_model;
// How far breathing has lifted the site (model space) since site_to_model was set, sent alone every frame.
uniform vec3 site_lift;
// This skin lies right on the body model. Where the two overlap (at the edge of the region) it's moved this far
// (meters) toward the camera, along the view ray: it wins the depth test without being lifted off the body, so no
// step shows at its edge from any angle. The shadow passes see it moved the same way, so it doesn't shadow itself.
const float DEPTH_BIAS = 0.0002;

varying vec3 world_pos;
varying vec3 local_pos;
varying vec2 map_uv;
varying vec3 site_up;

#include "res://assets/shaders/wound.gdshaderinc"
#include "res://assets/shaders/skin_light.gdshaderinc"

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	map_uv = rivulet_depth > 0.0 ? UV2 : UV;
	vec3 site_normal = rivulet_depth > 0.0 ? (site_to_model * vec4(0.0, 1.0, 0.0, 0.0)).xyz : vec3(0.0, 1.0, 0.0);
	site_up = normalize((VIEW_MATRIX * MODEL_MATRIX * vec4(site_normal, 0.0)).xyz);
	local_pos = rivulet_depth > 0.0 ? VERTEX : (site_to_model * vec4(VERTEX, 1.0)).xyz + site_lift;
	// Not raised by bruises, burns or blood, like the body's skin (toon.gdshader): raised, this sheet would lift off
	// the walls down its cuts and off the layers under it, which don't rise with it, and step up where it meets the
	// body. Blood reads as standing on it from its lighting (blood_normal) alone. Cuts don't sink: the mesh itself is
	// split along them.
	vec3 camera = (inverse(MODEL_MATRIX) * vec4(MAIN_CAM_INV_VIEW_MATRIX[3].xyz, 1.0)).xyz;
	VERTEX += normalize(camera - VERTEX) * DEPTH_BIAS;
}

void fragment() {
	bool mapped = rivulet_depth <= 0.0 || (all(greaterThanEqual(map_uv, vec2(0.0))) && all(lessThanEqual(map_uv, vec2(1.0))));
	vec4 w = texture(wound_map, map_uv);
	// Open cuts are gaps in the simulated mesh, so painting their groove here would put a dark ring around the lips.
	// Only tied sutures mark joined edges; a low-quality closure in w.a is not a seam marker.
	w.r *= texture(seam_map, map_uv).r;
	vec4 f = texture(fluid_map, map_uv);
	float profile_depth = 0.0;
	float base_thickness = blood_thickness(f.r);
	if (rivulet_depth > 0.0) {
		if (!mapped) {
			w = vec4(0.0);
			f = vec4(0.0);
		}
		// Match the body projection: the far side must not inherit the blood above it.
		vec2 inside = min(map_uv, 1.0 - map_uv);
		f.r *= smoothstep(0.0, 0.1, min(inside.x, inside.y)) * smoothstep(-0.3, 0.2, dot(NORMAL, site_up));
		base_thickness = blood_thickness(f.r);
		float across = UV.x * 2.0 - 1.0;
		float section = max(1.0 - across * across, 0.0);
		// The rounded rim has zero slope at the skin, avoiding a repeated bright outline.
		// Integral of (1 - x*x)^2 on [-1, 1] is 16/15: peak 15/8 preserves the mean depth.
		profile_depth = 1.875 * rivulet_depth * section * section;
		// Add the tail's optical depth to the actual underlying film, retaining already-wet rims.
		f.r = 1.0 - (1.0 - f.r) * exp(-profile_depth / blood_cover_depth);
	}
	// Pigment can break up at the rim without turning that grain into separate shiny bumps.
	float thickness = max(profile_depth, base_thickness);
	f.r = blood_amount(f.r, noise(world_pos.xz * 120.0 + world_pos.y * 50.0));
	// Where a rivulet thins out at its sides it would only paint skin over the film or a neighbouring rivulet.
	if (rivulet_depth > 0.0 && f.r < 0.02) {
		discard;
	}
	NORMAL = blood_normal(NORMAL, VERTEX, thickness);
	// The same pores as the body's skin around it (toon.gdshader, detail 1), so the two shade alike.
	NORMAL = blood_normal(NORMAL, VERTEX, (noise(local_pos.xz * 900.0 + local_pos.y * 300.0) + noise(local_pos.xy * 700.0) - 1.0) * 0.00004);
	float n = noise(local_pos.xz * 14.0) * 0.6 + noise(local_pos.xy * 6.3 + 7.0) * 0.4;
	vec3 c = mix(skin_color.rgb, skin_color.rgb * vec3(0.25, 0.22, 0.15) * 1.6, grime * smoothstep(0.35, 0.9, n));
	c = mix(c, vec3(0.78, 0.8, 0.8), pallor * 0.5);
	float wet = apply_wounds(c, w, f);
	if (mapped) {
		wet = max(wet, paint_spot(c, skin_spot(texture(bleed_map, map_uv))));
	}
	ALBEDO = c;
	ROUGHNESS = mix(0.6, 0.08, wet);
}

// Same light as toon.gdshader so the site blends into the rest of the body.
void light() {
	float facing = dot(NORMAL, LIGHT);
	float ndl = max(facing, 0.0);
	float lit = ndl * ATTENUATION;
	float wrapped = max((facing + wrap) / (1.0 + wrap), 0.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * (lit + scatter_tint.rgb * (wrapped - ndl) * ATTENUATION) / PI;
	vec3 halfway = normalize(VIEW + LIGHT);
	// Dry skin gets the body's broad, dull highlight; blood and open cuts (low roughness) shine wet.
	float wet = clamp((0.6 - ROUGHNESS) / 0.52, 0.0, 1.0);
	float spec = skin_specular(dot(NORMAL, halfway), wet) * ATTENUATION;
	SPECULAR_LIGHT += LIGHT_COLOR * spec * mix(specular_strength, rivulet_depth > 0.0 ? 0.06 : 0.18, wet) / PI;
	float rim = pow(1.0 - max(dot(NORMAL, VIEW), 0.0), 3.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * rim * rim_strength * lit / PI;
}
