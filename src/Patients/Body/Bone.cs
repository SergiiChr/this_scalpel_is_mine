namespace Scalpel.Patients;

/// <summary>A bone under the site (a rib, the breastbone, a limb bone) that tools rest on.
/// </summary>
public partial class Bone : StaticBody3D {
    /// <summary>"rib", "sternum" or "bone".</summary>
    public string Kind { get; set; } = "bone";
    /// <summary>A limb's bone: the part of the skeleton X-ray films show that it is drawn from. Null for the others.
    /// </summary>
    internal MeshInstance3D? Part { get; set; }
}
