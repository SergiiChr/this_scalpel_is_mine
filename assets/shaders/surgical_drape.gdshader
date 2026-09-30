// Heavy woven surgical cloth. The mesh supplies the large folds and edge sag;
// this shader adds the smaller puckering and textile breakup that must remain
// attached to the sheet while the patient breathes or turns.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx, cull_disabled;

uniform vec4 cloth_color : source_color = vec4(0.22, 0.43, 0.40, 1.0);
uniform float fold_strength = 0.0022;

varying vec3 local_pos;

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
	local_pos = VERTEX;
	float broad = sin(VERTEX.x * 24.0 + VERTEX.z * 7.0) * 0.55;
	float crossed = sin(VERTEX.z * 39.0 - VERTEX.x * 5.0) * 0.28;
	float gathered = sin((VERTEX.x + VERTEX.z) * 73.0) * 0.17;
	VERTEX.y += (broad + crossed + gathered) * fold_strength;
}

void fragment() {
	float irregular = noise(local_pos.xz * 31.0) * 0.55 + noise(local_pos.xz * 118.0 + 9.0) * 0.45;
	float warp = sin(local_pos.x * 1450.0) * 0.5 + 0.5;
	float weft = sin(local_pos.z * 1320.0) * 0.5 + 0.5;
	float weave = warp * weft;
	float edge = min(min(UV.x, 1.0 - UV.x), min(UV.y, 1.0 - UV.y));
	vec3 c = cloth_color.rgb * mix(0.82, 1.08, irregular);
	c *= mix(0.94, 1.025, weave);
	c *= mix(0.72, 1.0, smoothstep(0.0, 0.045, edge));
	ALBEDO = c;
	ROUGHNESS = clamp(0.91 - weave * 0.05 + irregular * 0.03, 0.78, 0.96);
	SPECULAR = 0.18;
}

void light() {
	float wrapped = max((dot(NORMAL, LIGHT) + 0.24) / 1.24, 0.0);
	DIFFUSE_LIGHT += LIGHT_COLOR * wrapped * ATTENUATION / PI;
}
