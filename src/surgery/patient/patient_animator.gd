class_name PatientAnimator
extends Node
## Procedural animation of the patient model's joints, driven by the synced vitals on every peer.
## Breathing follows the respiration rate, awake patients look around, talk, flinch and panic,
## seizures shake every joint. The limb carrying the surgical site stays still so the site doesn't drift.

const BREATH_DEPTH := 0.018
const TORSO_TOP := 0.215

var body: PatientBody
var _joints: Dictionary = {}
var _rest: Dictionary = {}
var _breath_phase := 0.0
var _talk_left := 0.0
var _flinch := 0.0
var _last_pain := 0.0
var _locked_side := ""


func setup(patient_body: PatientBody, model: Node3D) -> void:
	body = patient_body
	for joint: String in [
		"Torso", "Head", "Jaw", "Eyes", "Irises", "Lids", "UpperArmL", "UpperArmR", "ForearmL", "ForearmR",
		"ThighL", "ThighR", "ShinL", "ShinR", "HandL", "HandR",
	]:
		var node := model.find_child(joint, true, false) as Node3D
		if node:
			_joints[joint] = node
			_rest[joint] = node.transform
	if body.is_limb_site():
		_locked_side = "L"


## Patient said something: move the jaw for a while.
func talk(seconds: float) -> void:
	_talk_left = seconds


func animate(vitals: Vitals, alive: bool, delta: float) -> void:
	if _joints.is_empty():
		return
	var t := Time.get_ticks_msec() * 0.001
	var breathing := alive and not vitals.is_arrested()
	var rate := (14.0 + vitals.panic * 14.0) / 60.0 if vitals.anesthesia < 0.7 else 12.0 / 60.0
	_breath_phase += delta * rate * TAU if breathing else 0.0
	var breath := (sin(_breath_phase) * 0.5 + 0.5) * BREATH_DEPTH * (1.0 + vitals.panic)
	_pose("Torso", Vector3.ZERO, Vector3(1.0, 1.0 + breath, 1.0 + breath * 0.4))
	body.set_breath_offset(TORSO_TOP * breath)

	var awake := alive and vitals.is_awake()
	_set_visible("Eyes", awake)
	_set_visible("Irises", awake)
	_set_visible("Lids", not awake)
	_talk_left = maxf(_talk_left - delta, 0.0)
	var jaw_open := (absf(sin(t * 11.0)) * 0.25 if _talk_left > 0.0 else 0.0) + (0.15 if vitals.pain > 0.6 and awake else 0.0)
	_pose("Jaw", Vector3(0, 0, -jaw_open))

	if vitals.pain > _last_pain + 0.08 and awake:
		_flinch = 1.0
	_last_pain = vitals.pain
	_flinch = move_toward(_flinch, 0.0, delta * 3.0)
	var look := sin(t * 0.4) * 0.35 if awake else 0.0
	var head := Vector3(0, look, 0) + Vector3(randf_range(-1, 1), randf_range(-1, 1), 0) * _flinch * 0.15
	var shake := 0.0
	if vitals.seizing:
		shake = 0.25
	elif awake and vitals.panic > 0.6:
		shake = (vitals.panic - 0.6) * 0.8
	_pose("Head", head + _jitter(shake * 0.6, t, 1.0))
	for side: String in ["L", "R"]:
		var mirror := 1.0 if side == "L" else -1.0
		var still := side == _locked_side
		var amount := 0.0 if still else shake + _flinch * 0.2
		var lift := 0.0 if still or not awake else vitals.panic * 0.15
		_pose("UpperArm" + side, Vector3(0, 0, lift * mirror) + _jitter(amount, t, 2.0 + mirror))
		_pose("Forearm" + side, _jitter(amount, t, 3.0 + mirror) + Vector3(0, -lift * 0.8 * mirror, 0))
		_pose("Hand" + side, _jitter(amount * 1.5, t, 4.0 + mirror))
		_pose("Thigh" + side, _jitter(amount * 0.4, t, 5.0 + mirror))
		_pose("Shin" + side, _jitter(amount * 0.4, t, 6.0 + mirror))


func _pose(joint: String, euler: Vector3, scale: Vector3 = Vector3.ONE) -> void:
	var node: Node3D = _joints.get(joint)
	if node:
		var rest: Transform3D = _rest[joint]
		node.transform = Transform3D(rest.basis * Basis.from_euler(euler) * Basis.from_scale(scale), rest.origin)


func _set_visible(joint: String, value: bool) -> void:
	var node: Node3D = _joints.get(joint)
	if node:
		node.visible = value


static func _jitter(amount: float, t: float, seed_value: float) -> Vector3:
	if amount <= 0.0:
		return Vector3.ZERO
	return Vector3(sin(t * 37.0 + seed_value), sin(t * 29.0 + seed_value * 2.0), sin(t * 43.0 + seed_value * 3.0)) * amount
