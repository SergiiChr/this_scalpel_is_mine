namespace Scalpel.World;

/// <summary>
/// Where an IV line goes into the arm: the catheter's stub going into the skin toward the elbow, its hub with a colored
/// cap and wings, a clear film over the site, two strips of woven tape across the arm (one over the hub, one holding
/// the tubing down) and the tubing taped along the arm, hanging off to the stand from <see cref="Exit"/>.
/// <para>
/// Local space (see PatientBody.IvSite()): the catheter goes in at the origin on the skin, X runs along the arm toward
/// the elbow, Y out of the skin, Z across the arm. Everything wraps around the arm as a cylinder of
/// <see cref="Build"/>'s radius.
/// </para>
/// </summary>
public partial class IvDressing : Node3D
{
    /// <summary>Where the hub sits along the arm (toward the hand, behind where the catheter goes in): from, to.
    /// </summary>
    private static readonly Vector2 Hub = new(-0.028f, -0.012f);
    private const float HubRadius = 0.0032f;
    /// <summary>Where the tubing taped along the arm ends and starts hanging (IvLine starts here).</summary>
    public static readonly Vector3 Exit = new(-0.085f, IvLine.Radius, 0f);
    /// <summary>Tape strips across the arm: from x, to x, half span across. Kept to the top of the arm: a real arm is
    /// flatter than the cylinder they wrap around, wider ones would lift off.</summary>
    private static readonly Vector3[] Tapes = [new(-0.03f, -0.019f, 0.024f), new(-0.07f, -0.057f, 0.022f)];
    private static readonly Rect2 Film = new(-0.032f, -0.018f, 0.046f, 0.036f);
    private static readonly Color TapeColor = new(0.95f, 0.95f, 0.92f);
    private static readonly Color StripeColor = new(0.25f, 0.6f, 0.6f);
    private static readonly Color CapColor = new(0.85f, 0.82f, 0.2f);
    private static readonly Color PlasticColor = new(0.88f, 0.92f, 0.94f);

    private float _radius = 0.035f;

    /// <summary>The tubing's look, shared with the hanging part (<see cref="IvLine"/>).</summary>
    public static ShaderMaterial TubingMaterial() => Materials.ToonShaded(new Color(0.82f, 0.88f, 0.9f), 0.1f, false, 0.2f);

    public void Build(float armRadius)
    {
        _radius = armRadius;
        var plastic = Materials.ToonShaded(PlasticColor, 0.05f, false, 0.2f);
        // The visible end of the catheter, going into the skin at a shallow angle.
        Add(Shapes.Tube([new(0.004f, -0.0012f, 0f), new(Hub.Y, HubRadius * 0.8f, 0f)], 0.0007f, 0.0007f), plastic);
        Add(Shapes.Tube([new(Hub.Y, HubRadius, 0f), new(Hub.X, HubRadius, 0f)], HubRadius, HubRadius), plastic);
        Add(Shapes.Tube([new(Hub.Y - 0.001f, HubRadius, 0f), new(Hub.Y - 0.005f, HubRadius, 0f)],
            HubRadius * 1.15f, HubRadius * 1.15f), Materials.ToonShaded(CapColor, 0.05f, false, 0.3f));
        Add(Patch(new Rect2(Hub.Y - 0.008f, -0.009f, 0.007f, 0.018f), 0.0005f, 0.0006f, false), plastic);
        // Taped along the arm from the hub, then off toward the stand.
        var taped = Enumerable.Range(0, 6)
            .Select(i => new Vector3(Mathf.Lerp(Hub.X, Exit.X, i / 5f), IvLine.Radius, 0f))
            .ToArray();
        Add(Shapes.Tube(taped, IvLine.Radius, IvLine.Radius), TubingMaterial());
        Add(Patch(Film, 0.0006f, 0.0002f), FilmMaterial());
        var tape = Materials.FamilyUnique("cloth", TapeColor, 0.95f);
        foreach (var strip in Tapes)
        {
            Add(Patch(new Rect2(strip.X, -strip.Z, strip.Y - strip.X, strip.Z * 2f), 0.0007f, 0.0005f), tape);
        }
        // The printed band on the tape over the hub.
        var middle = (Tapes[0].X + Tapes[0].Y) * 0.5f;
        Add(Patch(new Rect2(middle - 0.0015f, -Tapes[0].Z, 0.003f, Tapes[0].Z * 2f), 0.0013f, 0.0002f),
            Materials.ToonShaded(StripeColor, 0.1f, false, 0.9f));
    }

    private void Add(Mesh mesh, Material material) =>
        AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = material });

    /// <summary>How high whatever lies on the skin reaches under a strip at (x, z): the hub with its wings, the tubing.
    /// </summary>
    private static float Under(float x, float z)
    {
        static float Bump(float z, float radius) =>
            radius * 2f * Mathf.Sqrt(Mathf.Max(1f - Mathf.Pow(z / (radius * 1.6f), 2f), 0f));
        var height = 0f;
        if (x > Hub.X - 0.002f && x < Hub.Y + 0.002f)
        {
            height = Mathf.Max(height, Bump(z, HubRadius));
        }
        if (x < Hub.X && x > Exit.X - 0.005f)
        {
            height = Mathf.Max(height, Bump(z, IvLine.Radius));
        }
        return height;
    }

    /// <summary>A point <paramref name="lift"/> off the arm's skin at x along the arm and z across it (arc length), the
    /// arm a cylinder around X.</summary>
    private Vector3 OnArm(float x, float z, float lift)
    {
        var angle = z / _radius;
        return new Vector3(x, ((_radius + lift) * Mathf.Cos(angle)) - _radius, (_radius + lift) * Mathf.Sin(angle));
    }

    /// <summary>A thin sheet (film, tape, the catheter's wings) over <paramref name="area"/> (x along the arm, y across
    /// it), laid on the skin <paramref name="lift"/> up and, if it drapes, over what's under it,
    /// <paramref name="thickness"/> thick. Only its top is drawn: the rest faces the skin.</summary>
    private ArrayMesh Patch(Rect2 area, float lift, float thickness, bool drapes = true)
    {
        var columns = Math.Max(2, Mathf.CeilToInt(area.Size.X / 0.002f));
        var rows = Math.Max(2, Mathf.CeilToInt(area.Size.Y / 0.002f));
        var grid = new Vector3[columns + 1, rows + 1];
        for (var i = 0; i <= columns; i++)
        {
            for (var j = 0; j <= rows; j++)
            {
                var x = area.Position.X + (area.Size.X * i / columns);
                var z = area.Position.Y + (area.Size.Y * j / rows);
                grid[i, j] = OnArm(x, z, lift + thickness + (drapes ? Under(x, z) : 0f));
            }
        }
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var i = 0; i < columns; i++)
        {
            for (var j = 0; j < rows; j++)
            {
                // Facing out of the skin: Godot's front faces wind clockwise.
                foreach (var vertex in (Vector3[])[
                    grid[i, j], grid[i + 1, j], grid[i, j + 1], grid[i + 1, j], grid[i + 1, j + 1], grid[i, j + 1]])
                {
                    surface.AddVertex(vertex);
                }
            }
        }
        surface.GenerateNormals();
        return surface.Commit();
    }

    private static StandardMaterial3D FilmMaterial() => new()
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(0.94f, 0.96f, 0.98f, 0.28f),
        Roughness = 0.15f,
        MetallicSpecular = 0.7f,
    };
}
