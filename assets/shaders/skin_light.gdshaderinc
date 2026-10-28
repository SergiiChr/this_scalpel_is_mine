// Skin and blood share a continuous reflection lobe on the body and simulated sheet.
// Thresholded highlights turn small changes in a fold's slope into bright polygon patches.
float skin_specular(float facing, float wet) {
	float cosine = max(facing, 0.0);
	// Broad dry reflection, narrower wet film; both vary smoothly as the skin or lamp moves.
	return mix(pow(cosine, 18.0), pow(cosine, 96.0), wet);
}

// Share of the light a wet film reflects at this angle between the normal and the view (Schlick): little face on,
// most of it at a grazing angle, which is what makes a wet surface read as wet.
float wet_fresnel(float facing) {
	float edge = 1.0 - clamp(facing, 0.0, 1.0);
	float edge2 = edge * edge;
	return 0.04 + 0.96 * edge2 * edge2 * edge;
}

// A sharp glint off a wet film, on top of the broad highlight: narrow, stronger at grazing angles.
float wet_glint(float n_dot_h, float n_dot_v, float wet) {
	return pow(max(n_dot_h, 0.0), 320.0) * wet_fresnel(n_dot_v) * wet * 6.0;
}

// Light through thin tissue from behind (`thin` 0..1, tinted in BACKLIGHT): lit from the far side, and toward the
// lamp when the view looks into it.
vec3 translucence(vec3 normal, vec3 light, vec3 view, vec3 backlight) {
	float back = max(-dot(normal, light), 0.0);
	float through = pow(max(dot(view, -light), 0.0), 4.0);
	return backlight * (back * 0.7 + through * 0.3);
}
