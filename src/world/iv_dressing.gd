class_name IvDressing
extends Node3D
## Where an IV line goes into the arm: the catheter's stub going into the skin toward the elbow, its hub with a colored
## cap and wings, a clear film over the site, two strips of woven tape across the arm (one over the hub, one holding
## the tubing down) and the tubing taped along the arm, hanging off to the stand from EXIT.
##
## Local space (see PatientBody.iv_site()): the catheter goes in at the origin on the skin, X runs along the arm toward
## the elbow, Y out of the skin, Z across the arm. Everything wraps around the arm as a cylinder of build()'s radius.

## Where the hub sits along the arm (toward the hand, behind where the catheter goes in) and how thick it is.
const HUB := Vector2(-0.028, -0.012)
const HUB_RADIUS := 0.0032
## Where the tubing taped along the arm ends and starts hanging (IvLine starts here).
const EXIT := Vector3(-0.085, IvLine.RADIUS, 0.0)
## Tape strips across the arm: [from x, to x, half span across].
## Kept to the top of the arm: a real arm is flatter than the cylinder they wrap around, wider ones would lift off.
const TAPES: Array[Vector3] = [Vector3(-0.03, -0.019, 0.024), Vector3(-0.07, -0.057, 0.022)]
const FILM := Rect2(-0.032, -0.018, 0.046, 0.036)
const TAPE_COLOR := Color(0.95, 0.95, 0.92)
const STRIPE_COLOR := Color(0.25, 0.6, 0.6)
const CAP_COLOR := Color(0.85, 0.82, 0.2)
const PLASTIC_COLOR := Color(0.88, 0.92, 0.94)

var _radius := 0.035


## The tubing's look, shared with the hanging part (IvLine).
static func tubing_material() -> ShaderMaterial:
	return Materials.toon(Color(0.82, 0.88, 0.9), 0.1, false, 0.2)


func build(arm_radius: float) -> void:
	_radius = arm_radius
	var plastic := Materials.toon(PLASTIC_COLOR, 0.05, false, 0.2)
	# The visible end of the catheter, going into the skin at a shallow angle.
	_add(Shapes.tube(PackedVector3Array([Vector3(0.004, -0.0012, 0.0), Vector3(HUB.y, HUB_RADIUS * 0.8, 0.0)]), 0.0007, 0.0007), plastic)
	var hub := PackedVector3Array([Vector3(HUB.y, HUB_RADIUS, 0.0), Vector3(HUB.x, HUB_RADIUS, 0.0)])
	_add(Shapes.tube(hub, HUB_RADIUS, HUB_RADIUS), plastic)
	_add(Shapes.tube(PackedVector3Array([Vector3(HUB.y - 0.001, HUB_RADIUS, 0.0), Vector3(HUB.y - 0.005, HUB_RADIUS, 0.0)]), HUB_RADIUS * 1.15, HUB_RADIUS * 1.15), Materials.toon(CAP_COLOR, 0.05, false, 0.3))
	_add(_patch(Rect2(HUB.y - 0.008, -0.009, 0.007, 0.018), 0.0005, 0.0006, false), plastic)
	# Taped along the arm from the hub, then off toward the stand.
	var taped := PackedVector3Array()
	for i in 6:
		var x := lerpf(HUB.x, EXIT.x, i / 5.0)
		taped.append(Vector3(x, IvLine.RADIUS, 0.0))
	_add(Shapes.tube(taped, IvLine.RADIUS, IvLine.RADIUS), tubing_material())
	_add(_patch(FILM, 0.0006, 0.0002), _film_material())
	var tape := Materials.family_unique("cloth", TAPE_COLOR, 0.95)
	for strip in TAPES:
		_add(_patch(Rect2(strip.x, -strip.z, strip.y - strip.x, strip.z * 2.0), 0.0007, 0.0005), tape)
	# The printed band on the tape over the hub.
	var middle := (TAPES[0].x + TAPES[0].y) * 0.5
	_add(_patch(Rect2(middle - 0.0015, -TAPES[0].z, 0.003, TAPES[0].z * 2.0), 0.0013, 0.0002), Materials.toon(STRIPE_COLOR, 0.1, false, 0.9))


func _add(mesh: Mesh, material: Material) -> void:
	var part := MeshInstance3D.new()
	part.mesh = mesh
	part.material_override = material
	add_child(part)


## How high whatever lies on the skin reaches under a strip at (x, z): the hub with its wings, the tubing.
func _under(x: float, z: float) -> float:
	var height := 0.0
	if x > HUB.x - 0.002 and x < HUB.y + 0.002:
		height = maxf(height, HUB_RADIUS * 2.0 * sqrt(maxf(1.0 - pow(z / (HUB_RADIUS * 1.6), 2.0), 0.0)))
	if x < HUB.x and x > EXIT.x - 0.005:
		height = maxf(height, IvLine.RADIUS * 2.0 * sqrt(maxf(1.0 - pow(z / (IvLine.RADIUS * 1.6), 2.0), 0.0)))
	return height


## A point `lift` off the arm's skin at x along the arm and z across it (arc length), the arm a cylinder around X.
func _on_arm(x: float, z: float, lift: float) -> Vector3:
	var angle := z / _radius
	return Vector3(x, (_radius + lift) * cos(angle) - _radius, (_radius + lift) * sin(angle))


## A thin sheet (film, tape, the catheter's wings) over `area` (x along the arm, y across it), laid on the skin `lift`
## up and, if it drapes, over what's under it, `thickness` thick. Only its top is drawn: the rest faces the skin.
func _patch(area: Rect2, lift: float, thickness: float, drapes: bool = true) -> ArrayMesh:
	var columns := maxi(2, ceili(area.size.x / 0.002))
	var rows := maxi(2, ceili(area.size.y / 0.002))
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	var grid: Array[PackedVector3Array] = []
	for i in columns + 1:
		var line := PackedVector3Array()
		for j in rows + 1:
			var x := area.position.x + area.size.x * i / columns
			var z := area.position.y + area.size.y * j / rows
			line.append(_on_arm(x, z, lift + thickness + (_under(x, z) if drapes else 0.0)))
		grid.append(line)
	for i in columns:
		for j in rows:
			# Facing out of the skin: Godot's front faces wind clockwise.
			for v: Vector3 in [grid[i][j], grid[i + 1][j], grid[i][j + 1], grid[i + 1][j], grid[i + 1][j + 1], grid[i][j + 1]]:
				st.add_vertex(v)
	st.generate_normals()
	return st.commit()


static func _film_material() -> StandardMaterial3D:
	var film := StandardMaterial3D.new()
	film.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	film.albedo_color = Color(0.94, 0.96, 0.98, 0.28)
	film.roughness = 0.15
	film.metallic_specular = 0.7
	return film
