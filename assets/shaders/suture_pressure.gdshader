// Small compression creases around a suture puncture. One shared plane/texture is instanced at every hole; pressure
// controls its opacity and size in PatientBody. The camera offset matches simulated skin and thread geometry.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_prepass_alpha;

uniform sampler2D pressure_texture : source_color, filter_linear_mipmap;
instance uniform float pressure : hint_range(0.0, 1.0) = 0.0;
const float CAMERA_BIAS = 0.001;

void vertex() {
	vec3 camera = (inverse(MODEL_MATRIX) * vec4(MAIN_CAM_INV_VIEW_MATRIX[3].xyz, 1.0)).xyz;
	VERTEX += normalize(camera - VERTEX) * CAMERA_BIAS;
}

void fragment() {
	float ink = texture(pressure_texture, UV).a;
	vec3 faint_red = vec3(0.48, 0.12, 0.13);
	vec3 compressed_red = vec3(0.32, 0.025, 0.035);
	ALBEDO = mix(faint_red, compressed_red, pressure);
	ALPHA = ink * mix(0.24, 0.92, pressure) * smoothstep(0.0, 0.08, pressure);
}
