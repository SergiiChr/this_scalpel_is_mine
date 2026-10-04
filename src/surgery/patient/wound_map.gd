class_name WoundMap
extends RefCounted
## CPU painted textures behind the skin shader. Every peer has its own copy.
## The host decides what gets painted and broadcasts paint ops, so all copies stay identical.
## Channel layout is documented in assets/shaders/skin.gdshader.

enum Layer { WOUNDS, FLUIDS }
enum Mode { MAX, ADD, SUB, MIN }

## Texture sizes range from MIN_SIZE to MAX_SIZE, picked so a texel covers about TEXEL meters on every site:
## a pad or blood pool then costs the same to paint on a small face as on a big belly.
const MIN_SIZE := 128
const MAX_SIZE := 512
const TEXEL := 0.0008
## Channel ids, for readable call sites.
const CUT := 0
const BURN := 1
const BRUISE := 2
const STITCH := 3
## A sub-threshold stitch value marks the narrow incision line left under a tied skin suture. Values >= 0.5 are
## rendered as thread or staples; this value only lets the closed cut's pink edge show on the simulated skin.
const CLOSED_SEAM := 0.25
const BLOOD := 0
const INK := 1
const IODINE := 2
const GRIME := 3

## Texels per side of both textures.
var size := MAX_SIZE
var images: Array[Image] = []
var textures: Array[ImageTexture] = []
## The pixels painted into, 4 bytes per texel. Copied into images and textures once per frame by flush().
## Raw bytes are several times faster to paint from GDScript than Image.get_pixel() / set_pixel().
var _data: Array[PackedByteArray] = []
var _dirty: Array[bool] = [false, false]
## Which cells of a coarse CELLS x CELLS grid have ever been painted into, per layer and channel (4 bytes per cell).
## Subtracting where a channel was never painted changes nothing, so a wipe over clean skin skips it.
var _painted: Array[PackedByteArray] = []
const CELLS := 16


func _init(texels: int = MAX_SIZE) -> void:
	size = texels
	for i in 2:
		var image := Image.create(size, size, false, Image.FORMAT_RGBA8)
		images.append(image)
		textures.append(ImageTexture.create_from_image(image))
		_data.append(image.get_data())
		var cells := PackedByteArray()
		cells.resize(CELLS * CELLS * 4)
		_painted.append(cells)


## The texture size for a site of this size in meters (a power of two).
static func size_for(site_size: Vector2) -> int:
	return clampi(1 << roundi(log(maxf(site_size.x, site_size.y) / TEXEL) / log(2.0)), MIN_SIZE, MAX_SIZE)


## Stamps hard-edged disks along a segment. jitter > 0 makes a ragged, torn line.
func stroke(layer: Layer, channel: int, a: Vector2, b: Vector2, radius: float, value: float, mode: Mode, jitter: float = 0.0, seed_value: int = 0) -> void:
	var step := maxf(radius * 0.5, 0.5 / size)
	var steps := maxi(1, ceili(a.distance_to(b) / step))
	var rng := RandomNumberGenerator.new()
	rng.seed = seed_value
	for i in steps + 1:
		var p := a.lerp(b, float(i) / steps)
		if jitter > 0.0:
			p += Vector2(rng.randf_range(-jitter, jitter), rng.randf_range(-jitter, jitter))
		disk(layer, channel, p, radius, value, mode, false)


func disk(layer: Layer, channel: int, center: Vector2, radius: float, value: float, mode: Mode, soft: bool = true) -> void:
	disk_ops(layer, center, radius, [[channel, value, mode]], soft)


## Paints several channels of one disk: ops is [[channel, value, Mode], ...].
## Each channel gets its own tight loop over the disk's rows, with the mode picked outside the loop:
## per texel GDScript work is what makes painting slow, and a wipe paints big disks every frame.
func disk_ops(layer: Layer, center: Vector2, radius: float, ops: Array, soft: bool = true) -> void:
	var data := _data[layer]
	var c := center * size
	var r := maxf(radius * size, 0.75)
	var painted := _painted[layer]
	var cell_from := (Vector2i((center - Vector2.ONE * radius) * CELLS)).clamp(Vector2i.ZERO, Vector2i.ONE * (CELLS - 1))
	var cell_to := (Vector2i((center + Vector2.ONE * radius) * CELLS)).clamp(Vector2i.ZERO, Vector2i.ONE * (CELLS - 1))
	for op: Array in ops:
		var channel: int = op[0]
		var k: float = float(op[1]) * 255.0
		var mode: Mode = op[2]
		var hard := roundi(k)
		var any := false
		for cy in range(cell_from.y, cell_to.y + 1):
			for cx in range(cell_from.x, cell_to.x + 1):
				var cell := (cy * CELLS + cx) * 4 + channel
				any = any or painted[cell] == 1
				if mode in [Mode.MAX, Mode.ADD] and k > 0.0:
					painted[cell] = 1
		if mode in [Mode.SUB, Mode.MIN] and not any:
			continue
		for y in range(maxi(0, floori(c.y - r)), mini(size, ceili(c.y + r) + 1)):
			var dy := (y + 0.5 - c.y) / r
			if dy * dy > 1.0:
				continue
			var half := sqrt(1.0 - dy * dy) * r
			var row := y * size * 4 + channel
			var from := maxi(0, ceili(c.x - half - 0.5))
			var to := mini(size, floori(c.x + half - 0.5) + 1)
			var edge := 1.0 - dy * dy
			match mode:
				Mode.MAX:
					for x in range(from, to):
						var dx := (x + 0.5 - c.x) / r
						var i := row + x * 4
						data[i] = maxi(data[i], roundi(k * (edge - dx * dx)) if soft else hard)
				Mode.ADD:
					for x in range(from, to):
						var dx := (x + 0.5 - c.x) / r
						var i := row + x * 4
						data[i] = mini(data[i] + (roundi(k * (edge - dx * dx)) if soft else hard), 255)
				Mode.SUB:
					for x in range(from, to):
						var dx := (x + 0.5 - c.x) / r
						var i := row + x * 4
						data[i] = maxi(data[i] - (roundi(k * (edge - dx * dx)) if soft else hard), 0)
				Mode.MIN:
					for x in range(from, to):
						var i := row + x * 4
						data[i] = mini(data[i], hard)
	# Packed arrays are copy on write: keep the painted copies.
	_data[layer] = data
	_painted[layer] = painted
	_dirty[layer] = true


## One channel of the painted map at uv, 0..1, as painted so far (no need to wait for flush()).
func value(layer: Layer, channel: int, uv: Vector2) -> float:
	var at := Vector2i((uv.clamp(Vector2.ZERO, Vector2.ONE) * (size - 1)).floor())
	return _data[layer][(at.y * size + at.x) * 4 + channel] / 255.0


## Uploads changed images to the GPU. Call once per frame.
func flush() -> void:
	for i in 2:
		if _dirty[i]:
			images[i].set_data(size, size, false, Image.FORMAT_RGBA8, _data[i])
			textures[i].update(images[i])
			_dirty[i] = false
