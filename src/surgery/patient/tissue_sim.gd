class_name TissueSim
extends RefCounted
## Position-based soft tissue for the surgical site skin.
## A grid of particles joined by springs, all in site-local space. Cells are about CELL meters square on every site.
##
## - Springs are a little shorter than the grid spacing, so the skin is under tension.
##   Cut the springs along an incision and the edges pull apart on their own.
## - Every particle is weakly anchored to where it sits on the body (fascia).
##   Near an incision the anchor is loosened, like undermined skin, and the cut edges are drawn back a little, more
##   the deeper the cut: skin gapes, fat bulges apart, cut muscle retracts.
## - Each severed spring remembers where along it the blade crossed (c_cross), so the layers are drawn split exactly
##   along the blade's path, not along the grid (PatientBody._rebuild_layers()).
## - Tools pin particles to their tip: forceps and retractors stretch the skin. A grip drags a patch of skin
##   around it along with falloff (a pinched fold, not a single point), so a pull spreads out and the skin stretches
##   over a wide area instead of tearing right beside the tip.
## - Springs stretched past their limit snap (host only). The host turns that into a tear.
## - Sutures add new springs across a cut. Their rest length is the stitch tension. A cut only counts as closed
##   where its edges have actually come together: a loose stitch leaves a gap that stays open (and bleeds).
## - A cut through the muscle retracts: the muscle pulls the edges further apart until it's stitched itself
##   (muscle_stitch()). Skin can't be closed over open muscle, see Patient.close_at().
## - Only an active window is simulated: the cells around cuts, grips and skin that moved, plus a margin. The rest
##   of the grid holds still where it settled. The sim sleeps when nothing moves, so an untouched patient costs nothing.

## Grid spacing (meters), or wider on a big site so it has no more than MAX_CELLS cells: a whole belly folded open
## moves every particle at once, and that has to fit a frame. The site's size sets how many cells it has along each
## side, at least MIN_RES.
const CELL := 0.006
const MAX_CELLS := 1000
const MIN_RES := 6
const TENSION := 0.93
const ANCHOR := 0.02
## Skin pulled this far (meters) from its spot isn't held there any more, see _substep().
const ANCHOR_REACH := 0.05
## A cut's edge lifted this far (meters) off its spot isn't drawn back from the cut any more.
const LIFTED_OFF := 0.005
## How far each edge of a cut is drawn back from it (meters) by cut depth (none, skin, fat, muscle), and how firmly.
## Skin gapes a little under its own tension, cut fat bulges apart, cut muscle retracts hard.
## The pull fades out over RETRACT_SPREAD (meters) from the cut, so the stretch is shared by the skin around it,
## and over RETRACT_TAPER (meters) toward the cut's ends, where it stays closed: the cut opens like a lens.
const RETRACT: Array[float] = [0.0, 0.0022, 0.0035, 0.015]
const RETRACT_SPREAD: Array[float] = [0.0, 0.03, 0.045, 0.04]
const RETRACT_TAPER: Array[float] = [0.0, 0.008, 0.012, 0.03]
const GAPE_PULL := 0.03
const MUSCLE_PULL := 0.05
const LOOSE_ANCHOR := 0.006
## Skin this close to a cut (meters) is loosened from what's under it.
const LOOSE_RADIUS := 0.012
## How far (uv) a grip drags the skin around it along, and how firmly per solver iteration at its center.
const GRIP_PATCH := 0.15
const GRIP_DRAG := 1.0
## Skin this close to a grip (meters, at rest) is held by it (see _hold()): forceps hold a few millimeters of skin,
## not a point, so a fine grid doesn't tear right beside the jaws.
const GRIP_HOLD := 0.01
## Skin under the drape stays at least this far (meters) below the height folded-out skin lies at on top of it.
const UNDER_DRAPE := 0.006
## How far (meters) from its rest point skin still rests on the body (see _stay_on_body()).
const BODY_REACH := 0.01
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
## One stitch pulls together every cut spring this close to it (meters, at rest): a stitch closes a few millimeters
## of the cut, however fine the grid.
const STITCH_REACH := 0.005
## Closest to either end of a spring (share of its length) the lip of a cut through it is drawn.
const CROSS_MARGIN := 0.2
const STEP := 1.0 / 30.0
## Most solver steps one frame may run to catch up, so a long frame doesn't turn into a longer one.
const MAX_CATCH_UP := 2
const SLEEP_EPSILON := 0.00002
const SLEEP_STEPS := 20
## Most solver steps the skin gets to settle at build time (see _settle()).
const SETTLE_STEPS := 300
## Cells of still skin simulated around the active area, and how often (solver steps) the window is redrawn.
## Skin that moved further than WINDOW_MOVE (meters) widens it; less than that, it's drawn by the body model anyway.
const WINDOW_MARGIN := 4
const WINDOW_MOVE := 0.002
const WINDOW_REFRESH := 6

enum Kind { TISSUE, STITCH }
## How deep a severed spring was cut: through the skin, into the fat, or through the muscle into the cavity.
enum Depth { NONE, SKIN, FAT, MUSCLE }
## A gap counts as open once it's pulled this far apart (meters).
const OPEN_GAP := 0.004
## Most of the grid a piece of skin cut out all round may hold: past that the cut just runs round most of the site.
const PIECE_MAX := 0.25
## Cells per side of the grid that sorts skin triangles by where they lie now, for skin_height() once the skin
## moved far (a flap folded back). It covers the site and a margin around it.
const BINS := 16
const BIN_MARGIN := 0.1

var size := Vector2.ONE
## Cells along u and v; particles per row is res_x + 1.
var res_x := 24
var res_y := 24
var rest := PackedVector3Array()
var pos := PackedVector3Array()
var prev := PackedVector3Array()
var anchor := PackedFloat32Array()
## Outward lift at stitched cut edges. Compression stays in the surface plane;
## this separate offset forms a lip without letting the two sides pass through.
var suture_lip := PackedFloat32Array()
var fixed := PackedByteArray()
## 1 for particles where the site hangs off the body (past a limb's or the flank's edge): not drawn and not part of
## the region while they hang there, so the site can't stick out of the body (see hanging_off()). They're still
## simulated, so skin next to them moves as it always did.
var off := PackedByteArray()
## Constraint arrays, one entry per spring.
var c_a := PackedInt32Array()
var c_b := PackedInt32Array()
var c_rest := PackedFloat32Array()
var c_active := PackedByteArray()
var c_kind := PackedByteArray()
var c_break := PackedFloat32Array()
var c_depth := PackedByteArray()
## Where along a severed spring (0 at c_a, 1 at c_b) the blade crossed it. A tear splits it in the middle.
var c_cross := PackedFloat32Array()
## Which way the cut ran where it crossed a severed spring (site x, z): its edges are drawn back square to it.
var c_cut_dir := PackedVector2Array()
## 1 for springs cut through the muscle whose muscle has been stitched: they count as cut only into the fat.
var c_muscle_closed := PackedByteArray()
## 1 once the subcutaneous layer has been closed: the same cut then counts as skin-only.
var c_fat_closed := PackedByteArray()
## Grid springs leaving each particle (-1 past the grid's edge): to the right, down, the (i+1, j)-(i, j+1) diagonal
## and the other diagonal. The drawn triangles of cell (i, j) use the first three of particle (i, j) and its
## neighbours, see triangles().
var spring_right := PackedInt32Array()
var spring_down := PackedInt32Array()
var spring_diag := PackedInt32Array()
var spring_anti := PackedInt32Array()
## Where the anchor pulls each particle: its rest position, moved back from a cut.
var anchor_target := PackedVector3Array()
## Which way is out of the body at each particle's rest point (unit), see _stay_on_body().
var rest_normal := PackedVector3Array()
## What exposed skin folded out of the site can't go below (the drape): site-local (x, z) -> height, NAN where
## there's nothing. Unset: nothing to lie on. Only particles marked in `exposed` are held up by it.
var floor_at: Callable
var exposed := PackedByteArray()
## The drape's opening (site x, z): no drape anywhere in it, so skin there doesn't need floor_at. Empty: unknown.
var floor_open := Rect2()
## Where each particle rests under the skin's own tension before anything touches it (see _settle()).
var settled := PackedVector3Array()
## Only the host decides when springs snap, so tears happen once for everyone.
var tearing := false
## Springs that snapped since the host last read them: [uv a, uv b, Kind, spring index].
## Springs are only ever appended, so the index names the same spring on every peer (see snap_spring()).
var snapped: Array[Array] = []
## Scales how far past its rest length a spring stretches before it snaps (thin skin tears sooner).
var break_mult := 1.0
## 1 for skin taken off (cut out all round and lifted away, a graft), one byte per particle: the skin layer has a
## hole there and what lies under it shows.
var excised := PackedByteArray()
## Changes whenever springs are cut, stitched or snap (meshes rebuild their triangles).
var topology_version := 0
## Counts simulation steps, so meshes only rebuild when something moved.
var steps_done := 0

## key -> [particle, target]
var _pins: Dictionary = {}
## Particle -> [particles, weights, held particles] it drags along when gripped, for the topology it was worked out for.
var _patches: Dictionary = {}
var _patch_version := -1
## Spring indices touching each particle.
var _springs_of: Array[PackedInt32Array] = []
var _still_steps := 0
var _accumulator := 0.0
var _edge_to_stitch: Dictionary = {}
## Tissue springs that are cut or snapped. Gap and mesh queries only look at these.
var _severed := PackedInt32Array()
## Every stitch spring, active or not.
var _stitches := PackedInt32Array()
## Running sutures, id -> {anchors (particle ids), springs, slack, layer, tension, final}.
## They preserve the route of the thread, so pulling its free end changes every span.
var _threads: Dictionary = {}
## 1 for particles the solver may move this step, 0 for the fixed border, pinned particles and still skin outside the
## active window.
var _free := PackedFloat32Array()
## The active window: particle columns and rows [x0, x1] x [y0, y1], empty when nothing needs simulating.
var _win := Rect2i()
## Movable particles inside the window, and every grid spring touching one.
var _win_particles := PackedInt32Array()
var _win_springs := PackedInt32Array()
var _win_steps := 0
var _win_dirty := true
## While the skin settles at build time the whole grid is simulated.
var _win_locked := false
## Every cut as [a, b] uv pairs, in the order they were made.
var _cut_segments := PackedVector2Array()
## Strokes: runs of segments each starting where the last one ended (a blade moved without lifting it), as
## [where it started, the point of it farthest from there]. Those two are its ends.
var _strokes: Array[PackedVector2Array] = []
## Which stroke each segment belongs to.
var _segment_stroke := PackedInt32Array()
## The ends of the cuts (uv), worked out again whenever a cut is added.
var _cut_ends := PackedVector2Array()
var _cut_ends_for := -1
## Set when the sim falls asleep: the next window is picked afresh, otherwise it only grows while the skin moves
## (shrinking a window around moving skin would hold skin at its edge where it doesn't rest, and it moves again).
var _win_fresh := true
## Particles drawn back from a cut (see _update_retraction()), so the next update can let go of them.
var _retracted := PackedInt32Array()
## Per particle, while _update_retraction() works: the summed pull away from cuts and how far it reaches.
var _pull := PackedVector3Array()
var _pull_amount := PackedFloat32Array()
var _retract_dirty := false
## Farthest any particle has moved from its rest position (meters, squared), as of the last step.
var _farthest := 0.0
## Skin triangles (particle indices) and, per bin, the first index in them of each triangle overlapping it.
var _bin_triangles := PackedInt32Array()
var _bins: Array[PackedInt32Array] = []
## [topology_version, steps_done] the bins were sorted for.
var _bins_for := [-1, -1]
## 1 for skin cut free from what's around it (see _stay_above_floor()), for the topology it was worked out for.
var _cut_free := PackedByteArray()
var _cut_free_for := -1
## Particles off the body (see off).
var _off_list := PackedInt32Array()
var _excised_list := PackedInt32Array()
var _hanging := PackedByteArray()
var _hanging_for := []
var _lipped := PackedInt32Array()


## height_at(uv) -> skin height above the site plane. on_body(uv) -> false where the site is off the body.
func build(site_size: Vector2, height_at: Callable, on_body: Callable = Callable()) -> void:
	size = site_size
	var cell := maxf(CELL, sqrt(size.x * size.y / MAX_CELLS))
	res_x = maxi(ceili(size.x / cell), MIN_RES)
	res_y = maxi(ceili(size.y / cell), MIN_RES)
	var count := (res_x + 1) * (res_y + 1)
	rest.resize(count)
	anchor.resize(count)
	suture_lip.resize(count)
	suture_lip.fill(0.0)
	fixed.resize(count)
	off.resize(count)
	excised.resize(count)
	excised.fill(0)
	_excised_list = PackedInt32Array()
	for j in res_y + 1:
		for i in res_x + 1:
			var uv := Vector2(float(i) / res_x, float(j) / res_y)
			var k := index(i, j)
			rest[k] = Vector3((uv.x - 0.5) * size.x, height_at.call(uv), (uv.y - 0.5) * size.y)
			anchor[k] = ANCHOR
			off[k] = 0 if on_body.is_null() or on_body.call(uv) else 1
			fixed[k] = 1 if i == 0 or j == 0 or i == res_x or j == res_y else 0
	pos = rest.duplicate()
	prev = rest.duplicate()
	anchor_target = rest.duplicate()
	rest_normal.resize(count)
	for j in res_y + 1:
		for i in res_x + 1:
			var dx := rest[index(mini(i + 1, res_x), j)] - rest[index(maxi(i - 1, 0), j)]
			var dz := rest[index(i, mini(j + 1, res_y))] - rest[index(i, maxi(j - 1, 0))]
			var normal := dz.cross(dx).normalized()
			rest_normal[index(i, j)] = normal if normal.y >= 0.0 else -normal
	_off_list = PackedInt32Array()
	for k in count:
		if off[k] == 1:
			_off_list.append(k)
	_free.resize(count)
	for list: PackedInt32Array in [spring_right, spring_down, spring_diag, spring_anti]:
		list.resize(count)
		list.fill(-1)
	for j in res_y + 1:
		for i in res_x + 1:
			var k := index(i, j)
			if i < res_x:
				spring_right[k] = _spring(k, index(i + 1, j))
			if j < res_y:
				spring_down[k] = _spring(k, index(i, j + 1))
			if i < res_x and j < res_y:
				spring_diag[k] = _spring(index(i + 1, j), index(i, j + 1))
				spring_anti[k] = _spring(k, index(i + 1, j + 1))
	_settle()


## Lets the skin settle under its own tension before anything touches it. Over a curved body tension pulls the sheet a
## few millimeters off the body's shape at the site's edges; that's where it rests, not a movement (see region()).
## A flat site is settled at once and falls asleep within SLEEP_STEPS.
## Springs left stretched far at the edge of a round limb (where the site hangs off it) would snap the moment a cut
## wakes the skin, so their breaking point is moved past how far they're stretched at rest. Springs in the site's
## outermost strip (its fixed border and the row of skin along it, under the drape's frame) or to skin off the body
## never snap: they're never drawn, a tear there would only be a bleed nobody can see or reach.
func _settle() -> void:
	_set_window(Rect2i(0, 0, res_x, res_y))
	_win_locked = true
	for step_index in SETTLE_STEPS:
		if is_sleeping():
			break
		_substep()
	_win_locked = false
	settled = pos.duplicate()
	prev = pos.duplicate()
	steps_done = 0
	for s in c_a.size():
		var stretched := pos[c_a[s]].distance_to(pos[c_b[s]]) / c_rest[s]
		c_break[s] = maxf(c_break[s], 1.0 + 2.0 * (stretched * 1.4 - 1.0))
		if _at_border(c_a[s]) or _at_border(c_b[s]) or off[c_a[s]] + off[c_b[s]] > 0:
			c_break[s] = INF
	_win_dirty = true


## Particle k is on the site's fixed border or right next to it.
func _at_border(k: int) -> bool:
	var at := cell_of(k)
	return at.x <= 1 or at.y <= 1 or at.x >= res_x - 1 or at.y >= res_y - 1


func index(i: int, j: int) -> int:
	return j * (res_x + 1) + i


func uv_of(k: int) -> Vector2:
	return Vector2(float(k % (res_x + 1)) / res_x, float(k / (res_x + 1)) / res_y)


## Column and row of particle k.
func cell_of(k: int) -> Vector2i:
	return Vector2i(k % (res_x + 1), k / (res_x + 1))


func nearest(uv: Vector2) -> int:
	var i := clampi(roundi(uv.x * res_x), 1, res_x - 1)
	var j := clampi(roundi(uv.y * res_y), 1, res_y - 1)
	return index(i, j)


func any_severed() -> bool:
	return not _severed.is_empty()


## Tissue springs that are cut or snapped.
func severed() -> PackedInt32Array:
	return _severed


## How deep spring s is cut (Depth.NONE while it's whole). Stitched muscle counts as cut only into the fat.
func cut_depth(s: int) -> int:
	if s < 0 or c_active[s] == 1 or c_kind[s] != Kind.TISSUE:
		return Depth.NONE
	var stitch: int = _edge_to_stitch.get(_edge_key(c_a[s], c_b[s]), -1)
	# A contact-length seam is drawn as one pressed surface. Loose stitches keep
	# the split lips and walls until their target length is short enough to meet.
	if stitch >= 0 and c_active[stitch] == 1 and c_rest[stitch] <= rest[c_a[s]].distance_to(rest[c_b[s]]) * 1.05:
		return Depth.NONE
	return depth_of(s)


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
	for thread: Dictionary in _threads.values():
		seeds.append_array(thread.anchors)
	seeds.append_array(_excised_list)
	# Only skin in the window moves: everything outside it holds still where it settled.
	for k in _win_particles:
		if pos[k].distance_squared_to(settled[k]) > REGION_MOVE * REGION_MOVE:
			seeds.append(k)
	for k in seeds:
		var at := cell_of(k)
		for y in range(maxi(at.y - reach, 0), mini(at.y + reach, res_y) + 1):
			for x in range(maxi(at.x - reach, 0), mini(at.x + reach, res_x) + 1):
				out[index(x, y)] = 1
	# Off the body, and right next to it: triangles touching skin off the body aren't drawn, so the body model has to
	# cover up to there, or its cut-away edge (halfway between region points) would leave a gap.
	var hanging := hanging_off()
	for k in _off_list:
		if hanging[k] == 1:
			var at := cell_of(k)
			for y in range(maxi(at.y - 1, 0), mini(at.y + 1, res_y) + 1):
				for x in range(maxi(at.x - 1, 0), mini(at.x + 1, res_x) + 1):
					out[index(x, y)] = 0
	return out


## Asleep once nothing has moved for SLEEP_STEPS steps, grips held still included: a moved grip wakes it.
func is_sleeping() -> bool:
	return _still_steps >= SLEEP_STEPS


func wake() -> void:
	_still_steps = 0


## Severs every tissue spring the segment crosses down to the given depth and loosens the skin around it.
## A point exactly on the cut's line counts as on its left, so a cut through a grid point severs each spring once.
func cut(a: Vector2, b: Vector2, depth: int) -> void:
	if a.is_equal_approx(b):
		return
	var count := _cut_segments.size()
	var starts := _strokes.is_empty() or count == 0 or not _cut_segments[count - 1].is_equal_approx(a)
	if starts:
		_strokes.append(PackedVector2Array([a, a]))
	var stroke := _strokes[-1]
	if ((b - stroke[0]) * size).length() > ((stroke[1] - stroke[0]) * size).length():
		stroke[1] = b
		_strokes[-1] = stroke
	_cut_segments.append_array([a, b])
	_segment_stroke.append(_strokes.size() - 1)
	# A short cut on its own (a blade pressed in) might fall between springs: it severs the ones a cell's length of its
	# line crosses, so the sim has it at all. A stroke going on is cut where the blade really went.
	var cell := maxf(size.x / res_x, size.y / res_y)
	var meters := ((b - a) * size).length()
	if starts and meters < cell:
		var middle := (a + b) * 0.5
		var half := (b - a) * (cell / meters) * 0.5
		a = middle - half
		b = middle + half
	var low := Vector2i(floori(minf(a.x, b.x) * res_x) - 1, floori(minf(a.y, b.y) * res_y) - 1).clamp(Vector2i.ZERO, Vector2i(res_x, res_y))
	var high := Vector2i(ceili(maxf(a.x, b.x) * res_x) + 1, ceili(maxf(a.y, b.y) * res_y) + 1).clamp(Vector2i.ZERO, Vector2i(res_x, res_y))
	for j in range(low.y, high.y + 1):
		for i in range(low.x, high.x + 1):
			var k := index(i, j)
			for s: int in [spring_right[k], spring_down[k], spring_diag[k], spring_anti[k]]:
				if s < 0 or c_kind[s] != Kind.TISSUE:
					continue
				var t := _crossing(uv_of(c_a[s]), uv_of(c_b[s]), a, b)
				if t >= 0.0:
					# A cut right through a grid point would split the triangles around it into slivers: the lip is
					# drawn a little off it instead (at most a fifth of a cell).
					_sever(s, depth, clampf(t, CROSS_MARGIN, 1.0 - CROSS_MARGIN), (b - a) * size)
	var reach := Vector2i(ceili(LOOSE_RADIUS / size.x * res_x), ceili(LOOSE_RADIUS / size.y * res_y))
	for j in range(maxi(low.y - reach.y, 0), mini(high.y + reach.y, res_y) + 1):
		for i in range(maxi(low.x - reach.x, 0), mini(high.x + reach.x, res_x) + 1):
			var k := index(i, j)
			var uv := uv_of(k)
			if ((uv - Geometry2D.get_closest_point_to_segment(uv, a, b)) * size).length() < LOOSE_RADIUS:
				anchor[k] = LOOSE_ANCHOR
	_retract_dirty = true
	topology_version += 1
	_win_dirty = true
	wake()


## Where segment a-b crosses the line from p to q, as a share of the way from p (0..1), or -1 if it doesn't.
## Half open on both: a point on the cut's line is on its left, the cut's end belongs to the next segment.
static func _crossing(p: Vector2, q: Vector2, a: Vector2, b: Vector2) -> float:
	var ab := b - a
	var sp := ab.cross(p - a)
	var sq := ab.cross(q - a)
	if (sp >= 0.0) == (sq >= 0.0):
		return -1.0
	var t := sp / (sp - sq)
	var along := (p.lerp(q, t) - a).dot(ab) / ab.length_squared()
	if along < 0.0 or along >= 1.0:
		return -1.0
	return t


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
		_win_dirty = true
		wake()
	return closed


## Closes the subcutaneous layer without closing the skin over it. This models
## deep absorbable bites through the connective tissue around fat, rather than
## pretending adipose itself has the holding strength of skin or fascia.
func fat_stitch(uv: Vector2, radius: float) -> int:
	var closed := 0
	for s in _severed:
		var fat_is_exposed := c_depth[s] == Depth.FAT or (c_depth[s] == Depth.MUSCLE and c_muscle_closed[s] == 1)
		if fat_is_exposed and c_fat_closed[s] == 0 and ((uv_of(c_a[s]) + uv_of(c_b[s])) * 0.5).distance_to(uv) < radius:
			c_fat_closed[s] = 1
			closed += 1
	if closed > 0:
		_update_retraction()
		topology_version += 1
		_win_dirty = true
		wake()
	return closed


## True while muscle cut near uv hasn't been stitched: skin closed over it would be under too much tension.
func muscle_open_near(uv: Vector2, radius: float) -> bool:
	for s in _severed:
		if depth_of(s) == Depth.MUSCLE and ((uv_of(c_a[s]) + uv_of(c_b[s])) * 0.5).distance_to(uv) < radius:
			return true
	return false


func fat_open_near(uv: Vector2, radius: float) -> bool:
	for s in _severed:
		if depth_of(s) >= Depth.FAT and ((uv_of(c_a[s]) + uv_of(c_b[s])) * 0.5).distance_to(uv) < radius:
			return true
	return false


## Raises both sides of a sewn edge along the surface normal. `amount` is set,
## not accumulated, so loosening the live thread reduces the lip again.
func suture_pucker(uv: Vector2, radius: float, amount: float) -> void:
	for s in _severed:
		var crossing := uv_of(c_a[s]).lerp(uv_of(c_b[s]), c_cross[s])
		if crossing.distance_to(uv) >= radius:
			continue
		for k: int in [c_a[s], c_b[s]]:
			if suture_lip[k] == 0.0 and amount > 0.0 and not _lipped.has(k):
				_lipped.append(k)
			suture_lip[k] = amount
	_retract_dirty = true
	wake()


## How deep spring s counts as cut: stitched muscle leaves only skin and fat open.
func depth_of(s: int) -> int:
	if c_fat_closed[s] == 1 and (c_depth[s] == Depth.FAT or c_muscle_closed[s] == 1):
		return Depth.SKIN
	return Depth.FAT if c_muscle_closed[s] == 1 else c_depth[s]


## The particles of the piece of skin around particle k that cuts have set free all round: those reached from k without
## crossing a cut (a stitch across one joins), as long as that never reaches the site's border. Empty while the skin
## there is still joined to the rest, or was already taken off.
func piece_of(k: int) -> PackedInt32Array:
	if excised[k] == 1:
		return PackedInt32Array()
	_index_springs()
	var piece := PackedInt32Array([k])
	var seen := {k: true}
	var n := 0
	while n < piece.size():
		var at := piece[n]
		n += 1
		if _at_border(at) or piece.size() > rest.size() * PIECE_MAX:
			return PackedInt32Array()
		for s in _springs_of[at]:
			if c_active[s] == 0 and not _stitched(c_a[s], c_b[s]):
				continue
			var other := c_b[s] if c_a[s] == at else c_a[s]
			if not seen.has(other):
				seen[other] = true
				piece.append(other)
	return piece


## Takes off the piece of skin around particle k (see piece_of()): it's no longer drawn or touched, and grips on it let
## go. Returns how many grid points it held, 0 when there's no piece there.
func excise(k: int) -> int:
	var piece := piece_of(k)
	if piece.is_empty():
		return 0
	for p in piece:
		excised[p] = 1
	_excised_list.append_array(piece)
	for key: int in _pins.keys():
		if excised[_pins[key][0]] == 1:
			_pins.erase(key)
	topology_version += 1
	_win_dirty = true
	wake()
	return piece.size()


## Pins the particle nearest uv to follow a tool. Returns false if there is no tissue there.
func grip(key: int, uv: Vector2) -> bool:
	_pins[key] = [nearest(uv), pos[nearest(uv)]]
	_win_dirty = true
	wake()
	return true


func move_grip(key: int, target: Vector3) -> void:
	if _pins.has(key):
		var moved := _above_floor(_pins[key][0], target)
		if not moved.is_equal_approx(_pins[key][1]):
			_pins[key][1] = moved
			wake()


func release(key: int) -> void:
	_pins.erase(key)
	_win_dirty = true
	wake()


func grips() -> Array:
	return _pins.keys().map(func(key: int) -> Array: return [key, _pins[key][0], _pins[key][1]])


## Clients mirror the host's grips: [[key, particle, target], ...].
func set_grips(list: Array) -> void:
	var before := _pins.duplicate(true)
	_pins.clear()
	for entry: Array in list:
		_pins[entry[0]] = [entry[1], entry[2]]
	if _pins.keys() != before.keys():
		_win_dirty = true
	if _pins != before:
		wake()


## Closes the severed springs nearest uv with stitches: the nearest one within reach, and every other one within
## STITCH_REACH of it. tension scales their rest length (loose > 1 > tight).
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
	var center := (rest[c_a[best]] + rest[c_b[best]]) * 0.5
	for s in _severed.duplicate():
		if s == best or (not _stitched(c_a[s], c_b[s]) and ((rest[c_a[s]] + rest[c_b[s]]) * 0.5).distance_to(center) < STITCH_REACH):
			_spring(c_a[s], c_b[s], Kind.STITCH, tension, strength)
	_update_retraction()
	topology_version += 1
	_win_dirty = true
	wake()
	return true


## Joins every severed edge along a finalized supported section. Point stitches
## are deliberately local; a running thread needs this continuous contact seam
## so diagonal grid edges cannot leave tiny wall slivers between its bites.
func stitch_path(points: PackedVector2Array, radius: float, tension: float, strength: float) -> int:
	var joined := 0
	for s in _severed:
		var crossing := uv_of(c_a[s]).lerp(uv_of(c_b[s]), c_cross[s])
		if not _stitched(c_a[s], c_b[s]) and _distance_to_line(crossing, points) < radius:
			_spring(c_a[s], c_b[s], Kind.STITCH, tension, strength)
			joined += 1
	if joined > 0:
		_update_retraction()
		topology_version += 1
		_win_dirty = true
		wake()
	return joined


## Adds one puncture to a continuous running suture. The first puncture is only
## an anchor; every later one adds a constraint from the previous hole. `slack`
## remembers how much thread was paid out as each span was placed.
func thread_anchor(id: int, uv: Vector2, layer: int, tension: float, strength: float) -> bool:
	var k := nearest(uv)
	var thread: Dictionary = _threads.get(id, {
		"anchors": PackedInt32Array(), "springs": PackedInt32Array(),
		"slack": PackedFloat32Array(), "layer": layer, "tension": tension, "final": false,
	})
	var anchors: PackedInt32Array = thread.anchors
	if bool(thread.final) or (not anchors.is_empty() and anchors[-1] == k):
		return false
	if not anchors.is_empty():
		var previous := anchors[-1]
		var paid_out := pos[previous].distance_to(pos[k]) / maxf(rest[previous].distance_to(rest[k]), 0.0001)
		var slack: PackedFloat32Array = thread.slack
		# Hand spacing affects the final result, but cannot pay out so much extra
		# that the wheel can never take it back. A careful rhythm stays near 1;
		# erratic placement leaves up to twelve percent more thread to manage.
		slack.append(clampf(paid_out, 0.95, 1.12))
		thread.slack = slack
		var springs: PackedInt32Array = thread.springs
		springs.append(_spring(previous, k, Kind.STITCH, _thread_rest_scale(tension, layer) * slack[-1], strength))
		thread.springs = springs
	anchors.append(k)
	thread.anchors = anchors
	thread.layer = layer
	thread.tension = tension
	_threads[id] = thread
	topology_version += 1
	_win_dirty = true
	wake()
	return true


## Tightens or loosens the whole running thread from its free end.
func thread_tension(id: int, tension: float) -> bool:
	if not _threads.has(id):
		return false
	var thread: Dictionary = _threads[id]
	var springs: PackedInt32Array = thread.springs
	var slack: PackedFloat32Array = thread.slack
	var layer: int = thread.layer
	for i in springs.size():
		var s := springs[i]
		c_rest[s] = rest[c_a[s]].distance_to(rest[c_b[s]]) * _thread_rest_scale(tension, layer) * slack[i]
	thread.tension = tension
	_threads[id] = thread
	wake()
	return true


## Player-facing tension is intentionally not a literal spring-length ratio.
## Near the safe closure point the thread restores the tissue's uncut shape;
## further tightening gathers it rapidly, while loose thread still has slack.
func _thread_rest_scale(tension: float, layer: int) -> float:
	var loose: float = [0.0, 1.23, 1.12, 1.02][layer]
	var closed: float = [0.0, 1.08, 0.98, 0.86][layer]
	var tear: float = [0.0, 0.68, 0.60, 0.54][layer]
	if tension >= closed:
		return lerpf(0.90, 1.15, clampf((tension - closed) / maxf(loose - closed, 0.01), 0.0, 1.0))
	return lerpf(0.25, 0.90, clampf((tension - tear) / maxf(closed - tear, 0.01), 0.0, 1.0))


func finish_thread(id: int) -> void:
	if _threads.has(id):
		var thread: Dictionary = _threads[id]
		thread.final = true
		_threads[id] = thread


func thread_ids() -> Array:
	return _threads.keys()


func thread_info(id: int) -> Dictionary:
	return _threads.get(id, {})


func thread_uvs(id: int) -> PackedVector2Array:
	var out := PackedVector2Array()
	if not _threads.has(id):
		return out
	var thread: Dictionary = _threads[id]
	for k: int in thread.anchors:
		out.append(uv_of(k))
	return out


## Removes stitches near uv (a closure bursting open).
func burst(uv: Vector2, radius: float) -> void:
	for s in _stitches:
		if c_active[s] == 1 and ((uv_of(c_a[s]) + uv_of(c_b[s])) * 0.5).distance_to(uv) < radius:
			c_active[s] = 0
	_update_retraction()
	topology_version += 1
	_win_dirty = true
	wake()


## Snaps spring s exactly as the host's sim did (clients mirror host tears). Searching by position instead would be
## ambiguous: both diagonals of a grid cell share a midpoint, and several stitches can sit within any radius.
func snap_spring(s: int) -> void:
	if s < 0 or s >= c_a.size():
		push_error("Tissue out of sync: no spring %d (%d springs)" % [s, c_a.size()])
		return
	if c_kind[s] == Kind.TISSUE:
		_sever(s, Depth.SKIN, 0.5)
	else:
		c_active[s] = 0
	_update_retraction()
	topology_version += 1
	_win_dirty = true
	wake()


## Changes with every cut, stitch, burst or snap, and is the same on peers whose tissue was cut the same way.
func topology_hash() -> int:
	return hash([c_active, c_depth, c_muscle_closed, c_fat_closed, c_kind.size(), excised])


## Seizures, coughs and bumps shake the tissue (visual, every peer). Only skin that's being simulated shakes: the rest
## lies still under the body model anyway.
func shake(amount: float) -> void:
	for k in _win_particles:
		prev[k] = pos[k] - Vector3(randf_range(-1, 1), randf_range(-1, 1), randf_range(-1, 1)) * amount
	wake()


## True when uv lies inside an opening: between the lips of a cut at least `depth` deep, pulled open.
func is_open(uv: Vector2, depth: int = Depth.MUSCLE) -> bool:
	if depth <= Depth.SKIN and excised[nearest(uv)] == 1:
		return true
	var cell := maxf(size.x / res_x, size.y / res_y)
	for s in _severed:
		if depth_of(s) < depth:
			continue
		var gap := _open_gap(s)
		if gap <= OPEN_GAP:
			continue
		# Within half the gap of where the blade crossed, across the cut, and no further along it than the springs lie.
		var offset := (uv - uv_of(c_a[s]).lerp(uv_of(c_b[s]), c_cross[s])) * size
		var across := ((uv_of(c_b[s]) - uv_of(c_a[s])) * size).normalized()
		var side := offset.dot(across)
		if absf(side) < gap * 0.5 and (offset - across * side).length() < cell * 0.6:
			return true
	return false


## Meters the tissue has pulled apart across springs severed at least `depth` deep near uv.
func gap_at(uv: Vector2, radius: float = 0.04, depth: int = Depth.SKIN) -> float:
	return gap_along(PackedVector2Array([uv]), radius, depth)


## Widest gap near a polyline (a wound), same rules as gap_at(). Measured where the blade crossed each spring.
func gap_along(points: PackedVector2Array, radius: float, depth: int) -> float:
	var gap := 0.0
	for s in _severed:
		if depth_of(s) < depth:
			continue
		if _distance_to_line(uv_of(c_a[s]).lerp(uv_of(c_b[s]), c_cross[s]), points) < radius:
			gap = maxf(gap, _open_gap(s))
	return gap


## Height (site-local y) of the skin at uv as it's deformed now, NAN over an opening or where no skin lies.
## Tools and hands touch this, not the body's rest shape, so a lifted or pressed fold is where it's drawn.
## Skin that only moved a little is searched for around where it rests; a flap moved far is found through bins.
func skin_height(uv: Vector2) -> float:
	var p := Vector2((uv.x - 0.5) * size.x, (uv.y - 0.5) * size.y)
	var reach := 2
	if _farthest > pow(reach * minf(size.x / res_x, size.y / res_y) * 0.8, 2.0):
		return _binned_height(p)
	var hanging := hanging_off()
	var ci := clampi(floori(uv.x * res_x), 0, res_x - 1)
	var cj := clampi(floori(uv.y * res_y), 0, res_y - 1)
	for j in range(maxi(cj - reach, 0), mini(cj + reach, res_y - 1) + 1):
		for i in range(maxi(ci - reach, 0), mini(ci + reach, res_x - 1) + 1):
			var a := index(i, j)
			var c := a + res_x + 1
			for tri: PackedInt32Array in [PackedInt32Array([a, a + 1, c, spring_right[a], spring_diag[a], spring_down[a]]), PackedInt32Array([a + 1, c + 1, c, spring_down[a + 1], spring_right[c], spring_diag[a]])]:
				if hanging[tri[0]] + hanging[tri[1]] + hanging[tri[2]] + excised[tri[0]] + excised[tri[1]] + excised[tri[2]] > 0 or _gaping(tri[3]) or _gaping(tri[4]) or _gaping(tri[5]):
					continue
				var pa := pos[tri[0]]
				var pb := pos[tri[1]]
				var pc := pos[tri[2]]
				var weights := _barycentric(p, Vector2(pa.x, pa.z), Vector2(pb.x, pb.z), Vector2(pc.x, pc.z))
				if weights.x >= 0.0 and weights.y >= 0.0 and weights.z >= 0.0:
					return pa.y * weights.x + pb.y * weights.y + pc.y * weights.z
	return NAN


## Spring s is cut and pulled open, so no skin lies across it.
func _gaping(s: int) -> bool:
	return c_active[s] == 0 and c_kind[s] == Kind.TISSUE and _open_gap(s) > 0.0


func _binned_height(p: Vector2) -> float:
	_sort_bins()
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


## 1 for particles off the body, 0 elsewhere, except for skin folded out on top of the drape (see floor_at): that
## can't stick out of the body, it lies on the sheet, and is shown like any other. Skin still where it settled hasn't
## been folded anywhere, even where the drape's coarse grid reads lower than it (a steep flank).
## Worked out once per step.
func hanging_off() -> PackedByteArray:
	if _hanging_for == [steps_done, floor_at.is_valid()] and _hanging.size() == off.size():
		return _hanging
	_hanging_for = [steps_done, floor_at.is_valid()]
	_hanging = off.duplicate()
	if floor_at.is_valid() and settled.size() == pos.size():
		for k in _off_list:
			if _hanging[k] == 1 and pos[k].distance_squared_to(settled[k]) > LIFTED_OFF * LIFTED_OFF:
				var floor_y: float = floor_at.call(pos[k].x, pos[k].z)
				if not is_nan(floor_y) and pos[k].y >= floor_y - 0.002:
					_hanging[k] = 0
	return _hanging


## Triangle indices of the whole grid, minus triangles spanning a gap cut at least `depth` deep and pulled open,
## and minus triangles off the body. The meshes split triangles along the cut instead (PatientBody), this is what
## tests and contact count as open.
func triangles(depth: int) -> PackedInt32Array:
	var open := PackedByteArray()
	open.resize(c_a.size())
	for s in _severed:
		if depth_of(s) >= depth and _open_gap(s) > 0.0:
			open[s] = 1
	var hanging := hanging_off()
	# Where skin was taken off, the skin layer has a hole; the layers under it are whole.
	var gone := excised if depth <= Depth.SKIN else PackedByteArray()
	gone.resize(rest.size())
	var out := PackedInt32Array()
	for j in res_y:
		for i in res_x:
			var a := index(i, j)
			var c := a + res_x + 1
			if gone[a] + gone[a + 1] + gone[c + 1] + gone[c] > 0:
				continue
			if open[spring_right[a]] + open[spring_diag[a]] + open[spring_down[a]] + hanging[a] + hanging[a + 1] + hanging[c] == 0:
				out.append(a)
				out.append(a + 1)
				out.append(c)
			if open[spring_down[a + 1]] + open[spring_right[c]] + open[spring_diag[a]] + hanging[a + 1] + hanging[c + 1] + hanging[c] == 0:
				out.append(a + 1)
				out.append(c + 1)
				out.append(c)
	return out


## Triangles in the whole grid (indices, three per triangle), for comparing with triangles().
func triangle_count() -> int:
	return res_x * res_y * 2


## Advances the sim by delta seconds in steps of STEP. With run false the time only adds up, for the next call.
func step(delta: float, run: bool = true) -> void:
	_accumulator = minf(_accumulator + delta, STEP * MAX_CATCH_UP)
	while run and _accumulator >= STEP:
		_accumulator -= STEP
		if is_sleeping():
			_accumulator = 0.0
			return
		_substep()


func _substep() -> void:
	steps_done += 1
	_win_steps += 1
	# Asleep (stepped anyway), the window waits for something to wake it.
	if not _win_locked and (_win_dirty or (_win_steps >= WINDOW_REFRESH and not _win_fresh)):
		_refresh_window()
	var moved := 0.0
	var farthest := 0.0
	for k in _win_particles:
		var p := pos[k]
		pos[k] = p + (p - prev[k]) * DAMPING
		prev[k] = p
		_free[k] = 1.0
	for key: int in _pins:
		_free[_pins[key][0]] = 0.0
	for iteration in ITERATIONS:
		for key: int in _pins:
			var pin: Array = _pins[key]
			pos[pin[0]] = pin[1]
			var pull: Vector3 = pin[1] - anchor_target[pin[0]]
			_hold(pin[0])
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
		var count := _win_springs.size()
		for n in count:
			var s := _win_springs[n if iteration % 2 == 0 else count - 1 - n]
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
		for pass_index in STITCH_PASSES + 1:
			for s in _stitches:
				if c_active[s] == 1:
					_solve(s)
		for k in _win_particles:
			# Skin pulled far from its spot has come loose from what's under it, so a flap can be folded back. A cut's
			# edge lifted off the body isn't drawn back along it any more either: only skin lying on it is.
			var back := anchor_target[k] - pos[k]
			var hold := anchor[k]
			if hold > ANCHOR:
				hold = lerpf(hold, LOOSE_ANCHOR, clampf((pos[k].y - anchor_target[k].y) / LIFTED_OFF, 0.0, 1.0))
			pos[k] += back * hold * _free[k] * clampf(1.0 - back.length() / ANCHOR_REACH, 0.0, 1.0)
		# Inside the loop, so the springs even out what the floor pushes up instead of snapping from it.
		_stay_on_body()
		_stay_above_floor()
	for k in _win_particles:
		moved = maxf(moved, pos[k].distance_squared_to(prev[k]))
		farthest = maxf(farthest, pos[k].distance_squared_to(rest[k]))
	_farthest = farthest
	if tearing:
		_snap_overstretched()
	if _retract_dirty:
		_update_retraction()
	_still_steps = _still_steps + 1 if moved < SLEEP_EPSILON * SLEEP_EPSILON else 0
	if _still_steps >= SLEEP_STEPS:
		_win_fresh = true


## Picks the cells to simulate: around cuts, grips and skin that moved, plus WINDOW_MARGIN cells of still skin.
## While the skin moves the window only grows; once it fell asleep the next one is picked afresh.
func _refresh_window() -> void:
	_win_dirty = false
	_win_steps = 0
	var low := Vector2i(res_x + 1, res_y + 1)
	var high := Vector2i(-1, -1)
	var seeds := PackedInt32Array()
	for s in _severed:
		seeds.append(c_a[s])
		seeds.append(c_b[s])
	for s in _stitches:
		if c_active[s] == 1:
			seeds.append(c_a[s])
	for k in _win_particles:
		if pos[k].distance_squared_to(settled[k]) > WINDOW_MOVE * WINDOW_MOVE:
			seeds.append(k)
	for k in seeds:
		var at := cell_of(k)
		low = low.min(at)
		high = high.max(at)
	for key: int in _pins:
		# A grip drags a whole patch along: all of it moves.
		var at := cell_of(_pins[key][0])
		var patch := Vector2i(ceili(GRIP_PATCH * res_x), ceili(GRIP_PATCH * res_y))
		low = low.min(at - patch)
		high = high.max(at + patch)
	var fresh := _win_fresh or _win.size == Vector2i.ZERO
	_win_fresh = false
	if high.x < 0:
		if fresh:
			_set_window(Rect2i())
		return
	var margin := Vector2i(WINDOW_MARGIN, WINDOW_MARGIN)
	low = (low - margin).max(Vector2i.ZERO)
	high = (high + margin).min(Vector2i(res_x, res_y))
	if not fresh:
		low = low.min(_win.position)
		high = high.max(_win.end)
	var window := Rect2i(low, high - low)
	if window != _win:
		_set_window(window)


func _set_window(window: Rect2i) -> void:
	var before := _win
	for k in _win_particles:
		_free[k] = 0.0
	_win = window
	_win_particles = PackedInt32Array()
	_win_springs = PackedInt32Array()
	if window.size == Vector2i.ZERO:
		return
	for j in range(window.position.y, window.end.y + 1):
		for i in range(window.position.x, window.end.x + 1):
			var k := index(i, j)
			if fixed[k] == 0:
				_win_particles.append(k)
	# Every spring with an end inside: springs leaving each particle in the window and the ones arriving from the row
	# above and the column to the left of it.
	for j in range(maxi(window.position.y - 1, 0), window.end.y + 1):
		for i in range(maxi(window.position.x - 1, 0), window.end.x + 1):
			var k := index(i, j)
			for s: int in [spring_right[k], spring_down[k], spring_diag[k], spring_anti[k]]:
				if s >= 0:
					_win_springs.append(s)
	for k in _win_particles:
		_free[k] = 1.0
		# Skin coming back into the window starts out still, not with whatever motion it had when it left.
		if not _inside(before, cell_of(k)):
			prev[k] = pos[k]


## Column and row `at` lie in window rect (both ends included); nothing lies in an empty one.
static func _inside(rect: Rect2i, at: Vector2i) -> bool:
	return rect.size != Vector2i.ZERO and at.x >= rect.position.x and at.y >= rect.position.y and at.x <= rect.end.x and at.y <= rect.end.y


## The skin around a grip's jaws keeps its distance to the gripped particle, as stiff as thread: it turns with a flap
## folded back, but the pull is shared by the ring of springs around it instead of one spring at the jaws.
func _hold(k: int) -> void:
	var p := pos[k]
	for pass_index in STITCH_PASSES:
		for j in _patch(k)[2]:
			if _free[j] == 0.0:
				continue
			var d := pos[j] - p
			var length := d.length()
			if length > 0.00001:
				pos[j] = p + d * (rest[j].distance_to(rest[k]) * TENSION / length)


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


## Skin that started out exposed (inside the drape's opening) and is folded out over the drape lies on it. Skin under
## the drape stays under it: a flap pulled out over the drape can't lift it through the sheet. Unless it's been cut
## free there itself: a flap cut under the drape's edge takes it along.
## Skin lying in place rests on the body, it doesn't sink into it: over a curved body the sheet's tension would pull it
## a few millimeters inside the body's shape, and the site would show recessed next to the body model around it.
## Only near its rest point (BODY_REACH): the body's tangent plane there says nothing about where a flap pulled
## far away may go. Not the outermost strip either: it lies under the drape's frame, never drawn, and lifted onto the
## body it would count as lying on top of the drape.
func _stay_on_body() -> void:
	for k in _win_particles:
		if off[k] == 1 or _free[k] == 0.0 or _at_border(k):
			continue
		var moved := pos[k] - rest[k]
		var below := moved.dot(rest_normal[k])
		if below < 0.0 and moved.length_squared() < BODY_REACH * BODY_REACH:
			pos[k] -= rest_normal[k] * below


func _stay_above_floor() -> void:
	if not floor_at.is_valid():
		return
	_find_cut_free()
	for k in _win_particles:
		if _free[k] == 0.0:
			continue
		if exposed[k] == 1:
			# Only skin folded out: skin still near where it rests may lie just past the opening's edge, where the
			# drape's coarse grid can read below it on a steep flank and then step up as it moves, ratcheting it up.
			if not floor_open.has_point(Vector2(pos[k].x, pos[k].z)) and pos[k].distance_squared_to(settled[k]) > LIFTED_OFF * LIFTED_OFF:
				pos[k] = _above_floor(k, pos[k])
		elif _cut_free[k] == 0 and settled.size() == pos.size():
			# No higher than the drape lets it, or where it rested if the drape lies lower than that.
			var floor_y: float = floor_at.call(pos[k].x, pos[k].z)
			if not is_nan(floor_y):
				pos[k].y = minf(pos[k].y, maxf(floor_y - UNDER_DRAPE, settled[k].y))


## Marks the ends of every cut spring and the grid points next to them.
func _find_cut_free() -> void:
	if _cut_free_for == topology_version and _cut_free.size() == pos.size():
		return
	_cut_free_for = topology_version
	_cut_free.resize(pos.size())
	_cut_free.fill(0)
	for s in _severed:
		for k: int in [c_a[s], c_b[s]]:
			var at := cell_of(k)
			for j in range(maxi(at.y - 1, 0), mini(at.y + 1, res_y) + 1):
				for i in range(maxi(at.x - 1, 0), mini(at.x + 1, res_x) + 1):
					_cut_free[index(i, j)] = 1


## p, where particle k is, lifted onto the floor if it's exposed skin below it.
func _above_floor(k: int, p: Vector3) -> Vector3:
	if not floor_at.is_valid() or exposed.size() <= k or exposed[k] == 0:
		return p
	var floor_y: float = floor_at.call(p.x, p.z)
	if not is_nan(floor_y) and p.y < floor_y:
		p.y = floor_y
	return p


func _snap_overstretched() -> void:
	for s in _win_springs:
		_check_snap(s)
	for s in _stitches:
		_check_snap(s)


## A spring snaps once it's stretched past its limit, and the spring going on from it the same way at either end is
## stretched at least halfway there too: skin tears where it's overstretched over a length, not where one short spring
## of the grid takes a jump (the finer the grid, the shorter the spring that would).
func _check_snap(s: int) -> void:
	if c_active[s] == 0:
		return
	var a := c_a[s]
	var b := c_b[s]
	var limit := 1.0 + (c_break[s] - 1.0) * break_mult
	if pos[a].distance_to(pos[b]) / c_rest[s] <= limit:
		return
	if c_kind[s] == Kind.TISSUE:
		var halfway := 1.0 + (limit - 1.0) * 0.5
		var along := cell_of(b) - cell_of(a)
		var next := _spring_between(cell_of(b), cell_of(b) + along)
		var prev := _spring_between(cell_of(a) - along, cell_of(a))
		if not (_stretched(next, halfway) or _stretched(prev, halfway)):
			return
		_sever(s, Depth.SKIN, 0.5)
	else:
		c_active[s] = 0
	snapped.append([uv_of(a), uv_of(b), c_kind[s], s])
	topology_version += 1
	_retract_dirty = true
	_win_dirty = true


## The grid spring from grid point a to grid point b (column, row), -1 if there's none.
func _spring_between(a: Vector2i, b: Vector2i) -> int:
	if a.x < 0 or a.y < 0 or b.x < 0 or b.y < 0 or a.x > res_x or b.x > res_x or a.y > res_y or b.y > res_y:
		return -1
	var d := b - a
	var k := index(a.x, a.y)
	if d == Vector2i(1, 0):
		return spring_right[k]
	if d == Vector2i(0, 1):
		return spring_down[k]
	if d == Vector2i(1, 1):
		return spring_anti[k]
	if d == Vector2i(-1, 1):
		return spring_diag[index(a.x - 1, a.y)]
	return -1


## Spring s is whole and stretched past `ratio` of its rest length.
func _stretched(s: int, ratio: float) -> bool:
	return s >= 0 and c_active[s] == 1 and pos[c_a[s]].distance_to(pos[c_b[s]]) / c_rest[s] > ratio


func _spring(a: int, b: int, kind: Kind = Kind.TISSUE, tension: float = TENSION, strength: float = TISSUE_BREAK) -> int:
	c_a.append(a)
	c_b.append(b)
	c_rest.append(rest[a].distance_to(rest[b]) * tension)
	c_active.append(1)
	c_kind.append(kind)
	c_break.append(strength)
	c_depth.append(Depth.NONE)
	c_cross.append(0.5)
	c_cut_dir.append(Vector2.ZERO)
	c_muscle_closed.append(0)
	c_fat_closed.append(0)
	var s := c_a.size() - 1
	if kind == Kind.STITCH:
		_edge_to_stitch[_edge_key(a, b)] = s
		_stitches.append(s)
	return s


## The skin a grip on particle k drags along: particles within GRIP_PATCH reached without crossing a cut,
## weighted falling off in a straight line from the edge of the skin the grip holds (GRIP_HOLD) to 0 at the edge of
## the patch.
func _patch(k: int) -> Array:
	if _patch_version != topology_version:
		_patches.clear()
		_patch_version = topology_version
	if _patches.has(k):
		return _patches[k]
	_index_springs()
	var around := PackedInt32Array()
	var weights := PackedFloat32Array()
	var held := PackedInt32Array()
	var reach := GRIP_PATCH * (size.x + size.y) * 0.5 - GRIP_HOLD
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
			var meters := rest[other].distance_to(rest[k])
			weights.append(clampf(1.0 - (meters - GRIP_HOLD) / reach, 0.0, 1.0))
			if meters < GRIP_HOLD and fixed[other] == 0:
				held.append(other)
	_patches[k] = [around, weights, held]
	return _patches[k]


## Fills in _springs_of, the springs at each particle, the first time it's needed. Stitches added later aren't in it.
func _index_springs() -> void:
	if _springs_of.is_empty():
		_springs_of.resize(rest.size())
		for s in c_a.size():
			_springs_of[c_a[s]].append(s)
			_springs_of[c_b[s]].append(s)


## A tear (no cut_dir) runs square to the spring it snapped.
func _sever(s: int, depth: int, cross: float, cut_dir: Vector2 = Vector2.ZERO) -> void:
	if c_active[s] == 1:
		c_active[s] = 0
		c_cross[s] = cross
		if cut_dir == Vector2.ZERO:
			var along := rest[c_b[s]] - rest[c_a[s]]
			cut_dir = Vector2(-along.z, along.x)
		c_cut_dir[s] = cut_dir.normalized()
		_severed.append(s)
	c_depth[s] = maxi(c_depth[s], depth)
	if depth == Depth.MUSCLE:
		c_muscle_closed[s] = 0


## Particles on either side of a cut are drawn back from it, sideways along the skin, as far as the deepest cut near
## them retracts (RETRACT): less the further they are from it, and less toward the cut's ends, where its edges still
## hold together. Each cut spring pulls the strip of skin across from it. Stitched springs don't pull: the thread
## holds their edges together.
func _update_retraction() -> void:
	_retract_dirty = false
	for k in _retracted:
		anchor_target[k] = rest[k]
		_pull[k] = Vector3.ZERO
		_pull_amount[k] = 0.0
		if anchor[k] == MUSCLE_PULL or anchor[k] == GAPE_PULL:
			anchor[k] = LOOSE_ANCHOR
	for k in _lipped:
		anchor_target[k] = rest[k]
	if _pull.size() != rest.size():
		_pull.resize(rest.size())
		_pull_amount.resize(rest.size())
	_retracted = PackedInt32Array()
	_find_cut_ends()
	var cell := minf(size.x / res_x, size.y / res_y)
	for s in _severed:
		var depth := depth_of(s)
		if depth == Depth.NONE or _stitched(c_a[s], c_b[s]):
			continue
		var crossing := uv_of(c_a[s]).lerp(uv_of(c_b[s]), c_cross[s])
		var to_end := INF
		for end in _cut_ends:
			to_end = minf(to_end, ((crossing - end) * size).length())
		var taper := smoothstep(0.0, RETRACT_TAPER[depth], to_end)
		if taper <= 0.0:
			continue
		# Square to the cut, toward c_a's side, rising and falling with the skin like the spring does.
		var spring := rest[c_a[s]] - rest[c_b[s]]
		var square := Vector2(-c_cut_dir[s].y, c_cut_dir[s].x)
		if square.dot(Vector2(spring.x, spring.z)) < 0.0:
			square = -square
		var flat := Vector2(spring.x, spring.z).dot(square)
		var across := Vector3(square.x, spring.y / maxf(flat, 0.0005), square.y).normalized()
		var across_uv := Vector2(square.x / size.x, square.y / size.y)
		var middle := rest[c_a[s]].lerp(rest[c_b[s]], c_cross[s])
		var spread: float = RETRACT_SPREAD[depth]
		var reach := ceili(spread / cell)
		# The strip of skin straight across the cut from this spring, a grid point at a time.
		for n in range(-reach, reach + 1):
			var at := crossing + across_uv * (n * cell)
			var i := roundi(at.x * res_x)
			var j := roundi(at.y * res_y)
			if i < 1 or j < 1 or i >= res_x or j >= res_y:
				continue
			var k := index(i, j)
			var offset := Vector2(rest[k].x - middle.x, rest[k].z - middle.z)
			var side := offset.dot(square)
			var weight := (1.0 - absf(side) / spread) * clampf(1.0 - (offset - square * side).length() / cell, 0.0, 1.0) * taper
			# The spring's own ends: on the side they are on, even if the crossing was right at one of them.
			if k == c_a[s]:
				side = 1.0
			elif k == c_b[s]:
				side = -1.0
			if weight <= 0.0 or side == 0.0:
				continue
			if _pull_amount[k] == 0.0 and _pull[k] == Vector3.ZERO:
				_retracted.append(k)
			_pull[k] += across * signf(side) * weight
			_pull_amount[k] = maxf(_pull_amount[k], RETRACT[depth] * weight)
	for k in _retracted:
		var direction := _pull[k]
		if direction.length_squared() < 1e-8:
			continue
		var retract := _pull_amount[k]
		anchor_target[k] = rest[k] + direction.normalized() * retract
		anchor[k] = maxf(anchor[k], MUSCLE_PULL if retract >= RETRACT[Depth.SKIN] * 4.0 else GAPE_PULL)
	var active_lips := PackedInt32Array()
	for k in _lipped:
		if suture_lip[k] <= 0.0:
			continue
		anchor_target[k] += rest_normal[k] * suture_lip[k]
		anchor[k] = maxf(anchor[k], GAPE_PULL)
		active_lips.append(k)
	_lipped = active_lips


## The ends of the cuts: where each stroke started and the point of it farthest from there, unless another stroke runs
## through that point (a blade pressed in where the cut already runs makes a short cut inside the incision).
func _find_cut_ends() -> void:
	if _cut_ends_for == _cut_segments.size():
		return
	_cut_ends_for = _cut_segments.size()
	_cut_ends = PackedVector2Array()
	for n in _strokes.size():
		for p in _strokes[n]:
			if not _cut_ends.has(p) and not _on_other_stroke(p, n):
				_cut_ends.append(p)


## p lies within a millimeter of a segment of a stroke other than stroke n.
func _on_other_stroke(p: Vector2, n: int) -> bool:
	for m in _segment_stroke.size():
		if _segment_stroke[m] == n:
			continue
		var nearest := Geometry2D.get_closest_point_to_segment(p, _cut_segments[m * 2], _cut_segments[m * 2 + 1])
		if ((nearest - p) * size).length() < 0.001:
			return true
	return false


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
