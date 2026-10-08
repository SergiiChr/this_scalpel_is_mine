namespace Scalpel.Patients;

/// <summary>A vein drawn along the inside of a forearm, where a syringe draws blood or gives a drug straight into the
/// blood.</summary>
public partial class Vein : MeshInstance3D
{
    /// <summary>The vein's line, local to this mesh (which rides the forearm bone).</summary>
    public Vector3[] Line { get; set; } = [];
}
