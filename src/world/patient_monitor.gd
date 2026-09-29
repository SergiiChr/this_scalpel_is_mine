class_name PatientMonitor
extends Node3D
## Bedside monitor: vitals on a screen and a pulse oximeter beep generated in code.
## Beep pitch follows SpO2 like real monitors, so you can hear oxygen dropping without looking.

const SAMPLE_RATE := 22050.0
const BEEP_LENGTH := 0.09

var _screen: Label3D
var _lab: Label3D
var _player: AudioStreamPlayer3D
var _playback: AudioStreamGeneratorPlayback
var _beat_timer := 0.0
var _tone_left := 0.0
var _tone_freq := 880.0
var _phase := 0.0
var _alarm_timer := 0.0


func build() -> void:
	ModelSlot.instantiate("props", "monitor", self, {"screen": Materials.glow(Color(0.02, 0.05, 0.04))})
	_screen = Shapes.label(self, "", Vector3(-0.17, 0.11, 0.012), 36)
	_screen.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	_screen.vertical_alignment = VERTICAL_ALIGNMENT_TOP
	_screen.modulate = Color(0.4, 1.0, 0.55)
	_lab = Shapes.label(self, "", Vector3(-0.17, -0.22, 0.012), 24)
	_lab.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	_lab.vertical_alignment = VERTICAL_ALIGNMENT_TOP
	_lab.modulate = Color(0.85, 0.85, 0.5)
	var stream := AudioStreamGenerator.new()
	stream.mix_rate = SAMPLE_RATE
	stream.buffer_length = 0.2
	_player = AudioStreamPlayer3D.new()
	_player.stream = stream
	_player.bus = "SFX"
	_player.unit_size = 4.0
	add_child(_player)
	if DisplayServer.get_name() != "headless":
		_player.play()
		_playback = _player.get_stream_playback()


func show_lab(text: String) -> void:
	_lab.text = text


func _process(delta: float) -> void:
	var v := Surgery.current.patient.vitals
	var alarm := v.is_arrested() or v.spo2 < 88.0 or v.systolic < 80.0
	_screen.text = "HR   %3d  %s\nSpO2 %3d %%\nBP   %3d / %d\nTEMP %.1f C\nRESP %s\n%s" % [
		v.heart_rate, v.rhythm_name(), v.spo2, v.systolic, v.systolic * 0.65, v.temperature,
		"--" if v.anesthesia > 0.7 else "%d" % (14 + v.panic * 12), "!! ALARM !!" if alarm and fmod(Time.get_ticks_msec() * 0.002, 1.0) > 0.5 else "",
	]
	_screen.modulate = Color(1.0, 0.35, 0.3) if alarm else Color(0.4, 1.0, 0.55)
	if _playback == null or Sfx.deaf:
		return
	_schedule_tones(v, alarm, delta)
	_fill_buffer()


func _schedule_tones(v: Vitals, alarm: bool, delta: float) -> void:
	if v.rhythm == Vitals.Rhythm.ASYSTOLE or not Surgery.current.patient.alive:
		_tone_freq = 960.0
		_tone_left = 0.1
		return
	if v.heart_rate > 1.0 and v.rhythm == Vitals.Rhythm.SINUS:
		_beat_timer -= delta
		if _beat_timer <= 0.0:
			_beat_timer = 60.0 / v.heart_rate
			_tone_freq = remap(clampf(v.spo2, 70.0, 100.0), 70.0, 100.0, 440.0, 880.0)
			_tone_left = BEEP_LENGTH
	_alarm_timer -= delta
	if alarm and _alarm_timer <= 0.0:
		_alarm_timer = 1.2
		_tone_freq = 1300.0
		_tone_left = 0.25


func _fill_buffer() -> void:
	var frames := _playback.get_frames_available()
	for i in frames:
		var sample := 0.0
		if _tone_left > 0.0:
			sample = sin(_phase * TAU) * 0.25
			_phase = fmod(_phase + _tone_freq / SAMPLE_RATE, 1.0)
			_tone_left -= 1.0 / SAMPLE_RATE
		_playback.push_frame(Vector2(sample, sample))
