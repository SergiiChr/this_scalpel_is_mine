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
    public static ArrayMesh Tube(IReadOnlyList<Vector3> path, float width, float height, int sides = 12,
        ArrayMesh? into = null) =>
        Tubes([path], width, height, sides, into);

    /// <summary>
    /// Several disconnected tubes in one mesh. A routed suture has multiple visible spans, but rebuilding and
    /// submitting one fine mesh is substantially cheaper than a mesh and MeshInstance3D for every span.
    /// <paramref name="into"/>: a mesh to refill instead of making a new one, for tubes rebuilt while the game runs.
    /// A replaced mesh keeps its GPU buffers until the garbage collector gets to its wrapper.
    /// </summary>
    public static ArrayMesh Tubes(IEnumerable<IReadOnlyList<Vector3>> paths, float width, float height, int sides = 12,
        ArrayMesh? into = null)
    {
        var vertices = new List<Vector3>();
        foreach (var path in paths)
        {
            AppendTube(vertices, path, width, height, sides);
        }
        var mesh = into ?? new ArrayMesh();
        mesh.ClearSurfaces();
        if (vertices.Count == 0)
        {
            return mesh;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = SmoothNormals(vertices);
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Per vertex, the sum of the face normals of every triangle with a corner at the same point, normalized:
    /// what SurfaceTool.GenerateNormals() gives, without a call into the engine per vertex.</summary>
    private static Vector3[] SmoothNormals(List<Vector3> vertices)
    {
        var sums = new Dictionary<Vector3, Vector3>();
        for (var i = 0; i < vertices.Count; i += 3)
        {
            var normal = new Plane(vertices[i], vertices[i + 1], vertices[i + 2]).Normal;
            for (var j = i; j < i + 3; j++)
            {
                sums[vertices[j]] = sums.GetValueOrDefault(vertices[j]) + normal;
            }
        }
        return [.. vertices.Select(vertex => sums[vertex].Normalized())];
    }

    private static void AppendTube(List<Vector3> vertices, IReadOnlyList<Vector3> path, float width, float height,
        int sides)
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
                vertices.AddRange([rings[i - 1][k], rings[i][n], rings[i][k], rings[i - 1][k], rings[i - 1][n], rings[i][n]]);
            }
        }
        foreach (var end in (int[])[0, rings.Length - 1])
        {
            for (var k = 0; k < sides; k++)
            {
                var next = rings[end][(k + 1) % sides];
                vertices.AddRange(end == 0 ? [path[end], next, rings[end][k]] : [rings[end][k], next, path[end]]);
            }
        }
    }
}
