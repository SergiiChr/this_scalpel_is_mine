class_name PatientAnimator
extends Node
## Procedural animation of the patient's skeleton, driven by the synced vitals on every peer.
## Breathing follows the respiration rate, awake patients look around, talk, flinch and panic,
## seizures shake every joint. The limb carrying the surgical site stays still so the site doesn't drift.

const BREATH_DEPTH := 0.018
const TORSO_TOP := 0.215

var body: PatientBody
var _rig: BoneRig
var _parts: Dictionary = {}
var _breath_phase := 0.0
var _talk_left := 0.0
var _flinch := 0.0
var _last_pain := 0.0
var _locked_side := ""


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
func animate(vitals: Vitals, alive: bool, delta: float) -> void:
	if _rig == null:
		return
	var t := Time.get_ticks_msec() * 0.001
	var breathing := alive and not vitals.is_arrested()
	var rate := (14.0 + vitals.panic * 14.0) / 60.0 if vitals.anesthesia < 0.7 else 12.0 / 60.0
	_breath_phase += delta * rate * TAU if breathing else 0.0
	var breath := (sin(_breath_phase) * 0.5 + 0.5) * BREATH_DEPTH * (1.0 + vitals.panic)
	# The whole trunk rises with the surgical site; legs, neck and arms stay put so only the torso skin moves.
	var rise := Vector3(0, TORSO_TOP * breath, 0)
	_rig.shift("Torso", rise)
	for held: String in ["ThighL", "ThighR", "Neck", "UpperArmL", "UpperArmR"]:
		_rig.hold(held, rise)
	body.set_breath_offset(TORSO_TOP * breath)

	var awake := alive and vitals.is_awake()
	for eye in ["EyeL", "EyeR"]:
		_set_visible(eye, awake)
	_set_visible("Lids", not awake)
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
		var amount := 0.0 if still else shake + _flinch * 0.2
		# Panicking patients lift their arms off the table: the arms point toward the feet, so a negative turn about Z raises them.
		var lift := 0.0 if still or not awake else vitals.panic * 0.15
		_rig.rotate("UpperArm" + side, Basis.from_euler(Vector3(0, 0, -lift) + _jitter(amount, t, 2.0 + mirror)))
		_rig.rotate("Forearm" + side, Basis.from_euler(Vector3(0, 0, -lift * 0.8) + _jitter(amount, t, 3.0 + mirror)))
		_rig.rotate("Hand" + side, Basis.from_euler(_jitter(amount * 1.5, t, 4.0 + mirror)))
		_rig.rotate("Thigh" + side, Basis.from_euler(_jitter(amount * 0.4, t, 5.0 + mirror)))
		_rig.rotate("Shin" + side, Basis.from_euler(_jitter(amount * 0.4, t, 6.0 + mirror)))


func _set_visible(part: String, value: bool) -> void:
	var node: Node3D = _parts.get(part)
	if node:
		node.visible = value


static func _jitter(amount: float, t: float, seed_value: float) -> Vector3:
	if amount <= 0.0:
		return Vector3.ZERO
	return Vector3(sin(t * 37.0 + seed_value), sin(t * 29.0 + seed_value * 2.0), sin(t * 43.0 + seed_value * 3.0)) * amount
