class_name Drape
extends MeshInstance3D
## The surgical drape: a sheet laid over the torso and legs with an opening (fenestration) framing the surgical site.
## Built once from the body model at rest, in body space (patient along X, head at +X), a little off the skin so the
## body never pokes through it, even breathing. It covers the edge of the site, where the simulated skin meets the
## body, and the flanks the flat site can't follow. Hands rest on it (DRAPE_LAYER), tools and rays for limbs don't.

## Where the sheet lies, body space: from past the feet to just under the collarbones, and across the torso between
## the arms (they lie at about |z| 0.22 and may lift, so the sheet stays clear of them).
const FROM_X := -1.3
const TO_X := 0.44
const HALF_WIDTH := 0.185
const CELL := 0.02
## How far the sheet stays off the skin (meters): more than a breath lifts the torso under it.
const OFFSET := 0.012
## How far the sheet hangs over its edges.
const HEM := 0.025
## How much of the site's edge the sheet covers, as a share of the site: the fixed border of the simulated skin.
const FRAME := 0.04
const COLOR := Color(0.24, 0.42, 0.4)
const DRAPE_LAYER := 512


## meshes: the body model's meshes (skin, gown). site: the surgical site node, a child of root. up: +1 face up,
## -1 for a site on the back (the sheet goes on that side).
func build(root: Node3D, meshes: Array[MeshInstance3D], site: Node3D, site_size: Vector2, up: float) -> void:
	name = "Drape"
	var faces := PackedVector3Array()
	for mesh in meshes:
		var to_root := root.global_transform.affine_inverse() * mesh.global_transform
		for v in mesh.mesh.get_faces():
			faces.append(to_root * v)
	var body := TriangleMesh.new()
	body.create_from_faces(faces)
	var columns := ceili((TO_X - FROM_X) / CELL) + 1
	var rows := ceili(HALF_WIDTH * 2.0 / CELL) + 1
	var heights := _lay(body, columns, rows, up)
	# Grid points inside the opening move out onto its edge, so the opening is a clean rectangle, not grid steps.
	var points := PackedVector3Array()
	var inside := PackedByteArray()
	for j in rows:
		for i in columns:
			var p := _point(i, j, heights, columns)
			inside.append(1 if _in_opening(site, site_size, p) else 0)
			points.append(_to_edge(body, site, site_size, p, up) if inside[-1] == 1 else p)
	var st := SurfaceTool.new()
	st.begin(Mesh.PRIMITIVE_TRIANGLES)
	for j in rows - 1:
		for i in columns - 1:
			var ids: Array[int] = [j * columns + i, j * columns + i + 1, (j + 1) * columns + i + 1, (j + 1) * columns + i]
			if ids.all(func(k: int) -> bool: return inside[k] == 1):
				continue
			_quad(st, ids.map(func(k: int) -> Vector3: return points[k]), up)
	# Hems: the sheet hangs a little over its long sides and its ends instead of stopping in the air.
	for i in columns - 1:
		for j: int in [0, rows - 1]:
			var a := _point(i, j, heights, columns)
			var b := _point(i + 1, j, heights, columns)
			var out := Vector3(0, 0, -1.0 if j == 0 else 1.0) * 0.004
			_quad(st, [a, b, b + out + Vector3.DOWN * up * HEM, a + out + Vector3.DOWN * up * HEM], up if j == 0 else -up)
	for j in rows - 1:
		for i: int in [0, columns - 1]:
			var a := _point(i, j, heights, columns)
			var b := _point(i, j + 1, heights, columns)
			var out := Vector3(-1.0 if i == 0 else 1.0, 0, 0) * 0.004
			_quad(st, [a, b, b + out + Vector3.DOWN * up * HEM, a + out + Vector3.DOWN * up * HEM], -up if i == 0 else up)
	st.generate_normals()
	mesh = st.commit()
	var mat := Materials.family_unique("cloth", COLOR, 0.95)
	mat.next_pass = Materials.outline_for(0.002)
	material_override = mat
	cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_ON
	var solid := StaticBody3D.new()
	solid.collision_layer = DRAPE_LAYER
	solid.collision_mask = 0
	var shape := CollisionShape3D.new()
	shape.shape = mesh.create_trimesh_shape()
	solid.add_child(shape)
	add_child(solid)


## Height of the sheet at each grid point: the skin under it plus OFFSET. Over gaps (between the legs) it spans from
## side to side, sagging a little, never below the table.
func _lay(body: TriangleMesh, columns: int, rows: int, up: float) -> PackedFloat32Array:
	var heights := PackedFloat32Array()
	heights.resize(columns * rows)
	var hit := PackedByteArray()
	hit.resize(columns * rows)
	for j in rows:
		for i in columns:
			var at := Vector3(FROM_X + i * CELL, up * 0.5, -HALF_WIDTH + j * CELL)
			var result := body.intersect_ray(at, Vector3.DOWN * up)
			if not result.is_empty():
				heights[j * columns + i] = (result.position as Vector3).y + up * OFFSET
				hit[j * columns + i] = 1
	for j in rows:
		for i in columns:
			if hit[j * columns + i] == 1:
				continue
			# The nearest skin on either side across the sheet, lowered a little.
			var near := -INF if up > 0.0 else INF
			for step in rows:
				for jj: int in [j - step, j + step]:
					if jj >= 0 and jj < rows and hit[jj * columns + i] == 1:
						near = maxf(near, heights[jj * columns + i]) if up > 0.0 else minf(near, heights[jj * columns + i])
				if is_finite(near):
					break
			heights[j * columns + i] = near - up * 0.015 if is_finite(near) else -up * PatientBody.HALF_HEIGHT
	return heights


func _point(i: int, j: int, heights: PackedFloat32Array, columns: int) -> Vector3:
	return Vector3(FROM_X + i * CELL, heights[j * columns + i], -HALF_WIDTH + j * CELL)


## Inside the site, short of its edge by FRAME: left open for the surgery.
static func _in_opening(site: Node3D, site_size: Vector2, p: Vector3) -> bool:
	var local := site.transform.affine_inverse() * p
	var uv := Vector2(local.x / site_size.x + 0.5, local.z / site_size.y + 0.5)
	return uv.x > FRAME and uv.x < 1.0 - FRAME and uv.y > FRAME and uv.y < 1.0 - FRAME


## A point inside the opening moved to its nearest edge, back on the skin (plus OFFSET).
static func _to_edge(body: TriangleMesh, site: Node3D, site_size: Vector2, p: Vector3, up: float) -> Vector3:
	var local := site.transform.affine_inverse() * p
	var uv := Vector2(local.x / site_size.x + 0.5, local.z / site_size.y + 0.5)
	var gaps := [uv.x - FRAME, 1.0 - FRAME - uv.x, uv.y - FRAME, 1.0 - FRAME - uv.y]
	match gaps.find(gaps.min()):
		0: uv.x = FRAME
		1: uv.x = 1.0 - FRAME
		2: uv.y = FRAME
		3: uv.y = 1.0 - FRAME
	var edge := site.transform * Vector3((uv.x - 0.5) * site_size.x, local.y, (uv.y - 0.5) * site_size.y)
	var hit := body.intersect_ray(Vector3(edge.x, up * 0.5, edge.z), Vector3.DOWN * up)
	return Vector3(edge.x, (hit.position as Vector3).y + up * OFFSET if not hit.is_empty() else p.y, edge.z)


## Two triangles, wound so the side facing `up` is the front.
static func _quad(st: SurfaceTool, c: Array, up: float) -> void:
	var order := PackedInt32Array([0, 1, 2, 0, 2, 3]) if up > 0.0 else PackedInt32Array([0, 2, 1, 0, 3, 2])
	for n in order:
		st.add_vertex(c[n])
