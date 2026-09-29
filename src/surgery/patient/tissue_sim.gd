class_name TissueSim
extends RefCounted
## Position-based soft tissue for the surgical site skin.
## A grid of particles joined by springs, all in site-local space.
##
## - Springs are a little shorter than the grid spacing, so the skin is under tension.
##   Cut the springs along an incision and the edges pull apart on their own.
## - Every particle is weakly anchored to where it sits on the body (fascia).
##   Near an incision the anchor is loosened, like undermined skin.
## - Tools pin particles to their tip: forceps and retractors stretch the skin.
## - Springs stretched past their limit snap (host only). The host turns that into a tear.
## - Sutures add new springs across a cut. Their rest length is the stitch tension.
## - The sim sleeps when nothing moves, so an untouched patient costs nothing.

const RES := 24
const TENSION := 0.93
const ANCHOR := 0.05
const LOOSE_ANCHOR := 0.006
const LOOSE_RADIUS := 0.045
const DAMPING := 0.88
const ITERATIONS := 4
const TISSUE_BREAK := 1.9
const STEP := 1.0 / 30.0
const SLEEP_EPSILON := 0.00002
const SLEEP_STEPS := 20

enum Kind { TISSUE, STITCH }
## How deep a severed spring was cut: through the skin, into the fat, or through the muscle into the cavity.
enum Depth { NONE, SKIN, FAT, MUSCLE }
## A gap counts as open once it's pulled this far apart (meters).
const OPEN_GAP := 0.004

var size := Vector2.ONE
var rest := PackedVector3Array()
var pos := PackedVector3Array()
var prev := PackedVector3Array()
var anchor := PackedFloat32Array()
var fixed := PackedByteArray()
## Constraint arrays, one entry per spring.
var c_a := PackedInt32Array()
var c_b := PackedInt32Array()
var c_rest := PackedFloat32Array()
var c_active := PackedByteArray()
var c_kind := PackedByteArray()
var c_break := PackedFloat32Array()
var c_depth := PackedByteArray()
## Only the host decides when springs snap, so tears happen once for everyone.
var tearing := false
## Springs that snapped since the host last read them: [uv a, uv b, Kind].
var snapped: Array[Array] = []
## Scales how far past its rest length a spring stretches before it snaps (thin skin tears sooner).
var break_mult := 1.0
## Changes whenever springs are cut, stitched or snap (meshes rebuild their triangles).
var topology_version := 0
## Counts simulation steps, so meshes only rebuild when something moved.
var steps_done := 0

var _pins: Dictionary = {}
var _still_steps := 0
var _accumulator := 0.0
var _edge_to_stitch: Dictionary = {}
## Spring index of the grid edges leaving each particle: to the right, down, and the (i+1, j)-(i, j+1) diagonal.
var _right := PackedInt32Array()
var _down := PackedInt32Array()
var _diag := PackedInt32Array()
## Tissue springs that are cut or snapped. Gap and mesh queries only look at these.
var _severed := PackedInt32Array()
## 1 for particles the solver may move, 0 for the fixed border and pinned particles.
var _free := PackedFloat32Array()


## height_at(uv) -> skin height above the site plane.
func build(site_size: Vector2, height_at: Callable) -> void:
	size = site_size
	var count := (RES + 1) * (RES + 1)
	rest.resize(count)
	anchor.resize(count)
	fixed.resize(count)
	for j in RES + 1:
		for i in RES + 1:
			var uv := Vector2(float(i) / RES, float(j) / RES)
			var k := index(i, j)
			rest[k] = Vector3((uv.x - 0.5) * size.x, height_at.call(uv), (uv.y - 0.5) * size.y)
			anchor[k] = ANCHOR
			fixed[k] = 1 if i == 0 or j == 0 or i == RES or j == RES else 0
	pos = rest.duplicate()
	prev = rest.duplicate()
	_free.resize(count)
	_right.resize(count)
	_down.resize(count)
	_diag.resize(count)
	for j in RES + 1:
		for i in RES + 1:
			var k := index(i, j)
			if i < RES:
				_right[k] = _spring(k, index(i + 1, j))
			if j < RES:
				_down[k] = _spring(k, index(i, j + 1))
			if i < RES and j < RES:
				_diag[k] = _spring(index(i + 1, j), index(i, j + 1))
				_spring(k, index(i + 1, j + 1))


func index(i: int, j: int) -> int:
	return j * (RES + 1) + i


func uv_of(k: int) -> Vector2:
	return Vector2(float(k % (RES + 1)) / RES, float(k / (RES + 1)) / RES)


func nearest(uv: Vector2) -> int:
	var i := clampi(roundi(uv.x * RES), 1, RES - 1)
	var j := clampi(roundi(uv.y * RES), 1, RES - 1)
	return index(i, j)


func any_severed() -> bool:
	return not _severed.is_empty()


## Grid points where the simulated skin has to take over from the body model: within `reach` points of a cut
## or of skin a tool is holding. 1 inside, 0 outside, one byte per particle. Elsewhere the skin never moves.
func region(reach: int = 1) -> PackedByteArray:
	var out := PackedByteArray()
	out.resize(rest.size())
	var seeds := PackedInt32Array()
	for s in _severed:
		seeds.append(c_a[s])
		seeds.append(c_b[s])
	for key: int in _pins:
		seeds.append(_pins[key][0])
	for k in seeds:
		var i := k % (RES + 1)
		var j := k / (RES + 1)
		for y in range(maxi(j - reach, 0), mini(j + reach, RES) + 1):
			for x in range(maxi(i - reach, 0), mini(i + reach, RES) + 1):
				out[index(x, y)] = 1
	return out


func is_sleeping() -> bool:
	return _still_steps >= SLEEP_STEPS and _pins.is_empty()


func wake() -> void:
	_still_steps = 0


## Severs every tissue spring crossing the segment down to the given depth and loosens the skin around it.
func cut(a: Vector2, b: Vector2, depth: int) -> void:
	for s in c_a.size():
		if c_kind[s] != Kind.TISSUE:
			continue
		if Geometry2D.segment_intersects_segment(uv_of(c_a[s]), uv_of(c_b[s]), a, b) != null:
			_sever(s, depth)
	for k in rest.size():
		var uv := uv_of(k)
		if uv.distance_to(Geometry2D.get_closest_point_to_segment(uv, a, b)) < LOOSE_RADIUS:
			anchor[k] = LOOSE_ANCHOR
	topology_version += 1
	wake()


## Pins the particle nearest uv to follow a tool. Returns false if there is no tissue there.
func grip(key: int, uv: Vector2) -> bool:
	_pins[key] = [nearest(uv), pos[nearest(uv)]]
	wake()
	return true


func move_grip(key: int, target: Vector3) -> void:
	if _pins.has(key):
		_pins[key][1] = target


func release(key: int) -> void:
	_pins.erase(key)
	wake()


func grips() -> Array:
	return _pins.keys().map(func(key: int) -> Array: return [key, _pins[key][0], _pins[key][1]])


## Clients mirror the host's grips: [[key, particle, target], ...].
func set_grips(list: Array) -> void:
	_pins.clear()
	for entry: Array in list:
		_pins[entry[0]] = [entry[1], entry[2]]
	if not list.is_empty():
		wake()


## Closes the nearest severed spring near uv with a stitch. tension scales its rest length (loose > 1 > tight).
func stitch(uv: Vector2, tension: float, strength: float) -> bool:
	var best := -1
	var best_dist := 0.05
	for s in _severed:
		var mid := (uv_of(c_a[s]) + uv_of(c_b[s])) * 0.5
		var dist := mid.distance_to(uv)
		if dist < best_dist and not _stitched(c_a[s], c_b[s]):
			best_dist = dist
			best = s
	if best < 0:
		return false
	_spring(c_a[best], c_b[best], Kind.STITCH, tension, strength)
	topology_version += 1
	wake()
	return true


## Removes stitches near uv (a closure bursting open).
func burst(uv: Vector2, radius: float) -> void:
	for s in c_a.size():
		if c_kind[s] == Kind.STITCH and c_active[s] == 1 and ((uv_of(c_a[s]) + uv_of(c_b[s])) * 0.5).distance_to(uv) < radius:
			c_active[s] = 0
	topology_version += 1
	wake()


## Snaps the intact tissue spring nearest uv (mirrors a tear the host detected).
func sever_near(uv: Vector2, depth: int) -> void:
	var best := -1
	var best_dist := 0.06
	for s in c_a.size():
		if c_kind[s] == Kind.TISSUE and c_active[s] == 1:
			var dist := ((uv_of(c_a[s]) + uv_of(c_b[s])) * 0.5).distance_to(uv)
			if dist < best_dist:
				best_dist = dist
				best = s
	if best >= 0:
		_sever(best, depth)
		topology_version += 1
		wake()


## Seizures, coughs and bumps shake the tissue (visual, every peer).
func shake(amount: float) -> void:
	for k in pos.size():
		if fixed[k] == 0:
			prev[k] = pos[k] - Vector3(randf_range(-1, 1), randf_range(-1, 1), randf_range(-1, 1)) * amount
	wake()


## True when the tissue at uv is cut down to at least `depth` and pulled open.
func is_open(uv: Vector2, depth: int = Depth.MUSCLE) -> bool:
	return gap_at(uv, 0.04, depth) > OPEN_GAP


## Meters the tissue has pulled apart across springs severed at least `depth` deep near uv.
func gap_at(uv: Vector2, radius: float = 0.04, depth: int = Depth.SKIN) -> float:
	return gap_along(PackedVector2Array([uv]), radius, depth)


## Widest gap near a polyline (a wound), same rules as gap_at().
func gap_along(points: PackedVector2Array, radius: float, depth: int) -> float:
	var gap := 0.0
	for s in _severed:
		if c_depth[s] < depth:
			continue
		var a := c_a[s]
		var b := c_b[s]
		if _distance_to_line((uv_of(a) + uv_of(b)) * 0.5, points) < radius and not _stitched(a, b):
			gap = maxf(gap, pos[a].distance_to(pos[b]) - rest[a].distance_to(rest[b]))
	return gap


## Current stretch of the quad edge between two particles, relative to its rest length.
func stretch(a: int, b: int) -> float:
	return pos[a].distance_to(pos[b]) / maxf(rest[a].distance_to(rest[b]), 0.0001)


## Triangle indices of the grid, minus triangles spanning a gap cut at least `depth` deep and pulled open,
## so a layer mesh built from them shows a hole there.
func triangles(depth: int) -> PackedInt32Array:
	var open := PackedByteArray()
	open.resize(c_a.size())
	for s in _severed:
		var a := c_a[s]
		var b := c_b[s]
		if c_depth[s] >= depth and not _stitched(a, b) and pos[a].distance_to(pos[b]) - rest[a].distance_to(rest[b]) > OPEN_GAP * 0.5:
			open[s] = 1
	var out := PackedInt32Array()
	for j in RES:
		for i in RES:
			var a := index(i, j)
			var c := a + RES + 1
			if open[_right[a]] + open[_diag[a]] + open[_down[a]] == 0:
				out.append(a)
				out.append(a + 1)
				out.append(c)
			if open[_down[a + 1]] + open[_right[c]] + open[_diag[a]] == 0:
				out.append(a + 1)
				out.append(c + 1)
				out.append(c)
	return out


func step(delta: float) -> void:
	_accumulator = minf(_accumulator + delta, STEP * 4.0)
	while _accumulator >= STEP:
		_accumulator -= STEP
		if is_sleeping():
			_accumulator = 0.0
			return
		_substep()


func _substep() -> void:
	steps_done += 1
	var moved := 0.0
	for k in pos.size():
		if fixed[k] == 1:
			continue
		var p := pos[k]
		pos[k] = p + (p - prev[k]) * DAMPING
		prev[k] = p
	for k in pos.size():
		_free[k] = 1.0 - fixed[k]
	for key: int in _pins:
		_free[_pins[key][0]] = 0.0
	for iteration in ITERATIONS:
		for key: int in _pins:
			var pin: Array = _pins[key]
			pos[pin[0]] = pin[1]
		for s in c_a.size():
			if c_active[s] == 0:
				continue
			var a := c_a[s]
			var b := c_b[s]
			var wa := _free[a]
			var wb := _free[b]
			if wa + wb == 0.0:
				continue
			var d := pos[b] - pos[a]
			var length := d.length()
			if length < 0.00001:
				continue
			var correction := d * ((length - c_rest[s]) / (length * (wa + wb)))
			pos[a] += correction * wa
			pos[b] -= correction * wb
		for k in pos.size():
			pos[k] += (rest[k] - pos[k]) * anchor[k] * _free[k]
	for k in pos.size():
		moved = maxf(moved, pos[k].distance_squared_to(prev[k]))
	if tearing:
		_snap_overstretched()
	_still_steps = _still_steps + 1 if moved < SLEEP_EPSILON * SLEEP_EPSILON else 0


func _snap_overstretched() -> void:
	for s in c_a.size():
		if c_active[s] == 0:
			continue
		var a := c_a[s]
		var b := c_b[s]
		if pos[a].distance_to(pos[b]) / c_rest[s] > 1.0 + (c_break[s] - 1.0) * break_mult:
			if c_kind[s] == Kind.TISSUE:
				_sever(s, Depth.SKIN)
			else:
				c_active[s] = 0
			snapped.append([uv_of(a), uv_of(b), c_kind[s]])
			topology_version += 1


func _spring(a: int, b: int, kind: Kind = Kind.TISSUE, tension: float = TENSION, strength: float = TISSUE_BREAK) -> int:
	c_a.append(a)
	c_b.append(b)
	c_rest.append(rest[a].distance_to(rest[b]) * tension)
	c_active.append(1)
	c_kind.append(kind)
	c_break.append(strength)
	c_depth.append(Depth.NONE)
	if kind == Kind.STITCH:
		_edge_to_stitch[_edge_key(a, b)] = c_a.size() - 1
	return c_a.size() - 1


func _sever(s: int, depth: int) -> void:
	if c_active[s] == 1:
		c_active[s] = 0
		_severed.append(s)
	c_depth[s] = maxi(c_depth[s], depth)


func _stitched(a: int, b: int) -> bool:
	var s: int = _edge_to_stitch.get(_edge_key(a, b), -1)
	return s >= 0 and c_active[s] == 1


static func _distance_to_line(uv: Vector2, points: PackedVector2Array) -> float:
	if points.size() == 1:
		return uv.distance_to(points[0])
	var best := INF
	for i in range(1, points.size()):
		best = minf(best, uv.distance_to(Geometry2D.get_closest_point_to_segment(uv, points[i - 1], points[i])))
	return best


static func _edge_key(a: int, b: int) -> int:
	return mini(a, b) * 100000 + maxi(a, b)
