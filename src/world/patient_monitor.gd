class_name PatientMonitor
extends Node3D
## Bedside monitor: vitals on a screen (see MonitorScreen) and a pulse oximeter beep generated in code.
## Beep pitch follows SpO2 like real monitors, so you can hear oxygen dropping without looking.

const SAMPLE_RATE := 22050.0
const BEEP_LENGTH := 0.09

var _screen: MonitorScreen
var _player: AudioStreamPlayer3D
var _playback: AudioStreamGeneratorPlayback
var _beat_timer := 0.0
var _tone_left := 0.0
var _tone_freq := 880.0
var _phase := 0.0
var _alarm_timer := 0.0


func build() -> void:
	ModelSlot.instantiate("props", "monitor", self, {"screen": Materials.glow(Color(0.02, 0.05, 0.04))})
	_build_screen()
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


## The screen is 2D, drawn in its own viewport and shown on a quad over the casing's glass.
func _build_screen() -> void:
	var viewport := SubViewport.new()
	viewport.size = Vector2i(MonitorScreen.SIZE)
	viewport.disable_3d = true
	add_child(viewport)
	_screen = MonitorScreen.new()
	viewport.add_child(_screen)
	var mat := StandardMaterial3D.new()
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.albedo_texture = viewport.get_texture()
	var quad := QuadMesh.new()
	quad.size = Vector2(0.38, 0.25)
	var glass := MeshInstance3D.new()
	glass.mesh = quad
	glass.material_override = mat
	glass.position = Vector3(0.0, 0.01, 0.0155)
	add_child(glass)


func show_lab(text: String) -> void:
	_screen.show_lab(text)


## Alarm messages for the top bar, worst first.
static func alarms(v: Vitals, alive: bool) -> PackedStringArray:
	var out := PackedStringArray()
	if v.is_arrested() or not alive:
		out.append("*** %s" % ("ASYSTOLE" if not alive else v.rhythm_name()))
	if v.spo2 < 88.0:
		out.append("** SpO2 LOW")
	if v.systolic < 80.0:
		out.append("** NIBP LOW")
	return out


func _process(delta: float) -> void:
	var patient := Surgery.current.patient
	var v := patient.vitals
	var alarm_list := alarms(v, patient.alive)
	var beat := false
	if v.heart_rate > 1.0 and v.rhythm == Vitals.Rhythm.SINUS and patient.alive:
		_beat_timer -= delta
		if _beat_timer <= 0.0:
			_beat_timer = 60.0 / v.heart_rate
			_screen.beat(v.heart_rate)
			beat = true
	_screen.tick(v, patient.alive, alarm_list, delta)
	if _playback == null or Sfx.deaf:
		return
	_schedule_tones(v, beat, not alarm_list.is_empty(), delta)
	_fill_buffer()


func _schedule_tones(v: Vitals, beat: bool, alarm: bool, delta: float) -> void:
	if v.rhythm == Vitals.Rhythm.ASYSTOLE or not Surgery.current.patient.alive:
		_tone_freq = 960.0
		_tone_left = 0.1
		return
	if beat:
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
