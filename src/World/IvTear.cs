namespace Scalpel.World;

/// <summary>
/// Where an IV catheter was ripped out of the arm: a ragged slit of torn skin along the arm with a raw, bloodied rim
/// around it. Same local space as the dressing it replaces (<see cref="IvDressing"/>): the catheter went in at the
/// origin, X along the arm toward the elbow, Y out of the skin.
/// </summary>
public partial class IvTear : Node3D {
    /// <summary>The middle of the tear: between where the catheter went in and its hub, which tore the skin with it.
    /// </summary>
    private const float Middle = -0.008f;
    private static readonly Color RimColor = new(0.62f, 0.2f, 0.18f);
    private static readonly Color SlitColor = new(0.36f, 0.03f, 0.04f);

    public void Build(float armRadius) {
        Add(Ragged(armRadius, 0.014f, 0.006f, 0.0004f, 1.3f), RimColor);
        Add(Ragged(armRadius, 0.011f, 0.002f, 0.0008f, 4.1f), SlitColor);
    }

    private void Add(Mesh mesh, Color color) =>
        AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = Materials.ToonShaded(color, 0.3f, false, 0.15f) });

    /// <summary>A flat oval on the skin, <paramref name="halfLength"/> along the arm and <paramref name="halfWidth"/>
    /// across it, <paramref name="lift"/> off it, its edge jagged: each edge point pulled in by a different amount
    /// (<paramref name="phase"/> picks the pattern).</summary>
    private static ArrayMesh Ragged(float radius, float halfLength, float halfWidth, float lift, float phase) {
        const int Edge = 24;
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        var center = IvDressing.OnArm(radius, Middle, 0f, lift);
        Vector3 EdgePoint(int k) {
            var angle = Mathf.Tau * k / Edge;
            var jag = 0.7f + (0.3f * Mathf.Abs(Mathf.Sin((angle * 5f) + phase) * Mathf.Cos((angle * 3f) - phase)));
            return IvDressing.OnArm(radius, Middle + (Mathf.Cos(angle) * halfLength * jag),
                Mathf.Sin(angle) * halfWidth * jag, lift);
        }
        for (var k = 0; k < Edge; k++) {
            // Center, then around from +X toward +Z: clockwise seen from outside the skin, Godot's front faces.
            surface.AddVertex(center);
            surface.AddVertex(EdgePoint(k));
            surface.AddVertex(EdgePoint(k + 1));
        }
        surface.GenerateNormals();
        return surface.Commit();
    }
}
