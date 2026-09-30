extends Node
## Plays sounds by id from data/audio.cfg. Missing files are skipped, so audio can be filled in gradually.

## Set by the local surgeon's Hard of hearing quirk.
var deaf := false
var _cache: Dictionary = {}
const CONTACT_LIMIT := 3
const CONTACT_TIMEOUT_MSEC := 320
const CONTACT_PRIORITY := {"contact_cut": 3, "saw_bone": 3, "cautery_sizzle": 3, "contact_suction": 2, "contact_swab": 1}
var _contacts: Dictionary = {}


func _process(delta: float) -> void:
	var now := Time.get_ticks_msec()
	for uid: int in _contacts.keys():
		var state: Dictionary = _contacts[uid]
		var player: AudioStreamPlayer3D = state.player
		var fresh: bool = not deaf and now - int(state.last) < CONTACT_TIMEOUT_MSEC
		var target: float = state.level if fresh else -60.0
		player.volume_db = move_toward(player.volume_db, target, delta * (90.0 if fresh else 110.0))
		if not fresh and player.volume_db <= -55.0:
			player.queue_free()
			_contacts.erase(uid)


## Host contact updates arrive at a bounded rate. Missing updates fade out automatically,
## including when a tool is released, a peer disconnects or the action changes.
func contact(uid: int, id: String, at: Vector3, strength: float) -> void:
	if deaf or not CONTACT_PRIORITY.has(id) or strength <= 0.0:
		return
	var stream := _stream(id) as AudioStreamWAV
	if stream == null:
		return
	if _contacts.has(uid) and _contacts[uid].id != id:
		(_contacts[uid].player as AudioStreamPlayer3D).queue_free()
		_contacts.erase(uid)
	if not _contacts.has(uid):
		if _contacts.size() >= CONTACT_LIMIT:
			var weakest := -1
			var rank := INF
			for key: int in _contacts:
				var other: Dictionary = _contacts[key]
				var score := float(CONTACT_PRIORITY[other.id]) * 100.0 + float(other.level)
				if score < rank:
					rank = score
					weakest = key
			if rank >= float(CONTACT_PRIORITY[id]) * 100.0:
				return
			(_contacts[weakest].player as AudioStreamPlayer3D).queue_free()
			_contacts.erase(weakest)
		var player := AudioStreamPlayer3D.new()
		var looped := stream.duplicate() as AudioStreamWAV
		looped.loop_mode = AudioStreamWAV.LOOP_FORWARD
		looped.loop_end = int(stream.get_length() * stream.mix_rate)
		player.stream = looped
		player.bus = "SFX"
		player.volume_db = -60.0
		player.position = at
		add_child(player)
		player.play()
		_contacts[uid] = {"id": id, "player": player, "last": 0, "level": -60.0}
	var state: Dictionary = _contacts[uid]
	(state.player as AudioStreamPlayer3D).position = at
	state.last = Time.get_ticks_msec()
	state.level = -29.0 + 17.0 * clampf(strength, 0.0, 1.0)


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
