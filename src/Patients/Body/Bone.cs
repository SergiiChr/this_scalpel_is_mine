namespace Scalpel.Patients;

/// <summary>A bone under the site (a rib, the breastbone, a limb bone) that tools rest on and blades grate on.
/// </summary>
public partial class Bone : StaticBody3D
{
    /// <summary>"rib", "sternum" or "bone".</summary>
    public string Kind { get; set; } = "bone";
}
