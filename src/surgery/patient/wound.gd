class_name Wound
extends RefCounted
## One wound. Skin wounds are polylines in site UV space, internal wounds are a point under the skin.
## Closure is tracked per segment of length ("bins"), so a wound is only closed once you've sewn along all of it.

enum Kind { CUT, TEAR, BURN, PUNCTURE, GUNSHOT, INTERNAL }

## ml/s per meter of fully deep, fully open wound.
const BLEED_PER_METER := 35.0
const BIN_LENGTH_UV := 0.015

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
## Weighted closure quality, lower bursts easier.
var closure_quality := 1.0


func _init(wound_id: int, wound_kind: Kind, a: Vector2, wound_depth: float) -> void:
	id = wound_id
	kind = wound_kind
	points.append(a)
	depth = wound_depth
	_resize_bins()


func is_internal() -> bool:
	return kind == Kind.INTERNAL


func extend(point: Vector2) -> void:
	points.append(point)
	_resize_bins()


func length_uv() -> float:
	var total := 0.0
	for i in range(1, points.size()):
		total += points[i - 1].distance_to(points[i])
	return total


func closure() -> float:
	if bins.is_empty():
		return 0.0
	var total := 0.0
	for value in bins:
		total += value
	return total / bins.size()


func bleed_rate(site_size: float, bleed_mult: float) -> float:
	if kind == Kind.BURN:
		return 0.0
	var base := maxf(length_uv() * site_size, 0.01) * depth * BLEED_PER_METER
	if kind in [Kind.GUNSHOT, Kind.PUNCTURE, Kind.INTERNAL]:
		base = maxf(base, depth * 2.0)
	var open_factor := 1.0 + opened * 0.5
	return base * bleed_mult * open_factor * (1.0 - closure()) * (1.0 - cauterized) * (1.0 - clamped) * (1.0 - held)


func distance_to(uv: Vector2) -> float:
	if points.size() == 1:
		return points[0].distance_to(uv)
	var best := INF
	for i in range(1, points.size()):
		best = minf(best, uv.distance_to(Geometry2D.get_closest_point_to_segment(uv, points[i - 1], points[i])))
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


func _resize_bins() -> void:
	bins.resize(maxi(1, ceili(length_uv() / BIN_LENGTH_UV)))
