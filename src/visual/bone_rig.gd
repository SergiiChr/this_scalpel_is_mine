class_name BoneRig
extends RefCounted
## Poses a model's skeleton in the model's own axes (Y up, patient's head at +X), whatever way each bone points.
## Rotations turn a bone about its joint; the motion is given as if the bone were at rest, which is how procedural
## animation thinks ("tilt the head", "curl the finger toward the palm").

var skeleton: Skeleton3D


static func find(root: Node) -> BoneRig:
	var found := root.find_children("*", "Skeleton3D", true, false)
	if found.is_empty():
		return null
	var rig := BoneRig.new()
	rig.skeleton = found[0] as Skeleton3D
	return rig


func has(bone: String) -> bool:
	return skeleton.find_bone(bone) >= 0


## Turns the bone by `turn` (a rotation in model space) from its rest pose, children following.
func rotate(bone: String, turn: Basis) -> void:
	var i := skeleton.find_bone(bone)
	if i < 0:
		return
	var rest := skeleton.get_bone_global_rest(i).basis.orthonormalized()
	var local := skeleton.get_bone_rest(i).basis * (rest.inverse() * turn * rest)
	skeleton.set_bone_pose_rotation(i, local.get_rotation_quaternion())


## Moves the bone (and everything under it) by `offset` in model space from rest.
func shift(bone: String, offset: Vector3) -> void:
	var i := skeleton.find_bone(bone)
	if i >= 0:
		skeleton.set_bone_pose_position(i, skeleton.get_bone_rest(i).origin + _parent_rest(i).inverse() * offset)


## Keeps the bone where it was while its parent is shifted by `offset`.
func hold(bone: String, offset: Vector3) -> void:
	shift(bone, -offset)


func _parent_rest(i: int) -> Basis:
	var parent := skeleton.get_bone_parent(i)
	return skeleton.get_bone_global_rest(parent).basis if parent >= 0 else Basis.IDENTITY


## Model-space direction the bone points at rest: toward its first child, or on from its parent at the tips.
func direction(bone: String) -> Vector3:
	var i := skeleton.find_bone(bone)
	if i < 0:
		return Vector3.RIGHT
	var at := skeleton.get_bone_global_rest(i).origin
	var children := skeleton.get_bone_children(i)
	if not children.is_empty():
		return (skeleton.get_bone_global_rest(children[0]).origin - at).normalized()
	var parent := skeleton.get_bone_parent(i)
	return (at - skeleton.get_bone_global_rest(parent).origin).normalized() if parent >= 0 else Vector3.RIGHT
