class_name BloodFlow
extends Node3D
## Blood as a fluid, on every peer. Bleeding wounds (sources, synced by the host) release rivulets that run downhill
## over the skin, staining it as they go (the fluid map). Where a rivulet runs off the body it drips: droplets fall
## to the table or the floor and collect into pools that grow. Strong bleeds also spurt droplets into the air.
## Purely visual and local: each peer runs its own, so the stains differ a little between players, which is fine.

const MAX_RIVULETS := 48
const MAX_DROPS := 96
## Rivulets per ml of blood lost.
const RIVULETS_PER_ML := 0.35
const RIVULET_SPEED := 0.05
const STAIN_RADIUS := 0.0045
const DROP_RADIUS := 0.0035
## Bleeding faster than this (ml/s) spurts.
const SPURT_RATE := 3.0
const POOL_MERGE := 0.06
const POOL_MAX_RADIUS := 0.25
const FLOOR_Y := 0.004

var body: PatientBody
## [[uv, ml per second], ...] of the wounds bleeding onto the skin.
var sources: Array = []

var _rivulets: Array[Dictionary] = []
var _drops: Array[Dictionary] = []
var _pools: Array[MeshInstance3D] = []
var _spawn_acc: Dictionary = {}
var _drop_mesh: MultiMeshInstance3D
var _rng := RandomNumberGenerator.new()


func setup(patient_body: PatientBody) -> void:
	body = patient_body
	_rng.seed = 7
	_drop_mesh = MultiMeshInstance3D.new()
	var multimesh := MultiMesh.new()
	multimesh.transform_format = MultiMesh.TRANSFORM_3D
	var sphere := SphereMesh.new()
	sphere.radius = DROP_RADIUS
	sphere.height = DROP_RADIUS * 2.6
	sphere.radial_segments = 6
	sphere.rings = 4
	multimesh.mesh = sphere
	multimesh.instance_count = MAX_DROPS
	multimesh.visible_instance_count = 0
	_drop_mesh.multimesh = multimesh
	_drop_mesh.material_override = Materials.blood_pool()
	_drop_mesh.top_level = true
	_drop_mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(_drop_mesh)


func _process(delta: float) -> void:
	if body == null or delta <= 0.0:
		return
	_spawn(delta)
	_flow(delta)
	_fall(delta)


func _spawn(delta: float) -> void:
	for i in sources.size():
		var source: Array = sources[i]
		var uv: Vector2 = source[0]
		var rate: float = source[1]
		var acc: float = _spawn_acc.get(i, 0.0) + rate * delta * RIVULETS_PER_ML
		while acc >= 1.0 and _rivulets.size() < MAX_RIVULETS:
			acc -= 1.0
			var jitter := Vector2(_rng.randf_range(-1, 1), _rng.randf_range(-1, 1)) * 0.01
			_rivulets.append({"uv": uv + jitter, "volume": _rng.randf_range(0.6, 1.2), "wander": _rng.randf_range(-1, 1)})
		_spawn_acc[i] = minf(acc, 3.0)
		if rate > SPURT_RATE and _rng.randf() < delta * (rate - SPURT_RATE) * 1.5:
			var up := body.site.global_basis.y
			var spray := up * _rng.randf_range(0.6, 1.3) + Vector3(_rng.randf_range(-0.4, 0.4), 0.0, _rng.randf_range(-0.4, 0.4))
			_add_drop(body.uv_to_world(uv), spray)


## Rivulets slide downhill over the skin: gravity along the skin's own slope, wandering a little.
func _flow(delta: float) -> void:
	var down := body.site.global_basis.inverse() * Vector3.DOWN
	for rivulet in _rivulets.duplicate():
		var uv: Vector2 = rivulet.uv
		var slope := _slope(uv)
		var along := Vector2(down.x, down.z) - slope * down.y
		if along.length() < 0.05:
			along = Vector2(rivulet.wander, 1.0) * 0.05
		var dir := along.normalized().rotated(sin(Time.get_ticks_msec() * 0.003 + rivulet.wander * 9.0) * 0.4)
		var step := dir * RIVULET_SPEED * delta / ((body.site_size.x + body.site_size.y) * 0.5)
		var next := uv + step
		body.wound_map.stroke(WoundMap.Layer.FLUIDS, WoundMap.BLOOD, uv, next, STAIN_RADIUS, 0.55, WoundMap.Mode.MAX)
		rivulet.uv = next
		rivulet.volume -= delta * 0.25
		if next.x < 0.0 or next.y < 0.0 or next.x > 1.0 or next.y > 1.0:
			# Ran off the site: it drips from here.
			_add_drop(body.uv_to_world(next.clamp(Vector2.ZERO, Vector2.ONE)), Vector3.ZERO)
			_rivulets.erase(rivulet)
		elif rivulet.volume <= 0.0:
			_rivulets.erase(rivulet)


## Skin slope at uv (height change per uv unit), from the baked skin heights.
func _slope(uv: Vector2) -> Vector2:
	const E := 0.02
	var dx := body.surface_height(uv + Vector2(E, 0)) - body.surface_height(uv - Vector2(E, 0))
	var dy := body.surface_height(uv + Vector2(0, E)) - body.surface_height(uv - Vector2(0, E))
	return Vector2(dx / body.site_size.x, dy / body.site_size.y) / (2.0 * E) * 8.0


func _add_drop(at: Vector3, velocity: Vector3) -> void:
	if _drops.size() < MAX_DROPS:
		_drops.append({"pos": at, "vel": velocity})


## Droplets fall under gravity and land on the table top or the floor, where they pool.
func _fall(delta: float) -> void:
	var table_top := Room.TABLE_HEIGHT + 0.07
	for drop in _drops.duplicate():
		drop.vel += Vector3.DOWN * 9.8 * delta
		var pos: Vector3 = drop.pos + drop.vel * delta
		var on_table := absf(pos.x) < 1.0 and absf(pos.z) < 0.31
		var ground := table_top if on_table and (drop.pos as Vector3).y >= table_top else FLOOR_Y
		if pos.y <= ground:
			_pool(Vector3(pos.x, ground, pos.z))
			_drops.erase(drop)
		else:
			drop.pos = pos
	var multimesh := _drop_mesh.multimesh
	multimesh.visible_instance_count = _drops.size()
	for i in _drops.size():
		multimesh.set_instance_transform(i, Transform3D(Basis.IDENTITY, _drops[i].pos))


## Adds a drop's worth of blood to the nearest pool, or starts a new one. Pools grow with the square root of volume.
func _pool(at: Vector3) -> void:
	for pool in _pools:
		if pool.position.distance_to(at) < POOL_MERGE + pool.scale.x:
			var area := pool.scale.x * pool.scale.x + 0.0004
			var radius := minf(sqrt(area), POOL_MAX_RADIUS)
			pool.scale = Vector3(radius, 1.0, radius)
			return
	var mesh := MeshInstance3D.new()
	var disc := CylinderMesh.new()
	disc.top_radius = 1.0
	disc.bottom_radius = 1.0
	disc.height = 0.002
	disc.radial_segments = 14
	disc.rings = 1
	mesh.mesh = disc
	mesh.material_override = Materials.blood_pool()
	mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	mesh.top_level = true
	add_child(mesh)
	mesh.position = at + Vector3(0, 0.001, 0)
	mesh.scale = Vector3(0.02, 1.0, 0.02)
	_pools.append(mesh)
