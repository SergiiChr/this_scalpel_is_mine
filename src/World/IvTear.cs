namespace Scalpel.World;

/// <summary>
/// Where an IV catheter came out of the arm: ripped out, a ragged slit of torn skin along the arm with a raw, bloodied
/// rim around it; taken out (Patient.RemoveIv()), a small hole where it went in.
/// Same local space as the dressing it replaces (<see cref="IvDressing"/>): the catheter went in at the origin, X along
/// the arm toward the elbow, Y out of the skin.
/// It oozes until a strip of tape across it holds it shut (Patient.TapeIvTear()).
/// </summary>
public partial class IvTear : Node3D {
    /// <summary>The middle of a tear: between where the catheter went in and its hub, which tore the skin with it.
    /// </summary>
    private const float TornMiddle = -0.008f;
    /// <summary>How far the torn rim reaches to either side of the slit: a strip covers the tear when it reaches past
    /// this on both sides.</summary>
    private const float RimHalfWidth = 0.006f;
    private static readonly Color RimColor = new(0.62f, 0.2f, 0.18f);
    private static readonly Color SlitColor = new(0.36f, 0.03f, 0.04f);

    private float _radius;
    /// <summary>Along the arm from where the catheter went in.</summary>
    private float _middle;

    /// <summary>Taped shut.</summary>
    public bool Taped { get; private set; }
    /// <summary>Ripped out with the tubing, not taken out.</summary>
    public bool Torn { get; private set; }

    public void Build(float armRadius, bool torn) {
        _radius = armRadius;
        Torn = torn;
        _middle = torn ? TornMiddle : 0f;
        // Half lengths along the arm and half widths across it of the rim and the slit.
        var (rim, slit) = torn ? (new Vector2(0.014f, 0.006f), new Vector2(0.011f, 0.002f))
            : (new Vector2(0.0035f, 0.003f), new Vector2(0.0014f, 0.0012f));
        Add(Ragged(armRadius, rim.X, rim.Y, 0.0004f, 1.3f), RimColor);
        Add(Ragged(armRadius, slit.X, slit.Y, 0.0008f, 4.1f), SlitColor);
    }

    /// <summary>The point on the skin (world) <paramref name="z"/> across the arm from the tear's middle.</summary>
    internal Vector3 Across(float z) => ToGlobal(IvDressing.OnArm(_radius, _middle, z, 0f));

    /// <summary>A world point as x along the arm and z across it (arc length), on the arm's surface.</summary>
    internal Vector2 OnArm(Vector3 world) {
        var local = ToLocal(world);
        return new Vector2(local.X, _radius * Mathf.Atan2(local.Z, local.Y + _radius));
    }

    /// <summary>A strip <paramref name="halfWidth"/> wide each way, from <paramref name="a"/> to <paramref name="b"/>
    /// (<see cref="OnArm"/>), lies across the tear: its middle over the slit, reaching past the rim on both sides.
    /// </summary>
    internal bool Covers(Vector2 a, Vector2 b, float halfWidth) =>
        Mathf.Abs(((a.X + b.X) * 0.5f) - _middle) <= halfWidth * 0.5f
        && Mathf.Min(a.Y, b.Y) < -RimHalfWidth && Mathf.Max(a.Y, b.Y) > RimHalfWidth;

    /// <summary>Taped shut: a strip across the arm from <paramref name="z0"/> to <paramref name="z1"/> over the slit's
    /// middle, <paramref name="halfWidth"/> wide each way.</summary>
    public void TapeOver(float z0, float z1, float halfWidth, Color color) {
        Taped = true;
        const int Rows = 16;
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var j = 0; j < Rows; j++) {
            var (from, to) = (Mathf.Lerp(z0, z1, (float)j / Rows), Mathf.Lerp(z0, z1, (float)(j + 1) / Rows));
            Vector3 Corner(float x, float z) => IvDressing.OnArm(_radius, _middle + x, z, 0.0009f);
            // Clockwise seen from outside the skin, Godot's front faces, whichever way the strip was laid.
            var (near, far) = z1 > z0 ? (-halfWidth, halfWidth) : (halfWidth, -halfWidth);
            surface.AddVertex(Corner(near, from));
            surface.AddVertex(Corner(far, from));
            surface.AddVertex(Corner(far, to));
            surface.AddVertex(Corner(near, from));
            surface.AddVertex(Corner(far, to));
            surface.AddVertex(Corner(near, to));
        }
        surface.GenerateNormals();
        AddChild(new MeshInstance3D { Mesh = surface.Commit(), MaterialOverride = Materials.FamilyUnique("cloth", color, 0.95f) });
    }

    private void Add(Mesh mesh, Color color) =>
        AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = Materials.ToonShaded(color, 0.3f, false, 0.15f) });

    /// <summary>A flat oval on the skin, <paramref name="halfLength"/> along the arm and <paramref name="halfWidth"/>
    /// across it, <paramref name="lift"/> off it, its edge jagged: each edge point pulled in by a different amount
    /// (<paramref name="phase"/> picks the pattern).</summary>
    private ArrayMesh Ragged(float radius, float halfLength, float halfWidth, float lift, float phase) {
        const int Edge = 24;
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        var center = IvDressing.OnArm(radius, _middle, 0f, lift);
        Vector3 EdgePoint(int k) {
            var angle = Mathf.Tau * k / Edge;
            var jag = 0.7f + (0.3f * Mathf.Abs(Mathf.Sin((angle * 5f) + phase) * Mathf.Cos((angle * 3f) - phase)));
            return IvDressing.OnArm(radius, _middle + (Mathf.Cos(angle) * halfLength * jag),
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
