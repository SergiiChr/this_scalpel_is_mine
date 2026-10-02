// Surgical site skin where it's cut or pinched: the outer tissue layer, simulated by TissueSim, lit like the body.
// Damage comes from the two WoundMap textures, drawn by wound.gdshaderinc exactly like on the body around it.
// Openings aren't painted: the sim pulls cut edges apart and gaping triangles are left out of the mesh,
// so you look through the skin into the fat and muscle layers below.
shader_type spatial;

uniform vec4 skin_color : source_color = vec4(0.84, 0.66, 0.56, 1.0);
uniform sampler2D wound_map : filter_linear, hint_default_black;
uniform sampler2D fluid_map : filter_linear, hint_default_black;
uniform float rim_strength = 0.2;
uniform float specular_strength = 0.12;
uniform float grime = 0.12;
uniform float pallor = 0.0;
// Set from Materials.FAMILIES.skin, like the body's skin (toon.gdshader).
uniform float wrap = 0.0;
uniform vec4 scatter_tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
// Site space to the body model's own space: pores and grime are laid out where the body's are (toon.gdshader uses the
// model's space), so they run on unbroken where this skin takes over from the body.
uniform mat4 site_to_model;
// This skin lies right on the body model. Where the two overlap (at the edge of the region) it's moved this far
// (meters) toward the camera, along the view ray: it wins the depth test without being lifted off the body, so no
// step shows at its edge from any angle. The shadow passes see it moved the same way, so it doesn't shadow itself.
const float DEPTH_BIAS = 0.001;

varying vec3 world_pos;
varying vec3 local_pos;

#include "res://assets/shaders/wound.gdshaderinc"

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
	local_pos = (site_to_model * vec4(VERTEX, 1.0)).xyz;
	vec4 w = textureLod(wound_map, UV, 0.0);
	// Bruises and burns swell, blood stands on top. Cuts don't sink: here the mesh itself is split along them.
	VERTEX.y += w.b * 0.002 + smoothstep(0.1, 0.5, w.g) * 0.0015;
	VERTEX.y += blood_thickness(textureLod(fluid_map, UV, 0.0).r);
	vec3 camera = (inverse(MODEL_MATRIX) * vec4(MAIN_CAM_INV_VIEW_MATRIX[3].xyz, 1.0)).xyz;
	VERTEX += normalize(camera - VERTEX) * DEPTH_BIAS;
}

void fragment() {
	vec4 w = texture(wound_map, UV);
	// No painted groove: the sim splits the mesh exactly along a cut, so its lips are skin up to the edge. The groove
	// (and its pink rim) would lie right on them, a dark ring round every opening.
	w.r = 0.0;
	vec4 f = texture(fluid_map, UV);
	f.r = blood_amount(f.r, noise(world_pos.xz * 120.0 + world_pos.y * 50.0));
	NORMAL = blood_normal(NORMAL, VERTEX, blood_thickness(f.r));
	// The same pores as the body's skin around it (toon.gdshader, detail 1), so the two shade alike.
	NORMAL = blood_normal(NORMAL, VERTEX, (noise(local_pos.xz * 900.0 + local_pos.y * 300.0) + noise(local_pos.xy * 700.0) - 1.0) * 0.00004);
	float n = noise(local_pos.xz * 14.0) * 0.6 + noise(local_pos.xy * 6.3 + 7.0) * 0.4;
	vec3 c = mix(skin_color.rgb, skin_color.rgb * vec3(0.25, 0.22, 0.15) * 1.6, grime * smoothstep(0.35, 0.9, n));
	c = mix(c, vec3(0.78, 0.8, 0.8), pallor * 0.5);
	float wet = apply_wounds(c, w, f);
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
	// Dry skin gets the body's small dull highlight; only blood and open cuts (low roughness) shine wet.
	float wet = clamp((0.6 - ROUGHNESS) / 0.52, 0.0, 1.0);
	float spec_size = mix(mix(0.995, 0.9, ROUGHNESS), 0.994, wet);
	float spec = smoothstep(spec_size, (spec_size + 1.0) * 0.5, dot(NORMAL, halfway)) * ATTENUATION;
	SPECULAR_LIGHT += LIGHT_COLOR * spec * mix(specular_strength, 0.5, wet) / PI;
	float rim = pow(1.0 - max(dot(NORMAL, VIEW), 0.0), 3.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * rim * rim_strength * lit / PI;
}
