class_name TissueSim
extends RefCounted
## Position-based soft tissue for the surgical site skin.
## A grid of particles joined by springs, all in site-local space.
##
## - Springs are a little shorter than the grid spacing, so the skin is under tension.
##   Cut the springs along an incision and the edges pull apart on their own.
## - Every particle is weakly anchored to where it sits on the body (fascia).
##   Near an incision the anchor is loosened, like undermined skin.
## - Tools pin particles to their tip: forceps and retractors stretch the skin. A grip drags a patch of skin
##   around it along with falloff (a pinched fold, not a single point), so a pull spreads out and the skin stretches
##   over a wide area instead of tearing right beside the tip.
## - Springs stretched past their limit snap (host only). The host turns that into a tear.
## - Sutures add new springs across a cut. Their rest length is the stitch tension. A cut only counts as closed
##   where its edges have actually come together: a loose stitch leaves a gap that stays open (and bleeds).
## - A cut through the muscle retracts: the muscle pulls the edges further apart until it's stitched itself
##   (muscle_stitch()). Skin can't be closed over open muscle, see Patient.close_at().
## - The sim sleeps when nothing moves, so an untouched patient costs nothing.

const RES := 24
const TENSION := 0.93
const ANCHOR := 0.02
## Skin pulled this far (meters) from its spot isn't held there any more, see _substep().
const ANCHOR_REACH := 0.05
## How far cut muscle pulls each edge back from the cut, and how firmly (meters, anchor strength).
const MUSCLE_RETRACT := 0.015
const MUSCLE_PULL := 0.05
const LOOSE_ANCHOR := 0.006
const LOOSE_RADIUS := 0.045
## How far (uv) a grip drags the skin around it along, and how firmly per solver iteration at its center.
const GRIP_PATCH := 0.15
const GRIP_DRAG := 1.0
## Pulls up to this far (meters) drag the patch fully, twice as far not at all.
const DRAG_REACH := 0.035
## Skin that moved further than this (meters) from where it settled is shown simulated.
const REGION_MOVE := 0.001
const DAMPING := 0.88
const ITERATIONS := 4
## Extra solver passes over the stitches each iteration. Thread is far stiffer than skin: solved as often as the
## skin, a stitch would give way to the stretched skin around it and the edges would never meet.
const STITCH_PASSES := 3
const TISSUE_BREAK := 2.3
const STEP := 1.0 / 30.0
const SLEEP_EPSILON := 0.00002
const SLEEP_STEPS := 20
## Most solver steps the skin gets to settle at build time (see _settle()).
const SETTLE_STEPS := 300

enum Kind { TISSUE, STITCH }
## How deep a severed spring was cut: through the skin, into the fat, or through the muscle into the cavity.
enum Depth { NONE, SKIN, FAT, MUSCLE }
## A gap counts as open once it's pulled this far apart (meters).
const OPEN_GAP := 0.004
## Cells per side of the grid that sorts skin triangles by where they lie now, for skin_height().
## It covers the site and a margin around it, since pulled skin can leave the site's rectangle.
const BINS := 16
const BIN_MARGIN := 0.1

var size := Vector2.ONE
var rest := PackedVector3Array()
var pos := PackedVector3Array()
var prev := PackedVector3Array()
var anchor := PackedFloat32Array()
var fixed := PackedByteArray()
## 1 for particles where the site hangs off the body (past a limb's or the flank's edge): never drawn and never part
## of the region, so the site can't stick out of the body. They're still simulated, so skin next to them moves as
## it always did.
var off := PackedByteArray()
## Constraint arrays, one entry per spring.
var c_a := PackedInt32Array()
var c_b := PackedInt32Array()
var c_rest := PackedFloat32Array()
var c_active := PackedByteArray()
var c_kind := PackedByteArray()
var c_break := PackedFloat32Array()
var c_depth := PackedByteArray()
## 1 for springs cut through the muscle whose muscle has been stitched: they count as cut only into the fat.
var c_muscle_closed := PackedByteArray()
## Where the anchor pulls each particle: its rest position, moved back from a cut through open muscle.
var anchor_target := PackedVector3Array()
## Where each particle rests under the skin's own tension before anything touches it (see _settle()).
var settled := PackedVector3Array()
## Only the host decides when springs snap, so tears happen once for everyone.
var tearing := false
## Springs that snapped since the host last read them: [uv a, uv b, Kind, spring index].
## Springs are only ever appended, so the index names the same spring on every peer (see snap_spring()).
var snapped: Array[Array] = []
## Scales how far past its rest length a spring stretches before it snaps (thin skin tears sooner).
var break_mult := 1.0
## Changes whenever springs are cut, stitched or snap (meshes rebuild their triangles).
var topology_version := 0
## Counts simulation steps, so meshes only rebuild when something moved.
var steps_done := 0

## key -> [particle, target]
var _pins: Dictionary = {}
## Particle -> [particles, weights] it drags along when gripped, for the topology it was worked out for.
var _patches: Dictionary = {}
var _patch_version := -1
## Spring indices touching each particle.
var _springs_of: Array[PackedInt32Array] = []
var _still_steps := 0
var _accumulator := 0.0
var _edge_to_stitch: Dictionary = {}
## Spring index of the grid edges leaving each particle: to the right, down, and the (i+1, j)-(i, j+1) diagonal.
var _right := PackedInt32Array()
var _down := PackedInt32Array()
var _diag := PackedInt32Array()
## Tissue springs that are cut or snapped. Gap and mesh queries only look at these.
var _severed := PackedInt32Array()
## Every stitch spring, active or not.
var _stitches := PackedInt32Array()
## 1 for particles the solver may move, 0 for the fixed border and pinned particles.
var _free := PackedFloat32Array()
## Skin triangles (particle indices) and, per bin, the first index in them of each triangle overlapping it.
var _bin_triangles := PackedInt32Array()
var _bins: Array[PackedInt32Array] = []
## [topology_version, steps_done] the bins were sorted for.
var _bins_for := [-1, -1]


## height_at(uv) -> skin height above the site plane. on_body(uv) -> false where the site is off the body.
func build(site_size: Vector2, height_at: Callable, on_body: Callable = Callable()) -> void:
	size = site_size
	var count := (RES + 1) * (RES + 1)
	rest.resize(count)
	anchor.resize(count)
	fixed.resize(count)
	off.resize(count)
	for j in RES + 1:
		for i in RES + 1:
			var uv := Vector2(float(i) / RES, float(j) / RES)
			var k := index(i, j)
			rest[k] = Vector3((uv.x - 0.5) * size.x, height_at.call(uv), (uv.y - 0.5) * size.y)
			anchor[k] = ANCHOR
			off[k] = 0 if on_body.is_null() or on_body.call(uv) else 1
			fixed[k] = 1 if i == 0 or j == 0 or i == RES or j == RES else 0
	pos = rest.duplicate()
	prev = rest.duplicate()
	anchor_target = rest.duplicate()
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
	_settle()


## Lets the skin settle under its own tension before anything touches it. Over a curved body tension pulls the sheet a
## few millimeters off the body's shape at the site's edges; that's where it rests, not a movement (see region()).
## A flat site is settled at once and falls asleep within SLEEP_STEPS.
func _settle() -> void:
	for step_index in SETTLE_STEPS:
		if is_sleeping():
			break
		_substep()
	settled = pos.duplicate()
	prev = pos.duplicate()
	steps_done = 0


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


## Grid points where the simulated skin has to take over from the body model: within `reach` points of a cut,
## of skin a tool is holding, or of skin that moved visibly. 1 inside, 0 outside, one byte per particle.
func region(reach: int = 1) -> PackedByteArray:
	var out := PackedByteArray()
	out.resize(rest.size())
	var seeds := PackedInt32Array()
	for s in _severed:
		seeds.append(c_a[s])
		seeds.append(c_b[s])
	for key: int in _pins:
		seeds.append(_pins[key][0])
	for k in pos.size():
		if pos[k].distance_squared_to(settled[k]) > REGION_MOVE * REGION_MOVE:
			seeds.append(k)
	for k in seeds:
		var i := k % (RES + 1)
		var j := k / (RES + 1)
		for y in range(maxi(j - reach, 0), mini(j + reach, RES) + 1):
			for x in range(maxi(i - reach, 0), mini(i + reach, RES) + 1):
				out[index(x, y)] = 1
	# Off the body, and right next to it: triangles touching skin off the body aren't drawn, so the body model has to
	# cover up to there, or its cut-away edge (halfway between region points) would leave a gap.
	for k in out.size():
		if off[k] == 1:
			var i := k % (RES + 1)
			var j := k / (RES + 1)
			for y in range(maxi(j - 1, 0), mini(j + 1, RES) + 1):
				for x in range(maxi(i - 1, 0), mini(i + 1, RES) + 1):
					out[index(x, y)] = 0
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
	_update_retraction()
	topology_version += 1
	wake()


## Stitches the muscle under every spring cut through it near uv. Returns how many springs it closed.
func muscle_stitch(uv: Vector2, radius: float) -> int:
	var closed := 0
	for s in _severed:
		if c_depth[s] == Depth.MUSCLE and c_muscle_closed[s] == 0 and ((uv_of(c_a[s]) + uv_of(c_b[s])) * 0.5).distance_to(uv) < radius:
			c_muscle_closed[s] = 1
			closed += 1
	if closed > 0:
		_update_retraction()
		topology_version += 1
		wake()
	return closed


## True while muscle cut near uv hasn't been stitched: skin closed over it would be under too much tension.
func muscle_open_near(uv: Vector2, radius: float) -> bool:
	for s in _severed:
		if depth_of(s) == Depth.MUSCLE and ((uv_of(c_a[s]) + uv_of(c_b[s])) * 0.5).distance_to(uv) < radius:
			return true
	return false


## How deep spring s counts as cut: stitched muscle leaves only skin and fat open.
func depth_of(s: int) -> int:
	return Depth.FAT if c_muscle_closed[s] == 1 else c_depth[s]


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


## Snaps spring s exactly as the host's sim did (clients mirror host tears). Searching by position instead would be
## ambiguous: both diagonals of a grid cell share a midpoint, and several stitches can sit within any radius.
func snap_spring(s: int) -> void:
	if s < 0 or s >= c_a.size():
		push_error("Tissue out of sync: no spring %d (%d springs)" % [s, c_a.size()])
		return
	if c_kind[s] == Kind.TISSUE:
		_sever(s, Depth.SKIN)
	else:
		c_active[s] = 0
	topology_version += 1
	wake()


## Changes with every cut, stitch, burst or snap, and is the same on peers whose tissue was cut the same way.
func topology_hash() -> int:
	return hash([c_active, c_depth, c_muscle_closed, c_kind.size()])


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
		if depth_of(s) < depth:
			continue
		var a := c_a[s]
		var b := c_b[s]
		if _distance_to_line((uv_of(a) + uv_of(b)) * 0.5, points) < radius:
			gap = maxf(gap, _open_gap(s))
	return gap


## Height (site-local y) of the skin at uv as it's deformed now, NAN over an opening or where no skin lies.
## Tools and hands touch this, not the body's rest shape, so a lifted or pressed fold is where it's drawn.
func skin_height(uv: Vector2) -> float:
	_sort_bins()
	var p := Vector2((uv.x - 0.5) * size.x, (uv.y - 0.5) * size.y)
	var cell := _bin_of(p)
	if cell.x < 0 or cell.x >= BINS or cell.y < 0 or cell.y >= BINS:
		return NAN
	for t in _bins[cell.y * BINS + cell.x]:
		var a := pos[_bin_triangles[t]]
		var b := pos[_bin_triangles[t + 1]]
		var c := pos[_bin_triangles[t + 2]]
		var weights := _barycentric(p, Vector2(a.x, a.z), Vector2(b.x, b.z), Vector2(c.x, c.z))
		if weights.x >= 0.0 and weights.y >= 0.0 and weights.z >= 0.0:
			return a.y * weights.x + b.y * weights.y + c.y * weights.z
	return NAN


## Current stretch of the quad edge between two particles, relative to its rest length.
func stretch(a: int, b: int) -> float:
	return pos[a].distance_to(pos[b]) / maxf(rest[a].distance_to(rest[b]), 0.0001)


## Triangle indices of the grid, minus triangles spanning a gap cut at least `depth` deep and pulled open,
## so a layer mesh built from them shows a hole there, and minus triangles off the body.
func triangles(depth: int) -> PackedInt32Array:
	var open := PackedByteArray()
	open.resize(c_a.size())
	for s in _severed:
		if depth_of(s) >= depth and _open_gap(s) > 0.0:
			open[s] = 1
	var out := PackedInt32Array()
	for j in RES:
		for i in RES:
			var a := index(i, j)
			var c := a + RES + 1
			if open[_right[a]] + open[_diag[a]] + open[_down[a]] + off[a] + off[a + 1] + off[c] == 0:
				out.append(a)
				out.append(a + 1)
				out.append(c)
			if open[_down[a + 1]] + open[_right[c]] + open[_diag[a]] + off[a + 1] + off[c + 1] + off[c] == 0:
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
	var springs := c_a.size()
	for iteration in ITERATIONS:
		for key: int in _pins:
			var pin: Array = _pins[key]
			pos[pin[0]] = pin[1]
			var pull: Vector3 = pin[1] - anchor_target[pin[0]]
			# The patch is dragged along by translating it, which only looks right for a modest pull. A flap swung
			# far back is left to the springs, or the translated patch would fight the way it turns.
			var drag := GRIP_DRAG * clampf(2.0 - pull.length() / DRAG_REACH, 0.0, 1.0)
			if drag <= 0.0:
				continue
			var patch := _patch(pin[0])
			var around: PackedInt32Array = patch[0]
			var weights: PackedFloat32Array = patch[1]
			for n in around.size():
				var j := around[n]
				pos[j] += (anchor_target[j] + pull * weights[n] - pos[j]) * drag * weights[n] * _free[j]
		# Sweeping the springs forward then backward keeps corrections from always flowing one way across the grid.
		for n in springs:
			var s := n if iteration % 2 == 0 else springs - 1 - n
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
		for pass_index in STITCH_PASSES:
			for s in _stitches:
				if c_active[s] == 1:
					_solve(s)
		for k in pos.size():
			# Skin pulled far from its spot has come loose from what's under it, so a flap can be folded back.
			var back := anchor_target[k] - pos[k]
			pos[k] += back * anchor[k] * _free[k] * clampf(1.0 - back.length() / ANCHOR_REACH, 0.0, 1.0)
	for k in pos.size():
		moved = maxf(moved, pos[k].distance_squared_to(prev[k]))
	if tearing:
		_snap_overstretched()
	_still_steps = _still_steps + 1 if moved < SLEEP_EPSILON * SLEEP_EPSILON else 0


## Moves the ends of spring s toward its rest length (the loop in _substep() does the same inline, for speed).
func _solve(s: int) -> void:
	var a := c_a[s]
	var b := c_b[s]
	var wa := _free[a]
	var wb := _free[b]
	var d := pos[b] - pos[a]
	var length := d.length()
	if wa + wb == 0.0 or length < 0.00001:
		return
	var correction := d * ((length - c_rest[s]) / (length * (wa + wb)))
	pos[a] += correction * wa
	pos[b] -= correction * wb


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
			snapped.append([uv_of(a), uv_of(b), c_kind[s], s])
			topology_version += 1


func _spring(a: int, b: int, kind: Kind = Kind.TISSUE, tension: float = TENSION, strength: float = TISSUE_BREAK) -> int:
	c_a.append(a)
	c_b.append(b)
	c_rest.append(rest[a].distance_to(rest[b]) * tension)
	c_active.append(1)
	c_kind.append(kind)
	c_break.append(strength)
	c_depth.append(Depth.NONE)
	c_muscle_closed.append(0)
	if kind == Kind.STITCH:
		_edge_to_stitch[_edge_key(a, b)] = c_a.size() - 1
		_stitches.append(c_a.size() - 1)
	return c_a.size() - 1


## The skin a grip on particle k drags along: particles within GRIP_PATCH reached without crossing a cut,
## weighted falling off in a straight line to 0 at the edge of the patch.
func _patch(k: int) -> Array:
	if _patch_version != topology_version:
		_patches.clear()
		_patch_version = topology_version
	if _patches.has(k):
		return _patches[k]
	if _springs_of.is_empty():
		_springs_of.resize(rest.size())
		for s in c_a.size():
			_springs_of[c_a[s]].append(s)
			_springs_of[c_b[s]].append(s)
	var around := PackedInt32Array()
	var weights := PackedFloat32Array()
	var seen := {k: true}
	var queue: Array[int] = [k]
	while not queue.is_empty():
		var at: int = queue.pop_front()
		for s in _springs_of[at]:
			if c_active[s] == 0 or c_kind[s] != Kind.TISSUE:
				continue
			var other := c_b[s] if c_a[s] == at else c_a[s]
			var dist := uv_of(other).distance_to(uv_of(k))
			if seen.has(other) or dist >= GRIP_PATCH:
				continue
			seen[other] = true
			queue.append(other)
			around.append(other)
			weights.append(1.0 - dist / GRIP_PATCH)
	_patches[k] = [around, weights]
	return _patches[k]


func _sever(s: int, depth: int) -> void:
	if c_active[s] == 1:
		c_active[s] = 0
		_severed.append(s)
	c_depth[s] = maxi(c_depth[s], depth)
	if depth == Depth.MUSCLE:
		c_muscle_closed[s] = 0


## Particles on either side of open muscle are pulled back from the cut, sideways along the skin.
func _update_retraction() -> void:
	var pull := PackedVector3Array()
	pull.resize(rest.size())
	for s in _severed:
		if depth_of(s) != Depth.MUSCLE:
			continue
		var across := (rest[c_a[s]] - rest[c_b[s]]) * Vector3(1, 0, 1)
		pull[c_a[s]] += across
		pull[c_b[s]] -= across
	for k in rest.size():
		var back := pull[k].normalized() * MUSCLE_RETRACT if pull[k].length_squared() > 0.0 else Vector3.ZERO
		anchor_target[k] = rest[k] + back
		if back != Vector3.ZERO:
			anchor[k] = maxf(anchor[k], MUSCLE_PULL)
		elif anchor[k] == MUSCLE_PULL:
			anchor[k] = LOOSE_ANCHOR


## How far severed spring s is pulled apart past its rest length (meters). Pulled together by a stitch it counts as
## closed only once the edges nearly meet (within half of OPEN_GAP); a loose stitch leaves the rest of the gap open.
func _open_gap(s: int) -> float:
	var a := c_a[s]
	var b := c_b[s]
	var gap := pos[a].distance_to(pos[b]) - rest[a].distance_to(rest[b])
	return gap if gap > OPEN_GAP * 0.5 else 0.0


## Re-sorts the skin triangles into bins after the skin moved or was cut. Only runs when queried.
func _sort_bins() -> void:
	if _bins_for == [topology_version, steps_done]:
		return
	_bins_for = [topology_version, steps_done]
	_bin_triangles = triangles(Depth.SKIN)
	_bins.resize(BINS * BINS)
	_bins.fill(PackedInt32Array())
	for t in range(0, _bin_triangles.size(), 3):
		var low := Vector2(INF, INF)
		var high := Vector2(-INF, -INF)
		for n in 3:
			var at := pos[_bin_triangles[t + n]]
			low = low.min(Vector2(at.x, at.z))
			high = high.max(Vector2(at.x, at.z))
		var from := _bin_of(low).clamp(Vector2i.ZERO, Vector2i(BINS - 1, BINS - 1))
		var to := _bin_of(high).clamp(Vector2i.ZERO, Vector2i(BINS - 1, BINS - 1))
		for y in range(from.y, to.y + 1):
			for x in range(from.x, to.x + 1):
				_bins[y * BINS + x].append(t)


## Bin of a site-local point (x, z). Out of range off the binned area.
func _bin_of(p: Vector2) -> Vector2i:
	var extent := size * (1.0 + BIN_MARGIN * 2.0)
	var cell := (p + extent * 0.5) / extent * BINS
	return Vector2i(floori(cell.x), floori(cell.y))


static func _barycentric(p: Vector2, a: Vector2, b: Vector2, c: Vector2) -> Vector3:
	var area := (b - a).cross(c - a)
	if absf(area) < 1e-12:
		return Vector3(-1, -1, -1)
	var wb := (p - a).cross(c - a) / area
	var wc := (b - a).cross(p - a) / area
	return Vector3(1.0 - wb - wc, wb, wc)


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
