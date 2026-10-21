namespace Scalpel.Tests.Support;

/// <summary>How a tourniquet sits once it's on a limb.</summary>
public static class TourniquetBand {
    /// <summary>What's wrong with the band of a tourniquet wrapped around the <paramref name="limb"/> whose skin on
    /// top is at <paramref name="top"/>: it runs around the limb, which lies along the body, not over it, its inside
    /// snug on that skin. Empty when nothing is; a tourniquet with no band is wrong too.</summary>
    public static List<string> Problems(SurgicalTool tourniquet, PatientBody body, Vector3 top, string limb) {
        if (tourniquet.Band is not { } band) {
            return [$"the tourniquet doesn't wrap around the {limb} with a band"];
        }
        var problems = new List<string>();
        var axis = band.GlobalBasis.Y.Normalized();
        if (Mathf.Abs(axis.Dot(body.Root.GlobalBasis.X.Normalized())) < 0.95f) {
            problems.Add($"the tourniquet band doesn't run around the {limb} (axis {axis})");
        }
        var center = band.GlobalPosition;
        if (center.Y > top.Y - 0.03f) {
            problems.Add($"the tourniquet lies on top of the {limb} instead of around it (center {center.Y:0.000}, skin on top {top.Y:0.000})");
        }
        // Snug: the band's inside passes just over the top of the limb.
        var inside = center.Y + ((TorusMesh)band.Mesh).InnerRadius;
        if (Mathf.Abs(inside - top.Y) > 0.015f) {
            problems.Add($"the tourniquet band isn't snug on the {limb} (inside at {inside:0.000}, skin at {top.Y:0.000})");
        }
        return problems;
    }
}
