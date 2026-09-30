extends Node3D
## Fits every tool model's grip to the glove and writes data/grips.json (see SurgeonHand.fit): for each hand, the glove
## moves off the tool (toward the back of the hand, and sideways) just until the palm clears it, then each finger opens
## or closes just until it clears too. Run it again after changing tool models, the glove or the grips:
##   godot --headless --path . res://tests/fit_grips.tscn

const GripCheck := preload("res://tests/grip_check.gd")
const OUT := "res://data/grips.json"
const LIFT_STEP := 0.003
const MAX_LIFT := 0.045
const MAX_SHIFT := 0.03
const CURL_STEP := 0.05
## A finger bent further than this folds into the palm.
const MAX_CURL := 1.3


func _ready() -> void:
	var fits: Dictionary = {"_about": "Grip fits per tool model and hand, made by tests/fit_grips.tscn. lift, shift: meters the glove moves off the tool toward the back of the hand and toward the pinky side. curl: how far each finger closes (index, middle, ring, pinky, thumb)."}
	for hand_index in 2:
		var hand := GripCheck.make_hand(self, hand_index)
		var side := "left" if hand_index == 0 else "right"
		var seen: Dictionary = {}
		for def: ToolDef in Db.tools.values():
			var model_id := def.model if def.model else def.id
			if seen.has(model_id):
				continue
			seen[model_id] = true
			var style: Array = (SurgeonHand.GRIPS.get(def.grip, SurgeonHand.GRIPS.pencil) as Dictionary).curl
			var fit := {"lift": 0.0, "shift": 0.0, "curl": style.duplicate()}
			var tool := GripCheck.hold(hand, def, fit.duplicate(true), self)
			await get_tree().physics_frame
			_fit_lift(hand, fit, "Palm")
			for f in SurgeonHand.FINGERS.size():
				_fit_finger(hand, fit, f)
			# The palm alone clear isn't always enough (the root of the thumb): move the whole glove, then the fingers again.
			if _try(hand, fit, "") > 0:
				_fit_lift(hand, fit, "")
				for f in SurgeonHand.FINGERS.size():
					_fit_finger(hand, fit, f)
			if fit.lift != 0.0 or fit.shift != 0.0 or fit.curl != style:
				var entry: Dictionary = fits.get(model_id, {})
				entry[side] = {"lift": snappedf(fit.lift, 0.001), "shift": snappedf(fit.shift, 0.001), "curl": (fit.curl as Array).map(func(c: float) -> float: return snappedf(c, 0.01))}
				fits[model_id] = entry
			print("%s %s: lift %.3f shift %.3f curl %s, still clipping %d" % [model_id, side, fit.lift, fit.shift, fit.curl, _try(hand, fit, "")])
			tool.queue_free()
			await get_tree().physics_frame
		hand.get_parent().queue_free()
	var file := FileAccess.open(OUT, FileAccess.WRITE)
	file.store_string(JSON.stringify(fits, "\t", false) + "\n")
	print("fit_grips: wrote ", OUT)
	get_tree().quit()


## The smallest move of the glove off the tool (lift toward the back of the hand, shift sideways) that gets the part
## of the glove clear of it, or failing that the one that clips least.
func _fit_lift(hand: SurgeonHand, fit: Dictionary, part: String) -> void:
	var moves: Array[Vector2] = []
	var lift := 0.0
	while lift <= MAX_LIFT + 0.0001:
		var shift := -MAX_SHIFT
		while shift <= MAX_SHIFT + 0.0001:
			moves.append(Vector2(lift, shift))
			shift += LIFT_STEP
		lift += LIFT_STEP
	moves.sort_custom(func(a: Vector2, b: Vector2) -> bool: return a.length() < b.length())
	var best := INF
	var best_move := Vector2.ZERO
	for move in moves:
		fit.lift = move.x
		fit.shift = move.y
		var clipped := _try(hand, fit, part)
		if clipped < best:
			best = clipped
			best_move = move
		if clipped == 0:
			break
	fit.lift = best_move.x
	fit.shift = best_move.y


## The curl nearest the grip's own that keeps one finger clear of the tool: opening it first, then closing it further
## (a thumb can wrap past a thick handle), or failing both the one that clips least.
func _fit_finger(hand: SurgeonHand, fit: Dictionary, finger: int) -> void:
	var curl: Array = fit.curl
	var start := float(curl[finger])
	var candidates: Array[float] = []
	for i in 30:
		for value: float in [start - CURL_STEP * i, start + CURL_STEP * i]:
			if value >= 0.0 and value <= MAX_CURL and not value in candidates:
				candidates.append(value)
	var best := INF
	var best_curl := start
	for value in candidates:
		curl[finger] = value
		var clipped := _try(hand, fit, SurgeonHand.FINGERS[finger])
		if clipped < best:
			best = clipped
			best_curl = value
		if clipped == 0:
			break
	curl[finger] = best_curl


func _try(hand: SurgeonHand, fit: Dictionary, part: String) -> int:
	# A copy each time: the hand re-poses its fingers only when its fit changes.
	hand.fit = fit.duplicate(true)
	hand.snap_pose(GripCheck.shoulder(hand))
	return GripCheck.clipped(hand, part)
