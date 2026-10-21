// Skin and blood share a continuous reflection lobe on the body and simulated sheet.
// Thresholded highlights turn small changes in a fold's slope into bright polygon patches.
float skin_specular(float facing, float wet) {
	float cosine = max(facing, 0.0);
	// Broad dry reflection, narrower wet film; both vary smoothly as the skin or lamp moves.
	return mix(pow(cosine, 18.0), pow(cosine, 96.0), wet);
}
