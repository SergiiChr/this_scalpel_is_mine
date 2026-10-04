class_name Wound
extends RefCounted
## One wound. Skin wounds are polylines in site UV space, internal wounds are a point under the skin.
## Closure is tracked per segment of length ("bins"), so a wound is only closed once you've sewn along all of it.

enum Kind { CUT, TEAR, BURN, PUNCTURE, GUNSHOT, INTERNAL }

## ml/s per meter of fully deep, fully open wound.
const BLEED_PER_METER := 35.0
const BIN_LENGTH_UV := 0.015
## A stroke reports a point every frame; closer than this the last point just moves, so long cuts stay cheap to query.
const POINT_SPACING_UV := 0.006
## Wound depth (0..1) from which it goes through the muscle, see Patient._tissue_depth().
const MUSCLE_DEPTH := 0.7

var id: int
var kind: Kind
var points := PackedVector2Array()
## 0..1. 0.7+ goes through the skin and can be opened.
var depth := 0.5
## Internal wounds: meters below the skin surface.
var depth_m := 0.0
var opened := 0.0
var cauterized := 0.0
var clamped := 0.0
## Pressure from gauze or the patient's own hands. Patient grip decays over time.
var held := 0.0
var dirty := false
var made_by_surgeon := false
## Closure progress per bin, 0..1.
var bins := PackedFloat32Array()
## Muscle closure per bin, 0..1, for wounds through the muscle (sewn from inside the opening, before the skin).
var muscle := PackedFloat32Array()
## Weighted closure quality, lower bursts easier.
var closure_quality := 1.0
var _length := 0.0


func _init(wound_id: int, wound_kind: Kind, a: Vector2, wound_depth: float) -> void:
	id = wound_id
	kind = wound_kind
	points.append(a)
	depth = wound_depth
	_resize_bins()


func is_internal() -> bool:
	return kind == Kind.INTERNAL


func extend(point: Vector2) -> void:
	var last := points.size() - 1
	if last >= 1 and points[last - 1].distance_to(point) < POINT_SPACING_UV:
		_length += points[last - 1].distance_to(point) - points[last - 1].distance_to(points[last])
		points[last] = point
	else:
		_length += points[last].distance_to(point)
		points.append(point)
	_resize_bins()


func length_uv() -> float:
	return _length


func closure() -> float:
	if bins.is_empty():
		return 0.0
	var total := 0.0
	for value in bins:
		total += value
	return total / bins.size()


## Cut through the muscle: its muscle has to be sewn before the skin will close over it.
func through_muscle() -> bool:
	return not is_internal() and kind != Kind.BURN and depth >= MUSCLE_DEPTH


## leak (0..1) is how much blood still gets through closures and packing: thinned blood or high pressure.
## Cautery and clamps seal regardless.
func bleed_rate(site_size: float, bleed_mult: float, leak: float = 0.0) -> float:
	if kind == Kind.BURN:
		return 0.0
	var base := maxf(length_uv() * site_size, 0.01) * depth * BLEED_PER_METER
	if kind in [Kind.GUNSHOT, Kind.PUNCTURE, Kind.INTERNAL]:
		base = maxf(base, depth * 2.0)
	var open_factor := 1.0 + opened * 0.5
	var sealed := (1.0 - closure() * (1.0 - leak)) * (1.0 - held * (1.0 - leak))
	return base * bleed_mult * open_factor * sealed * (1.0 - cauterized) * (1.0 - clamped)


func distance_to(uv: Vector2) -> float:
	return uv.distance_to(closest_point(uv))


## The point of the wound's line nearest to uv.
func closest_point(uv: Vector2) -> Vector2:
	var best := points[0]
	for i in range(1, points.size()):
		var on := Geometry2D.get_closest_point_to_segment(uv, points[i - 1], points[i])
		if on.distance_squared_to(uv) < best.distance_squared_to(uv):
			best = on
	return best


## Index of the closure bin nearest to uv.
func bin_at(uv: Vector2) -> int:
	var travelled := 0.0
	var best_bin := 0
	var best_dist := INF
	for i in range(1, points.size()):
		var a := points[i - 1]
		var b := points[i]
		var closest := Geometry2D.get_closest_point_to_segment(uv, a, b)
		var dist := uv.distance_to(closest)
		if dist < best_dist:
			best_dist = dist
			best_bin = int((travelled + a.distance_to(closest)) / BIN_LENGTH_UV)
		travelled += a.distance_to(b)
	return clampi(best_bin, 0, bins.size() - 1)


## UV position of a bin's center along the polyline.
func bin_position(bin: int) -> Vector2:
	var target := (bin + 0.5) * BIN_LENGTH_UV
	var travelled := 0.0
	for i in range(1, points.size()):
		var seg := points[i - 1].distance_to(points[i])
		if travelled + seg >= target:
			return points[i - 1].lerp(points[i], (target - travelled) / maxf(seg, 0.0001))
		travelled += seg
	return points[points.size() - 1]


func midpoint() -> Vector2:
	return bin_position(bins.size() / 2) if points.size() > 1 else points[0]


## Bins only grow: moving the last point back a little never throws away closure progress.
func _resize_bins() -> void:
	bins.resize(maxi(maxi(bins.size(), ceili(_length / BIN_LENGTH_UV)), 1))
	muscle.resize(bins.size())
