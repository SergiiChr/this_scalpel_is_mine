namespace Scalpel.Visuals;

/// <summary>
/// Room architecture (walls, floors), invisible colliders, 3D labels and tubes along a path (bones, veins, tubing).
/// Everything else is a model.
/// </summary>
public static class Shapes
{
    public static MeshInstance3D Slab(Node3D parent, Vector3 size, Color color, Vector3 position, float grime = 0.35f,
        Pattern pattern = Pattern.None) =>
        Slab(parent, size, Materials.RoomSurface(color, grime, pattern), position);

    public static MeshInstance3D Slab(Node3D parent, Vector3 size, Material material, Vector3 position)
    {
        var instance = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = material,
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
        var normals = new List<Vector3>();
        foreach (var path in paths)
        {
            AppendTube(vertices, normals, path, width, height, sides);
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
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>The tube's triangles with their normals: a ring vertex's points straight out of the ellipse at it
    /// (its surface normal, which is what averaging the faces round it approximates), an end cap's along the path.
    /// </summary>
    private static void AppendTube(List<Vector3> vertices, List<Vector3> normals, IReadOnlyList<Vector3> path,
        float width, float height, int sides)
    {
        var rings = new Vector3[path.Count][];
        var ringNormals = new Vector3[path.Count][];
        var alongs = new Vector3[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            var along = (path[Math.Min(i + 1, path.Count - 1)] - path[Math.Max(i - 1, 0)]).Normalized();
            var side = along.Cross(Vector3.Up).Normalized();
            if (side.LengthSquared() < 0.5f)
            {
                side = Vector3.Right;
            }
            var up = side.Cross(along).Normalized();
            alongs[i] = along;
            rings[i] = new Vector3[sides];
            ringNormals[i] = new Vector3[sides];
            for (var k = 0; k < sides; k++)
            {
                var angle = Mathf.Tau * k / sides;
                var (sin, cos) = (Mathf.Sin(angle), Mathf.Cos(angle));
                rings[i][k] = path[i] + side * cos * width + up * sin * height;
                ringNormals[i][k] = (side * (cos / width) + up * (sin / height)).Normalized();
            }
        }
        for (var i = 1; i < rings.Length; i++)
        {
            for (var k = 0; k < sides; k++)
            {
                var n = (k + 1) % sides;
                // Clockwise seen from outside: Godot's front faces.
                vertices.AddRange([rings[i - 1][k], rings[i][n], rings[i][k], rings[i - 1][k], rings[i - 1][n], rings[i][n]]);
                normals.AddRange([ringNormals[i - 1][k], ringNormals[i][n], ringNormals[i][k], ringNormals[i - 1][k], ringNormals[i - 1][n], ringNormals[i][n]]);
            }
        }
        foreach (var end in (int[])[0, rings.Length - 1])
        {
            var normal = end == 0 ? -alongs[end] : alongs[end];
            for (var k = 0; k < sides; k++)
            {
                var next = rings[end][(k + 1) % sides];
                vertices.AddRange(end == 0 ? [path[end], next, rings[end][k]] : [rings[end][k], next, path[end]]);
                normals.AddRange([normal, normal, normal]);
            }
        }
    }
}
