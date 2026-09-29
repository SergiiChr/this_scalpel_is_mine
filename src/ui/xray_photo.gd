class_name XrayPhoto
extends Control
## The X-ray film, full size on a lightbox, drawn from the shapes the cart captured.
## It develops slowly, from black, over a few seconds. Metal glows white, bone is light gray, masses are soft gray,
## gas pockets are dark.

const SIZE := Vector2(860, 1000)
## A portrait film sheet on the lightbox. Shape sizes below were tuned on a 540 px film and scale with it.
const FILM := Rect2(50, 50, 760, 900)
const SCALE := 760.0 / 540.0

var cart: XrayCart


func _init(source: XrayCart) -> void:
	cart = source
	custom_minimum_size = SIZE


## Redraws only while the print develops; once it's done the picture never changes.
func _process(_delta: float) -> void:
	queue_redraw()
	if cart.developed() >= 1.0:
		set_process(false)


func _draw() -> void:
	var develop := cart.developed()
	# Lightbox: a gray bezel around a cool white panel, the film clipped on top.
	draw_rect(Rect2(Vector2.ZERO, SIZE), Color(0.2, 0.21, 0.22))
	draw_rect(Rect2(Vector2(20, 20), SIZE - Vector2(40, 40)), Color(0.86, 0.9, 0.94))
	draw_rect(FILM, Color(0.02, 0.025, 0.035))
	var fade := func(c: Color) -> Color: return Color(0.02, 0.025, 0.035).lerp(c, develop)
	_draw_anatomy(cart.print_data.site, fade)
	for shape: Array in cart.print_data.shapes:
		_draw_shape(shape, fade)
	var rng := RandomNumberGenerator.new()
	rng.seed = cart.printed_at_msec
	for i in 900:
		var p := FILM.position + Vector2(rng.randf(), rng.randf()) * FILM.size
		draw_rect(Rect2(p, Vector2.ONE * 2), Color(1, 1, 1, rng.randf() * 0.07 * develop))
	var font := get_theme_default_font()
	var ink: Color = fade.call(Color(0.9, 0.92, 0.95))
	draw_string(font, FILM.position + Vector2(24, 52), "L", HORIZONTAL_ALIGNMENT_LEFT, -1, 40, ink)
	var label := "PORTABLE AP   %s" % str(cart.print_data.site).to_upper().replace("_", " ")
	draw_string(font, FILM.position + Vector2(24, 44), label, HORIZONTAL_ALIGNMENT_RIGHT, FILM.size.x - 48, 20, ink)
	# Film clips at the top of the lightbox.
	for x: float in [FILM.position.x + 120, FILM.end.x - 120]:
		draw_rect(Rect2(Vector2(x - 20, FILM.position.y - 14), Vector2(40, 22)), Color(0.55, 0.57, 0.6))


func _uv(uv: Vector2) -> Vector2:
	return FILM.position + uv.clamp(Vector2.ZERO, Vector2.ONE) * FILM.size


## Faint body structures so the metal has context.
func _draw_anatomy(site: String, fade: Callable) -> void:
	var bone: Color = fade.call(Color(0.42, 0.44, 0.46))
	var soft: Color = fade.call(Color(0.14, 0.16, 0.17))
	draw_rect(FILM.grow(-30), soft)
	match site:
		# Site u runs along the body (head to the right), v across it: the spine is horizontal, ribs cross it.
		"chest", "abdomen", "back", "shoulder":
			draw_rect(Rect2(_uv(Vector2(0.0, 0.46)), Vector2(1.0, 0.08) * FILM.size), bone)
			if site != "abdomen":
				var r := FILM.size.y * 0.5
				for i in 6:
					var apex := _uv(Vector2(0.18 + i * 0.14, 0.5))
					draw_arc(apex - Vector2(r, 0.0), r, -PI * 0.3, PI * 0.3, 24, bone, 7.0 * SCALE)
			else:
				draw_arc(_uv(Vector2(-0.15, 0.5)), FILM.size.y * 0.45, -PI * 0.4, PI * 0.4, 32, bone, 16.0 * SCALE)
		"head", "face":
			draw_circle(_uv(Vector2(0.5, 0.5)), FILM.size.x * 0.42, bone)
			draw_circle(_uv(Vector2(0.5, 0.5)), FILM.size.x * 0.39, soft)
		_:
			draw_rect(Rect2(_uv(Vector2(0.0, 0.42)), Vector2(1.0, 0.16) * FILM.size), bone)


func _draw_shape(shape: Array, fade: Callable) -> void:
	var kind: String = shape[0]
	var at := _uv(shape[1])
	var metal: Color = fade.call(Color(0.97, 0.97, 0.95))
	match kind:
		"bullet":
			draw_circle(at, 9.0 * SCALE, metal)
		"knife":
			draw_line(at, at + Vector2(0, 160) * SCALE, metal, 10.0 * SCALE)
		"figurine":
			draw_rect(Rect2(at - Vector2(14, 60) * SCALE, Vector2(28, 120) * SCALE), metal)
		"tool":
			var dir := Vector2.UP.rotated(float(shape[3]))
			draw_line(at - dir * 70.0 * SCALE, at + dir * 70.0 * SCALE, metal, 7.0 * SCALE)
		"rib":
			# A rib end runs from the break toward the side it came from (yaw 180 points the other way).
			var along := Vector2(0.0, -70.0 if absf(float(shape[3])) > PI * 0.5 else 70.0) * SCALE
			draw_line(at, at + along + Vector2(-6.0, 0.0), fade.call(Color(0.7, 0.7, 0.68)), 9.0 * SCALE)
		"splinter":
			draw_line(at - Vector2(10.0, 12.0) * SCALE, at + Vector2(10.0, 12.0) * SCALE, fade.call(Color(0.75, 0.75, 0.72)), 4.0 * SCALE)
		"bone", "fragment":
			draw_rect(Rect2(at - Vector2(120, 14) * SCALE, Vector2(240, 28) * SCALE), fade.call(Color(0.62, 0.62, 0.6)))
		"tumor", "clot", "appendix":
			draw_circle(at, 22.0 * SCALE, fade.call(Color(0.3, 0.32, 0.33)))
		"organ":
			draw_circle(at, 55.0 * SCALE, fade.call(Color(0.2, 0.22, 0.23)))
		"air":
			draw_circle(at, 45.0 * SCALE, fade.call(Color(0.0, 0.0, 0.0)))
		"fluid":
			draw_circle(at, 50.0 * SCALE, fade.call(Color(0.28, 0.3, 0.3)))
