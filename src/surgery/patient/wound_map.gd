class_name WoundMap
extends RefCounted
## CPU painted textures behind the skin shader. Every peer has its own copy.
## The host decides what gets painted and broadcasts paint ops, so all copies stay identical.
## Channel layout is documented in assets/shaders/skin.gdshader.

enum Layer { WOUNDS, FLUIDS }
enum Mode { MAX, ADD, SUB, MIN }

const SIZE := 256
## Channel ids, for readable call sites.
const CUT := 0
const BURN := 1
const BRUISE := 2
const STITCH := 3
const BLOOD := 0
const INK := 1
const IODINE := 2
const GRIME := 3
## Cut value at which the skin is open (keep in sync with skin.gdshader open_threshold).
const OPEN := 0.82

var images: Array[Image] = []
var textures: Array[ImageTexture] = []
var _dirty: Array[bool] = [false, false]


func _init() -> void:
	for i in 2:
		var image := Image.create(SIZE, SIZE, false, Image.FORMAT_RGBA8)
		images.append(image)
		textures.append(ImageTexture.create_from_image(image))


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
	var image := images[layer]
	var c := center * SIZE
	var r := maxf(radius * SIZE, 0.75)
	for y in range(maxi(0, floori(c.y - r)), mini(SIZE, ceili(c.y + r) + 1)):
		for x in range(maxi(0, floori(c.x - r)), mini(SIZE, ceili(c.x + r) + 1)):
			var d := Vector2(x + 0.5, y + 0.5).distance_to(c) / r
			if d > 1.0:
				continue
			var strength := value * (1.0 - d * d if soft else 1.0)
			var pixel := image.get_pixel(x, y)
			var current: float = pixel[channel]
			match mode:
				Mode.MAX: current = maxf(current, strength)
				Mode.ADD: current = minf(current + strength, 1.0)
				Mode.SUB: current = maxf(current - strength, 0.0)
				Mode.MIN: current = minf(current, value)
			pixel[channel] = current
			image.set_pixel(x, y, pixel)
	_dirty[layer] = true


func sample(layer: Layer, channel: int, uv: Vector2) -> float:
	var p := (uv * SIZE).floor()
	if p.x < 0 or p.y < 0 or p.x >= SIZE or p.y >= SIZE:
		return 0.0
	return images[layer].get_pixel(int(p.x), int(p.y))[channel]


func is_open(uv: Vector2) -> bool:
	return sample(Layer.WOUNDS, CUT, uv) > OPEN


## Uploads changed images to the GPU. Call once per frame.
func flush() -> void:
	for i in 2:
		if _dirty[i]:
			textures[i].update(images[i])
			_dirty[i] = false
