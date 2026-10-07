namespace Scalpel.Patients;

/// <summary>
/// An organ in the cavity: a soft body hands push aside and tools take hold of. Only the host simulates it, clients
/// get its position from the patient's sync.
/// </summary>
public partial class Organ : RigidBody3D
{
    /// <summary>The model it's drawn with ("bowel", "heart"...).</summary>
    public string Kind { get; set; } = "";
    /// <summary>0 lies on top, 1 under it.</summary>
    public int Layer { get; set; }
    /// <summary>"beat" follows the pulse, "breath" the breathing, "" stays still.</summary>
    public string Motion { get; set; } = "";
    /// <summary>The model's scale at rest (its size, mirrored or not).</summary>
    public Vector3 BaseScale { get; set; } = Vector3.One;
    /// <summary>Where it belongs (site space): pushed or held aside, it drifts back here.</summary>
    public Vector3 RestPosition { get; set; }

    public Node3D? Model => GetNodeOrNull<Node3D>("Model");
}
