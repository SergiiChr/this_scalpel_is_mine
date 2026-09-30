class_name PatientAnimator
extends Node
## Procedural animation of the patient's skeleton, driven by the synced vitals on every peer.
## Breathing follows the respiration rate, awake patients look around, talk, flinch and panic,
## seizures shake every joint. The limb carrying the surgical site stays still so the site doesn't drift.

const BREATH_DEPTH := 0.018
const TORSO_TOP := 0.215
## How long the eyes stay shut in a blink (seconds).
const BLINK := 0.12

var body: PatientBody
var _rig: BoneRig
var _parts: Dictionary = {}
var _breath_phase := 0.0
var _beat_phase := 0.0
var _talk_left := 0.0
var _flinch := 0.0
var _jolt := 0.0
var _last_pain := 0.0
var _locked_side := ""
## Seconds until the next blink, and how much of the current one is left.
var _blink_in := 3.0
var _blink_left := 0.0


func setup(patient_body: PatientBody, model: Node3D) -> void:
	body = patient_body
	_rig = BoneRig.find(model)
	_parts = ModelSlot.parts(model, ["EyeL", "EyeR", "Lids"])
	if body.is_limb_site():
		_locked_side = "L"


## Patient said something: move the jaw for a while.
func talk(seconds: float) -> void:
	_talk_left = seconds


## Every motion is a rotation in model space (X toward the head, Y up, Z the patient's left) about the joint.
## A defibrillator shock: every muscle contracts at once for an instant.
func jolt() -> void:
	_jolt = 1.0


func animate(vitals: Vitals, alive: bool, delta: float) -> void:
	if _rig == null:
		return
	var t := Time.get_ticks_msec() * 0.001
	var breathing := alive and not vitals.is_arrested()
	var rate := (14.0 + vitals.panic * 14.0) / 60.0 if vitals.anesthesia < 0.7 else 12.0 / 60.0
	_breath_phase += delta * rate * TAU if breathing else 0.0
	var breath := (sin(_breath_phase) * 0.5 + 0.5) * BREATH_DEPTH * (1.0 + vitals.panic)
	# The whole trunk rises with the surgical site; legs, neck and arms stay put so only the torso skin moves.
	_jolt = move_toward(_jolt, 0.0, delta * 5.0)
	var rise := Vector3(0, TORSO_TOP * breath + _jolt * 0.025, 0)
	_rig.shift("Torso", rise)
	for held: String in ["ThighL", "ThighR", "Neck", "UpperArmL", "UpperArmR"]:
		_rig.hold(held, rise)
	body.set_breath_offset(TORSO_TOP * breath)
	body.breath = sin(_breath_phase) * 0.5 + 0.5 if breathing else body.breath
	body.heartbeat = _heartbeat(vitals, alive, delta)

	var awake := alive and vitals.is_awake()
	# Awake patients blink every few seconds, more often in pain or panic.
	_blink_in -= delta * (1.0 + vitals.pain + vitals.panic * 2.0) if awake else 0.0
	if _blink_in <= 0.0:
		_blink_in = randf_range(2.5, 6.0)
		_blink_left = BLINK
	_blink_left = maxf(_blink_left - delta, 0.0)
	var open := awake and _blink_left <= 0.0
	for eye in ["EyeL", "EyeR"]:
		_set_visible(eye, open)
	_set_visible("Lids", not open)
	_talk_left = maxf(_talk_left - delta, 0.0)
	var jaw_open := (absf(sin(t * 11.0)) * 0.25 if _talk_left > 0.0 else 0.0) + (0.15 if vitals.pain > 0.6 and awake else 0.0)
	# The jaw hinges about the ear-to-ear axis; a positive turn swings the chin toward the chest.
	_rig.rotate("Jaw", Basis(Vector3.BACK, jaw_open))

	if vitals.pain > _last_pain + 0.08 and awake:
		_flinch = 1.0
	_last_pain = vitals.pain
	_flinch = move_toward(_flinch, 0.0, delta * 3.0)
	# Looking left and right turns the head about its own long axis.
	var look := sin(t * 0.4) * 0.35 if awake else 0.0
	var head := Vector3(look, 0, 0) + Vector3(randf_range(-1, 1), 0, randf_range(-1, 1)) * _flinch * 0.15
	var shake := 0.0
	if vitals.seizing:
		shake = 0.25
	elif awake and vitals.panic > 0.6:
		shake = (vitals.panic - 0.6) * 0.8
	_rig.rotate("Head", Basis.from_euler(head + _jitter(shake * 0.6, t, 1.0)))
	for side: String in ["L", "R"]:
		var mirror := 1.0 if side == "L" else -1.0
		var still := side == _locked_side
		var amount := 0.0 if still else shake + _flinch * 0.2 + _jolt * 0.35
		# Panicking patients lift their arms off the table: the arms point toward the feet, so a negative turn about Z raises them.
		var lift := 0.0 if still or not awake else vitals.panic * 0.15
		_rig.rotate("UpperArm" + side, Basis.from_euler(Vector3(0, 0, -lift) + _jitter(amount, t, 2.0 + mirror)))
		_rig.rotate("Forearm" + side, Basis.from_euler(Vector3(0, 0, -lift * 0.8) + _jitter(amount, t, 3.0 + mirror)))
		_rig.rotate("Hand" + side, Basis.from_euler(_jitter(amount * 1.5, t, 4.0 + mirror)))
		_rig.rotate("Thigh" + side, Basis.from_euler(_jitter(amount * 0.4, t, 5.0 + mirror)))
		_rig.rotate("Shin" + side, Basis.from_euler(_jitter(amount * 0.4, t, 6.0 + mirror)))


## Heart contraction 0..1: a quick squeeze once per beat in sinus rhythm, a feeble quiver in V-fib, still in asystole.
func _heartbeat(vitals: Vitals, alive: bool, delta: float) -> float:
	if not alive or vitals.rhythm == Vitals.Rhythm.ASYSTOLE:
		return 0.0
	if vitals.rhythm == Vitals.Rhythm.VFIB:
		return randf() * 0.15
	_beat_phase = fmod(_beat_phase + delta * vitals.heart_rate / 60.0, 1.0)
	return pow(maxf(sin(_beat_phase * TAU), 0.0), 3.0)


func _set_visible(part: String, value: bool) -> void:
	var node: Node3D = _parts.get(part)
	if node:
		node.visible = value


static func _jitter(amount: float, t: float, seed_value: float) -> Vector3:
	if amount <= 0.0:
		return Vector3.ZERO
	return Vector3(sin(t * 37.0 + seed_value), sin(t * 29.0 + seed_value * 2.0), sin(t * 43.0 + seed_value * 3.0)) * amount
