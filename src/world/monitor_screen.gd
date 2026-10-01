class_name MonitorScreen
extends Control
## What the bedside monitor shows, drawn in 2D and put on the screen through a SubViewport.
## Laid out like a real patient monitor: sweeping waveforms on the left, numbers in their wave's color on the right,
## alarms in the top bar and lab results along the bottom.

const SIZE := Vector2(760, 500)
const BG := Color(0.01, 0.015, 0.02)
const BAR := Color(0.11, 0.13, 0.17)
const LINE := Color(0.2, 0.23, 0.28)
const ECG := Color(0.3, 1.0, 0.4)
const PLETH := Color(0.3, 0.85, 1.0)
const NIBP := Color(1.0, 0.45, 0.45)
const TEMP := Color(0.9, 0.9, 0.9)
const RESP := Color(1.0, 0.9, 0.3)
const LAB := Color(1.0, 0.75, 0.35)
const ALARM := Color(0.9, 0.12, 0.1)

const HEADER := 34.0
## Waves run from WAVE_LEFT to just short of WAVE_WIDTH, past the row labels.
const WAVE_LEFT := 56.0
const WAVE_WIDTH := 480.0
const WAVE_ROW := 106.0
const LAB_TOP := HEADER + WAVE_ROW * 3.0 + 8.0
## Samples a second, and seconds across the screen before the sweep starts over.
const SAMPLE_HZ := 120.0
const SWEEP_SECONDS := 6.0
## Blank stretch ahead of the sweep, in samples.
const GAP := 18

var _font: Font = ThemeDB.fallback_font
## One ring of samples per wave (ecg, pleth, resp), written at _head.
var _waves: Array[PackedFloat32Array] = []
var _head := 0
var _carry := 0.0
var _time := 0.0
## Breath cycle 0..1, kept apart from _time so a changing rate doesn't make the trace jump.
var _breath := 0.0
var _since_beat := 10.0
var _period := 0.8
var _arrest := Vitals.Rhythm.SINUS
var _pulse := 1.0
var _resp_rate := 0
var _numbers: Dictionary = {}
var _alarms := PackedStringArray()
var _lab := ""


func _init() -> void:
	size = SIZE
	for i in 3:
		var wave := PackedFloat32Array()
		wave.resize(int(SAMPLE_HZ * SWEEP_SECONDS))
		_waves.append(wave)


## A new heartbeat: the next QRS starts now.
func beat(heart_rate: float) -> void:
	_since_beat = 0.0
	_period = 60.0 / maxf(heart_rate, 1.0)


func show_lab(text: String) -> void:
	_lab = text.trim_prefix("LAB: ")


func tick(v: Vitals, alive: bool, alarms: PackedStringArray, delta: float) -> void:
	_arrest = v.rhythm if alive else Vitals.Rhythm.ASYSTOLE
	_pulse = clampf(v.systolic / 120.0, 0.0, 1.2) if _arrest == Vitals.Rhythm.SINUS else 0.0
	_resp_rate = 0 if v.anesthesia > 0.7 or not alive else int(14 + v.panic * 12)
	_alarms = alarms
	_numbers = {
		"hr": "%d" % v.heart_rate,
		"rhythm": v.rhythm_name(),
		"spo2": "%d" % v.spo2 if _pulse > 0.0 else "-?-",
		"nibp": "%d/%d" % [v.systolic, v.systolic * 0.65],
		"map": "(%d)" % (v.systolic * (1.0 + 0.65 * 2.0) / 3.0),
		"temp": "%.1f" % v.temperature,
		"rr": "%d" % _resp_rate if _resp_rate > 0 else "--",
	}
	_carry += delta
	while _carry >= 1.0 / SAMPLE_HZ:
		_carry -= 1.0 / SAMPLE_HZ
		_time += 1.0 / SAMPLE_HZ
		_since_beat += 1.0 / SAMPLE_HZ
		_waves[0][_head] = _ecg()
		_waves[1][_head] = _pleth()
		_breath = fmod(_breath + _resp_rate / 60.0 / SAMPLE_HZ, 1.0)
		_waves[2][_head] = sin(_breath * TAU) * 0.8
		_head = (_head + 1) % _waves[0].size()
	queue_redraw()


## Lead II, -1..1: P, QRS and T around each beat, chaos in V-fib, a faint wobble in asystole.
func _ecg() -> float:
	match _arrest:
		Vitals.Rhythm.VFIB:
			return sin(_time * TAU * 4.7) * 0.45 + sin(_time * TAU * 6.3 + 1.0) * 0.25 + sin(_time * TAU * 2.9) * 0.2
		Vitals.Rhythm.ASYSTOLE:
			return sin(_time * TAU * 0.7) * 0.02
	var t := _since_beat
	return (
		_bump(t, _period - 0.14, 0.025, 0.12) + _bump(t, 0.0, 0.01, -0.12) + _bump(t, 0.025, 0.014, 1.0)
		+ _bump(t, 0.055, 0.012, -0.3) + _bump(t, 0.26, 0.05, 0.25)
	)


## Finger pulse: a quick rise after each beat and a smaller bump past the dicrotic notch.
func _pleth() -> float:
	var t := _since_beat
	return (_bump(t, 0.28, 0.08, 1.4) + _bump(t, 0.5, 0.08, 0.5)) * _pulse - 0.7 * _pulse


func _bump(t: float, center: float, width: float, height: float) -> float:
	return height * exp(-pow((t - center) / width, 2.0))


func _draw() -> void:
	draw_rect(Rect2(Vector2.ZERO, SIZE), BG)
	if _numbers.is_empty():
		return
	var flash := fmod(Time.get_ticks_msec() * 0.002, 1.0) > 0.5
	var alarms := "".join(_alarms)
	_draw_header(flash)
	var labels: PackedStringArray = ["II", "Pleth", "Resp"]
	var colors: Array[Color] = [ECG, PLETH, RESP]
	for i in 3:
		var top := HEADER + WAVE_ROW * i
		_text(labels[i], Vector2(10, top + 20), 16, colors[i])
		_draw_wave(_waves[i], top + WAVE_ROW * 0.55, WAVE_ROW * 0.36, colors[i])
		draw_line(Vector2(0, top + WAVE_ROW), Vector2(SIZE.x, top + WAVE_ROW), LINE)
	draw_line(Vector2(WAVE_WIDTH, HEADER), Vector2(WAVE_WIDTH, LAB_TOP - 8), LINE)
	var at := Vector2(WAVE_WIDTH + 14, HEADER)
	_draw_number("HR", "bpm", _numbers.hr, at, ECG, 66, _arrest != Vitals.Rhythm.SINUS and flash)
	_text(_numbers.rhythm, at + Vector2(0, 92), 15, ECG)
	at.y += WAVE_ROW
	_draw_number("SpO2", "%", _numbers.spo2, at, PLETH, 66, "SpO2" in alarms and flash)
	at.y += WAVE_ROW
	_draw_number("NIBP", "mmHg", _numbers.nibp, at, NIBP, 40, "NIBP" in alarms and flash)
	_text(_numbers.map, at + Vector2(0, 66), 18, NIBP)
	_text("T  %s °C" % _numbers.temp, at + Vector2(0, 96), 17, TEMP)
	_text("RR  %s" % _numbers.rr, Vector2(SIZE.x - 12, at.y + 96), 17, RESP, HORIZONTAL_ALIGNMENT_RIGHT)
	_draw_lab()


func _draw_header(flash: bool) -> void:
	draw_rect(Rect2(0, 0, SIZE.x, HEADER), BAR)
	_text("OR 1    Adult", Vector2(10, 23), 16, TEMP)
	_text(Time.get_time_string_from_system().left(5), Vector2(SIZE.x - 10, 23), 16, TEMP, HORIZONTAL_ALIGNMENT_RIGHT)
	if _alarms.is_empty():
		return
	var box := Rect2(170, 3, 420, HEADER - 6)
	draw_rect(box, ALARM if flash else ALARM.darkened(0.5))
	_text("   ".join(_alarms), Vector2(box.get_center().x, 23), 17, Color.WHITE, HORIZONTAL_ALIGNMENT_CENTER)


## Ring buffer as a sweep: drawn up to the write head, a gap, then the old trace after it.
func _draw_wave(wave: PackedFloat32Array, middle: float, half_height: float, color: Color) -> void:
	var step := (WAVE_WIDTH - WAVE_LEFT - 10.0) / wave.size()
	var before := PackedVector2Array()
	var after := PackedVector2Array()
	for i in wave.size():
		var ahead := posmod(i - _head, wave.size())
		if ahead < GAP:
			continue
		var point := Vector2(WAVE_LEFT + i * step, middle - clampf(wave[i], -1.2, 1.2) * half_height)
		(before if i < _head else after).append(point)
	for line: PackedVector2Array in [before, after]:
		if line.size() > 1:
			draw_polyline(line, color, 2.0, true)


func _draw_number(title: String, unit: String, value: String, at: Vector2, color: Color, font_size: int, alarm: bool) -> void:
	var width := SIZE.x - at.x - 8
	if alarm:
		draw_rect(Rect2(at.x - 8, at.y + 2, width + 14, WAVE_ROW - 4), ALARM)
	var ink := Color.WHITE if alarm else color
	_text(title, at + Vector2(0, 20), 16, ink)
	_text(unit, at + Vector2(width - 4, 20), 13, ink, HORIZONTAL_ALIGNMENT_RIGHT)
	_text(value, at + Vector2(width - 4, 28 + font_size * 0.8), font_size, ink, HORIZONTAL_ALIGNMENT_RIGHT)


func _draw_lab() -> void:
	_text("Lab", Vector2(10, LAB_TOP + 14), 15, LAB.darkened(0.3))
	var body := _lab if not _lab.is_empty() else "No results yet."
	draw_multiline_string(_font, Vector2(56, LAB_TOP + 14), body, HORIZONTAL_ALIGNMENT_LEFT, SIZE.x - 66, 16, 6, LAB if not _lab.is_empty() else LAB.darkened(0.5))


## Text with its baseline at pos.y; right aligned text ends at pos.x, centered text centers on it.
func _text(text: String, pos: Vector2, font_size: int, color: Color, align := HORIZONTAL_ALIGNMENT_LEFT) -> void:
	var width := _font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size).x
	var x := pos.x - width if align == HORIZONTAL_ALIGNMENT_RIGHT else pos.x - width * 0.5 if align == HORIZONTAL_ALIGNMENT_CENTER else pos.x
	draw_string(_font, Vector2(x, pos.y), text, HORIZONTAL_ALIGNMENT_LEFT, -1, font_size, color)
