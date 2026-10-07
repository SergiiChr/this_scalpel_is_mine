namespace Scalpel.Visuals;

/// <summary>
/// Poses a model's skeleton in the model's own axes (Y up, patient's head at +X), whatever way each bone points.
/// Rotations turn a bone about its joint; the motion is given as if the bone were at rest, which is how procedural
/// animation thinks ("tilt the head", "curl the finger toward the palm").
/// </summary>
public sealed class BoneRig(Skeleton3D skeleton)
{
    public Skeleton3D Skeleton { get; } = skeleton;

    /// <summary>The rig of the first skeleton under <paramref name="root"/>, null when it has none.</summary>
    public static BoneRig? Find(Node root) =>
        root.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is Skeleton3D skeleton
            ? new BoneRig(skeleton)
            : null;

    public bool Has(string bone) => Skeleton.FindBone(bone) >= 0;

    /// <summary>Turns the bone by <paramref name="turn"/> (a rotation in model space) from its rest pose, children
    /// following.</summary>
    public void Rotate(string bone, Basis turn)
    {
        var i = Skeleton.FindBone(bone);
        if (i < 0)
        {
            return;
        }
        var rest = Skeleton.GetBoneGlobalRest(i).Basis.Orthonormalized();
        var local = Skeleton.GetBoneRest(i).Basis * (rest.Inverse() * turn * rest);
        Skeleton.SetBonePoseRotation(i, local.GetRotationQuaternion());
    }

    /// <summary>Moves the bone (and everything under it) by <paramref name="offset"/> in model space from rest.</summary>
    public void Shift(string bone, Vector3 offset)
    {
        var i = Skeleton.FindBone(bone);
        if (i >= 0)
        {
            Skeleton.SetBonePosePosition(i, Skeleton.GetBoneRest(i).Origin + ParentRest(i).Inverse() * offset);
        }
    }

    /// <summary>Keeps the bone where it was while its parent is shifted by <paramref name="offset"/>.</summary>
    public void Hold(string bone, Vector3 offset) => Shift(bone, -offset);

    private Basis ParentRest(int i)
    {
        var parent = Skeleton.GetBoneParent(i);
        return parent >= 0 ? Skeleton.GetBoneGlobalRest(parent).Basis : Basis.Identity;
    }

    /// <summary>Model-space direction the bone points at rest: toward its first child, or on from its parent at the
    /// tips.</summary>
    public Vector3 Direction(string bone)
    {
        var i = Skeleton.FindBone(bone);
        var at = Skeleton.GetBoneGlobalRest(i).Origin;
        var children = Skeleton.GetBoneChildren(i);
        if (children.Length > 0)
        {
            return (Skeleton.GetBoneGlobalRest(children[0]).Origin - at).Normalized();
        }
        var parent = Skeleton.GetBoneParent(i);
        return parent >= 0 ? (at - Skeleton.GetBoneGlobalRest(parent).Origin).Normalized() : Vector3.Right;
    }
}
