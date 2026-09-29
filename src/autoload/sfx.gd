extends Node
## Plays sounds by id from data/audio.cfg. Missing files are skipped, so audio can be filled in gradually.

## Set by the local surgeon's Hard of hearing quirk.
var deaf := false
var _cache: Dictionary = {}


func play(id: String, at: Vector3 = Vector3.INF, bus: String = "SFX", volume_db: float = 0.0) -> void:
	var stream := _stream(id)
	if stream == null or deaf:
		return
	var player: Node
	if at == Vector3.INF:
		var flat := AudioStreamPlayer.new()
		flat.stream = stream
		flat.bus = bus
		flat.volume_db = volume_db
		player = flat
	else:
		var spatial := AudioStreamPlayer3D.new()
		spatial.stream = stream
		spatial.bus = bus
		spatial.volume_db = volume_db
		spatial.position = at
		player = spatial
	add_child(player)
	player.finished.connect(player.queue_free)
	player.call("play")


## Looping background sound (room tone, engine, street). Returns the player so the caller can stop it.
func play_loop(id: String, parent: Node, volume_db: float = -12.0) -> AudioStreamPlayer:
	var stream := _stream(id) as AudioStreamWAV
	var player := AudioStreamPlayer.new()
	player.bus = "SFX"
	player.volume_db = volume_db
	parent.add_child(player)
	if stream == null or deaf:
		return player
	var looped := stream.duplicate() as AudioStreamWAV
	looped.loop_mode = AudioStreamWAV.LOOP_FORWARD
	looped.loop_end = int(stream.get_length() * stream.mix_rate)
	player.stream = looped
	player.play()
	return player


## Optional recorded patient lines: assets/audio/voice/<trigger>_<index>.ogg
func play_voice(voice_id: String, at: Vector3) -> void:
	var path := "res://assets/audio/voice/%s.ogg" % voice_id
	if not deaf and ResourceLoader.exists(path):
		_cache[voice_id] = load(path)
		play(voice_id, at, "Voice")


func _stream(id: String) -> AudioStream:
	if not _cache.has(id):
		var path: String = Db.audio.get_value("sfx", id, "")
		_cache[id] = load(path) if not path.is_empty() and ResourceLoader.exists(path) else null
	return _cache[id]
