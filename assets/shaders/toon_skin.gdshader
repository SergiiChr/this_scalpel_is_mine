// The game's own shading (toon.gdshaderinc) for skin, with light spread under it (Materials.Families Subsurface).
shader_type spatial;
// Light spread under skin is red: it reaches further than the other colors.
render_mode sss_mode_skin;

#define SUBSURFACE
#include "res://assets/shaders/toon.gdshaderinc"
