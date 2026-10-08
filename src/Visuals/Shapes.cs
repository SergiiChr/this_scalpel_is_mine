namespace Scalpel.Visuals;

/// <summary>
/// Room architecture (walls, floors), invisible colliders, 3D labels and tubes along a path (bones, veins, tubing).
/// Everything else is a model.
/// </summary>
public static class Shapes
{
    public static MeshInstance3D Slab(Node3D parent, Vector3 size, Color color, Vector3 position, float grime = 0.35f)
    {
        var instance = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = Materials.RoomSurface(color, grime),
            Position = position,
        };
        parent.AddChild(instance);
        return instance;
    }

    public static StaticBody3D StaticBox(Node3D parent, Vector3 size, Vector3 position, uint layer = 1)
    {
        var body = new StaticBody3D { CollisionLayer = layer, CollisionMask = 0, Position = position };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        parent.AddChild(body);
        return body;
    }

    public static Label3D Label(Node3D parent, string text, Vector3 position, int size = 32)
    {
        var label = new Label3D
        {
            Text = text,
            FontSize = size,
            PixelSize = 0.001f,
            Position = position,
            Modulate = new Color(0.75f, 0.9f, 0.78f),
        };
        parent.AddChild(label);
        return label;
    }

    /// <summary>
    /// A closed tube with an elliptic cross section (width across the path, height up) along a path (local space; the
    /// cross section keeps +Y as up where it can). Fine tubes can use fewer sides.
    /// </summary>
    public static ArrayMesh Tube(IReadOnlyList<Vector3> path, float width, float height, int sides = 12) =>
        Tubes([path], width, height, sides);

    /// <summary>
    /// Several disconnected tubes in one mesh. A routed suture has multiple visible spans, but rebuilding and
    /// submitting one fine mesh is substantially cheaper than a SurfaceTool commit and MeshInstance3D for every span.
    /// </summary>
    public static ArrayMesh Tubes(IEnumerable<IReadOnlyList<Vector3>> paths, float width, float height, int sides = 12)
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var path in paths)
        {
            AppendTube(surface, path, width, height, sides);
        }
        surface.GenerateNormals();
        return surface.Commit();
    }

    private static void AppendTube(SurfaceTool surface, IReadOnlyList<Vector3> path, float width, float height, int sides)
    {
        var rings = new Vector3[path.Count][];
        for (var i = 0; i < path.Count; i++)
        {
            var along = (path[Math.Min(i + 1, path.Count - 1)] - path[Math.Max(i - 1, 0)]).Normalized();
            var side = along.Cross(Vector3.Up).Normalized();
            if (side.LengthSquared() < 0.5f)
            {
                side = Vector3.Right;
            }
            var up = side.Cross(along).Normalized();
            rings[i] = new Vector3[sides];
            for (var k = 0; k < sides; k++)
            {
                var angle = Mathf.Tau * k / sides;
                rings[i][k] = path[i] + side * Mathf.Cos(angle) * width + up * Mathf.Sin(angle) * height;
            }
        }
        for (var i = 1; i < rings.Length; i++)
        {
            for (var k = 0; k < sides; k++)
            {
                var n = (k + 1) % sides;
                // Clockwise seen from outside: Godot's front faces.
                AddVertices(surface, rings[i - 1][k], rings[i][n], rings[i][k], rings[i - 1][k], rings[i - 1][n], rings[i][n]);
            }
        }
        foreach (var end in new[] { 0, rings.Length - 1 })
        {
            for (var k = 0; k < sides; k++)
            {
                var next = rings[end][(k + 1) % sides];
                if (end == 0)
                {
                    AddVertices(surface, path[end], next, rings[end][k]);
                }
                else
                {
                    AddVertices(surface, rings[end][k], next, path[end]);
                }
            }
        }
    }

    private static void AddVertices(SurfaceTool surface, params Vector3[] vertices)
    {
        foreach (var vertex in vertices)
        {
            surface.AddVertex(vertex);
        }
    }
}
