namespace Scalpel.Patients;

/// <summary>A rough box collider of one part of the patient's body (torso, an arm...), for what a tool touches.
/// </summary>
public partial class BodyPart : StaticBody3D
{
    /// <summary>"torso", "pelvis", "neck", "head", "arm_left", "leg_right"...</summary>
    public string Part { get; set; } = "";
}
