namespace Scalpel.Patients;

/// <summary>
/// A node riding a forearm bone from the elbow (its origin) to the wrist, so what's stuck to a forearm (veins, an IV
/// catheter's dressing) moves with the arm.
/// </summary>
public partial class Forearm : BoneAttachment3D
{
    /// <summary>Where the wrist is, local to this node.</summary>
    public Vector3 Wrist { get; set; }
}
