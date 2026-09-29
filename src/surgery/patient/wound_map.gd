class_name WoundMap
extends RefCounted
## CPU painted textures behind the skin shader. Every peer has its own copy.
## The host decides what gets painted and broadcasts paint ops, so all copies stay identical.
## Channel layout is documented in assets/shaders/skin.gdshader.

enum Layer { WOUNDS, FLUIDS }
enum Mode { MAX, ADD, SUB, MIN }

const SIZE := 512
## Channel ids, for readable call sites.
const CUT := 0
const BURN := 1
const BRUISE := 2
const STITCH := 3
const BLOOD := 0
const INK := 1
const IODINE := 2
const GRIME := 3

var images: Array[Image] = []
var textures: Array[ImageTexture] = []
## The pixels painted into, 4 bytes per texel. Copied into images and textures once per frame by flush().
## Raw bytes are several times faster to paint from GDScript than Image.get_pixel() / set_pixel().
var _data: Array[PackedByteArray] = []
var _dirty: Array[bool] = [false, false]


func _init() -> void:
	for i in 2:
		var image := Image.create(SIZE, SIZE, false, Image.FORMAT_RGBA8)
		images.append(image)
		textures.append(ImageTexture.create_from_image(image))
		_data.append(image.get_data())


## Stamps hard-edged disks along a segment. jitter > 0 makes a ragged, torn line.
func stroke(layer: Layer, channel: int, a: Vector2, b: Vector2, radius: float, value: float, mode: Mode, jitter: float = 0.0, seed_value: int = 0) -> void:
	var step := maxf(radius * 0.5, 0.5 / SIZE)
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
	var c := center * SIZE
	var r := maxf(radius * SIZE, 0.75)
	for op: Array in ops:
		var channel: int = op[0]
		var k: float = float(op[1]) * 255.0
		var mode: Mode = op[2]
		var hard := roundi(k)
		for y in range(maxi(0, floori(c.y - r)), mini(SIZE, ceili(c.y + r) + 1)):
			var dy := (y + 0.5 - c.y) / r
			if dy * dy > 1.0:
				continue
			var half := sqrt(1.0 - dy * dy) * r
			var row := y * SIZE * 4 + channel
			var from := maxi(0, ceili(c.x - half - 0.5))
			var to := mini(SIZE, floori(c.x + half - 0.5) + 1)
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
	# Packed arrays are copy on write: keep the painted copy.
	_data[layer] = data
	_dirty[layer] = true


## One channel of the painted map at uv, 0..1, as painted so far (no need to wait for flush()).
func value(layer: Layer, channel: int, uv: Vector2) -> float:
	var at := Vector2i((uv.clamp(Vector2.ZERO, Vector2.ONE) * (SIZE - 1)).floor())
	return _data[layer][(at.y * SIZE + at.x) * 4 + channel] / 255.0


## Uploads changed images to the GPU. Call once per frame.
func flush() -> void:
	for i in 2:
		if _dirty[i]:
			images[i].set_data(SIZE, SIZE, false, Image.FORMAT_RGBA8, _data[i])
			textures[i].update(images[i])
			_dirty[i] = false
